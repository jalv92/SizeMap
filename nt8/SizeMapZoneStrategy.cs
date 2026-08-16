// SizeMapZoneStrategy — trades the liquidity zones SizeMap's engine finds, trend-aligned on
// SMA 20/50/200, LIMIT entries scaled out 1:3 / 1:4 / 1:5 with a trailed runner.
//
// This file duplicates the depth-adapter pattern from SizeMapHeat.cs (its own BookMirror +
// WallTracker, its own OnMarketDepth/OnMarketData, the same three-branch Replay-clock guard) —
// deliberately, not by sharing state with the indicator: two separate NinjaScript objects on the
// same chart cannot safely share one engine instance, and SizeMapHeat.cs already documents why
// each of these guards exists. Read it before changing this file.
//
// THREADING CONTRACT (identical split to SizeMapHeat.cs):
//   depth thread    = OnMarketDepth / OnMarketData. Owns _book, _tracker, _lastEngineRun,
//                      _lastWallRun. Publishes _nodes/_nowTicks/_epochs via Volatile. NEVER calls
//                      an order method — that is the one rule every crash on this machine traces
//                      back to breaking.
//   strategy thread = OnBarUpdate / OnExecutionUpdate / OnOrderUpdate (NT8's own serialized
//                      strategy processing, not the shared instrument thread). Owns every
//                      zone/order/CSV field below. Reads _nodes/_nowTicks/_epochs via Volatile,
//                      never touches _engineLock.
//
// REAL-TIME / MARKET REPLAY ONLY. OnMarketDepth never fires on historical bars (same limitation
// BigPrintsStrategy.cs documents for OnMarketData) — State.DataLoaded refuses cleanly with a
// Print when IsInStrategyAnalyzer is true, and every hot method re-checks it defensively.
//
// Queue position is invisible in NinjaScript — there is no MBO here, only MBP (BookMirror is a
// positional ladder mirror, see engine/BookMirror.cs). A limit resting at a level that already
// has size in front of it may never fill even though price prints through that price on the
// tape. That is a property of the platform, not of this file.

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using SizeMap.Engine;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class SizeMapZoneStrategy : Strategy
    {
        private enum TrendState { None, Long, Short }

        private struct ZoneMemory { public double MaxDistanceTicks; }

        private struct ArmSnapshot
        {
            public DateTime Time;
            public double ZonePrice;
            public long ZonePeakSize;
            public long RestingSizeAtTouch;
            public double DepartureTicksAtArm;
            public string SmaState;
            // B6: captured off the armed RadarNode so the CSV can separate "the wall was there
            // and I was adverse-selected" from "the wall had been gone for four minutes" -- the
            // question this Replay test exists to answer.
            public bool InWindow;
            public double AgeSeconds;
            public double Confidence;
            public NodeState State;
            public NodeState RawState;
            public long FirstSeenTicks;
            public double EfficiencyRatio;
        }

        // A row is written once a leg's outcome is fully known (filled-then-exited, or
        // never-filled-cancelled/rejected). Volume here is a handful of rows per trade cycle, not
        // hundreds of events/sec like the raw depth tape SizeMapRecorder.cs writes — so a
        // Task.Run-per-row with one lock is the right size of solution.
        // ponytail: not SizeMapRecorder's ring+drain-thread pattern. That exists to survive
        // ~400 events/sec without blocking the depth thread; this writes on the order of one row
        // per scale-out leg. Upgrade path — borrow that ring if this ever becomes a hot path.
        private sealed class DecisionLog
        {
            private readonly object _lock = new object();
            private readonly string _path;
            // B6: independent Task.Run calls race the thread pool -- the lock stopped writes from
            // interleaving mid-append, but did nothing to keep them in CALL order, so a later leg's
            // row could land above an earlier one and the file reads out of chronological order.
            // Chaining off the previous write's Task keeps this the same fire-and-forget shape (still
            // off the strategy thread, still not SizeMapRecorder's ring+drain pattern -- see the class
            // comment above) while forcing each append to happen only after the one before it.
            private Task _tail = Task.CompletedTask;

            // Rows are written off the strategy thread through a chain, so at shutdown there can be
            // queued work with nothing left to run it. Bounded because a hung disk must not hold up
            // NinjaTrader's own teardown -- a lost row is bad, a wedged platform is worse.
            public void Drain(int ms)
            {
                try { _tail.Wait(ms); } catch { }
            }
            public DecisionLog(string path) { _path = path; }
            public void WriteRow(string line)
            {
                string path = _path;
                lock (_lock)
                {
                    _tail = _tail.ContinueWith(_ =>
                    {
                        try { File.AppendAllText(path, line + Environment.NewLine); }
                        catch (Exception) { }   // logging must never be worse than the trade it describes
                    }, TaskScheduler.Default);
                }
            }
        }

        private const string CsvHeader =
            "timestamp,signal,side,zone_price,zone_peak_size,resting_size_at_touch,departure_ticks," +
            "sma_state,limit_price,filled,fill_price,mae_ticks,mfe_ticks,targets_hit,exit_reason,pnl_ticks," +
            "in_window,age_seconds,confidence,state,raw_state,first_seen_ticks,exit_timestamp,holding_time_sec," +
            // Logged on EVERY row, including when the gate is switched off (MinEfficiencyRatio = 0),
            // because the rows the gate would have rejected are the ones that say whether the
            // threshold is set right. 0.30 is a guess until this column has a few sessions in it.
            "efficiency_ratio";

        private const int FixedTargetLegs = 3;               // TP1/TP2/TP3 -- everything past this is a runner

        // Ported from SizeMapHeat.cs MaybeRunEngine/HandleReplayReset — same three numbers, same
        // ordering-is-the-point reasoning. Collapsed to one cadence tier: this file has no
        // rendering-resolution reason for SizeMapHeat's separate 50 Hz outer throttle, only the
        // 250 ms wall-engine gate, which stays because it is what makes the cost survivable (see
        // SizeMapHeat.cs's own measurement: 5x the call rate costs 12.9x the CPU).
        private const double ReplayResetBackwardMs = 2000;
        private const double ReplayResetForwardMs = 60000;
        private const double WallEngineIntervalMs = 250;

        private static readonly TimeSpan TradeRetention = TimeSpan.FromSeconds(1500);   // same as SizeMapHeat.cs

        // ================= depth thread — everything here under _engineLock =================
        private readonly object _engineLock = new object();
        private BookMirror _book;
        private WallTracker _tracker;
        private RadarConfig _radarCfg;
        private double _tick = 0.25;
        private DateTime _lastEngineRun = DateTime.MinValue;
        private DateTime _lastWallRun = DateTime.MinValue;

        // depth thread -> strategy thread. Only these, only through Volatile, never a lock the
        // strategy thread waits on.
        private long _nowTicks;
        private int _epochs;
        private IReadOnlyList<RadarNode> _nodes;

        // ================= strategy thread only =================
        private int _lastSeenEpoch;

        private readonly Dictionary<long, ZoneMemory> _zoneMemory = new Dictionary<long, ZoneMemory>();

        private bool _cycleActive;
        private bool _armedIsLong;
        private double _armedPrice;
        private ArmSnapshot _armSnapshot;
        private Order[] _entryOrders;
        private int _workingEntryCount;
        private bool _cancelPending;

        private readonly Dictionary<string, int> _legIndexBySignal = new Dictionary<string, int>();
        private readonly HashSet<string> _openLegs = new HashSet<string>();
        private readonly List<string> _runnerSignals = new List<string>();
        private readonly Dictionary<string, double> _fillPrice = new Dictionary<string, double>();
        private readonly Dictionary<string, DateTime> _fillTime = new Dictionary<string, DateTime>();   // B6: holding_time_sec
        private readonly Dictionary<string, double> _maeTicks = new Dictionary<string, double>();
        private readonly Dictionary<string, double> _mfeTicks = new Dictionary<string, double>();
        private readonly Dictionary<string, bool> _stopIsBreakeven = new Dictionary<string, bool>();
        // M2/exit_reason: set once MaintainRunnerTrail's zone-based trail actually moves a leg's
        // stop, so a trailed exit can be told apart from a straight loser at the untouched initial
        // stop -- both used to write exit_reason "Stop" (reviewer finding).
        private readonly HashSet<string> _stopTrailed = new HashSet<string>();
        private readonly Dictionary<string, bool> _stopRetried = new Dictionary<string, bool>();
        private readonly List<string> _targetsHitThisCycle = new List<string>();

        private bool _beArmed;
        private double _runnerStopPrice;

        private SMA _sma20, _sma50, _sma200;
        private DecisionLog _log;

        // Range filter. _closeBuf is reused every bar so the gate allocates nothing; _efficiency is
        // the last value computed, captured into the arm snapshot. _efficiencyState is -1 until the
        // first evaluation so the very first verdict prints -- otherwise a session that starts in
        // chop looks identical to a session where the strategy never loaded.
        private double[] _closeBuf;
        private double _efficiency;
        private int _efficiencyState = -1;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "SizeMapZoneStrategy";
                Description = "Enters at SizeMap liquidity zones, trend-aligned on SMA 20/50/200. Needs live market depth (OnMarketDepth never fires on historical bars) -- Market Replay or a live/Sim connection only.";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.UniqueEntries;   // 4 distinct signals open at once, 1 entry each
                IsFillLimitOnTouch = false;
                BarsRequiredToTrade = 200;                     // SMA200 warmup

                // M2: all five below are user-editable in the strategy dialog whether or not this
                // file sets them, so writing them out is about pinning the STARTING value, not
                // locking the UI -- same reasoning PatternZoneStrategy.cs uses for the one it
                // already declares (a platform default change, or an accidental dropdown click,
                // can't move an implicit choice silently).
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                // Per-signal SetStopLoss/SetProfitTarget brackets ARE the scale-out architecture.
                // StopTargetHandling.ByStrategyPosition collapses every leg's bracket into ONE
                // strategy-wide stop and ONE target -- no compile error, no runtime warning, just
                // the whole TP1/TP2/TP3/runner design silently gone.
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                // RealtimeErrorHandling stays at NT8's own default (StopCancelClose), same choice
                // and same reasoning as PatternZoneStrategy.cs: the platform is the outer net, this
                // file's own reject-retry (see OnOrderUpdate) is the inner one -- and the inner one
                // has a documented gap, it gives up and leaves a leg genuinely unprotected (just a
                // Print) if the one resubmit also fails. B1's ClampStopToMarket removes the specific
                // cause that fired live (a stop handed to NT8 at/through the market), so keeping
                // StopCancelClose is not "the same crash will recur" -- it is keeping a backstop for
                // whatever a clamped, tighten-only stop write can still lose a genuine race against
                // (the market moving between the price being computed and NT8 validating the order).
                // IgnoreAllErrors would require this file to self-manage that gap completely, which
                // the existing retry-once does not.
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                // No custom reconnect/flatten policy: a transient connection blip mid-cycle (up to
                // ContractCount resting/working legs) should not itself force an exit. B2's
                // reconciliation check in OnBarUpdate is what actually recovers the cycle if a
                // disconnect (or anything else) flattens the position out from under this file's own
                // per-leg bookkeeping.
                ConnectionLossHandling = ConnectionLossHandling.KeepRunning;

                DepartureTicks = 8;
                ArmingDistanceTicks = 4;
                RangeFilterPeriod = 40;
                MinEfficiencyRatio = 0.30;
                RequirePriceBeyondSma20 = false;
                StopLossTicks = 8;
                Tp1RMultiple = 3.0;
                Tp2RMultiple = 4.0;
                Tp3RMultiple = 5.0;
                ContractCount = 4;
                MoveToBreakEvenAfterTp1 = true;
                WallSizeMultiple = 1.5;   // ES-measured (see engine/RadarConfig.cs) -- raise toward 4.0 on NQ
                MinWallLots = 40;
                // B6: OFF by default so the first Replay run measures the entry rule exactly as
                // specified -- ON is there so a LATER run can test whether requiring a fresher/more
                // confident wall would have helped, using the InWindow/AgeSeconds/Confidence columns
                // this pass adds to the CSV.
                RequireFreshWall = false;
                MinConfidenceToArm = 0.3;
            }
            else if (State == State.DataLoaded)
            {
                if (IsInStrategyAnalyzer)
                {
                    Print("[SizeMapZone] refusing to run in the Strategy Analyzer -- OnMarketDepth never fires on historical bars, so this strategy cannot see zones there. Use Market Replay or a live/Sim connection.");
                    return;
                }

                _tick = TickSize > 0 ? TickSize : 0.25;
                _radarCfg = new RadarConfig { TickSize = _tick, K_mult = WallSizeMultiple, MinAbsSize = MinWallLots };
                _book = new BookMirror(_tick, TradeRetention);
                _tracker = new WallTracker(_radarCfg);
                SeedFromSnapshot();

                _sma20 = SMA(20);
                _sma50 = SMA(50);
                _sma200 = SMA(200);

                _entryOrders = new Order[ContractCount];
                _closeBuf = new double[Math.Max(2, RangeFilterPeriod)];

                // ponytail: filename date is DateTime.Now (wall clock), not the bar/market time
                // SizeMapRecorder.cs uses. That file needs market-time rollover because it spans a
                // firehose that can cross session boundaries mid-Replay; this is a handful of rows
                // written on the day Javier actually ran the session, which is what he will look
                // for. Upgrade path -- switch to the first bar's Time[0] if that ever matters.
                string dir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "SizeMap");
                Directory.CreateDirectory(dir);
                string stem = Path.Combine(dir, "zone-decisions-" + Sanitize(Instrument.MasterInstrument.Name) + "-" + DateTime.Now.ToString("yyyyMMdd"));
                string path = PathForCurrentSchema(stem);
                if (!File.Exists(path)) File.WriteAllText(path, CsvHeader + Environment.NewLine);
                _log = new DecisionLog(path);
            }
            else if (State == State.Terminated)
            {
                // OnBarUpdate's reconciliation cannot cover this. RealtimeErrorHandling.StopCancelClose
                // disables the strategy as part of the same cascade that flattens -- there is no next
                // bar -- and that is exactly the path that fired on the first live run. A user clicking
                // "disable" mid-cycle is the same shape. Terminated fires in every shutdown case, so it
                // is a strict superset; OnBarUpdate keeps its own check because it reconciles while the
                // session is still alive rather than waiting for teardown.
                //
                // The row IS the deliverable here: this strategy cannot be backtested (OnMarketDepth
                // never fires on historical bars), so Replay plus this CSV is its only validation path.
                // A trade that happened and left no row is a Replay day spent for nothing.
                try { if (_cycleActive) EndCycle(reconciled: true); } catch (Exception ex) { Print("[SizeMapZone] terminate reconcile failed: " + ex.Message); }
                if (_log != null) _log.Drain(2000);
            }
        }

        // ================= INSTRUMENT DEPTH THREAD =================
        // No order call anywhere below this header. Depth updates the engine; orders are
        // submitted only from OnBarUpdate/OnExecutionUpdate/OnOrderUpdate (see the file header).

        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            if (IsInStrategyAnalyzer || _book == null) return;

            if (e.IsReset)
            {
                DepthEvent rs = new DepthEvent { IsReset = true, Time = e.Time };
                lock (_engineLock)
                {
                    if (e.Instrument != Instrument) return;
                    _book.ApplyDepth(rs);
                }
                return;
            }
            if (e.Price <= 0) return;

            DepthOp op = e.Operation == Operation.Add ? DepthOp.Add
                       : e.Operation == Operation.Update ? DepthOp.Update
                                                          : DepthOp.Remove;
            DepthEvent de = new DepthEvent
            {
                Side = e.MarketDataType == MarketDataType.Ask ? Side.Ask : Side.Bid,
                Op = op,
                Position = e.Position,
                Price = e.Price,
                Volume = e.Volume,
                Time = e.Time,
                IsReset = false
            };

            lock (_engineLock)
            {
                if (e.Instrument != Instrument) return;
                _book.ApplyDepth(de);
                Volatile.Write(ref _nowTicks, e.Time.Ticks);
                MaybeRunEngine(e.Time);
            }
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (IsInStrategyAnalyzer || _book == null) return;
            if (e.MarketDataType != MarketDataType.Last || e.Price <= 0) return;

            TradeEvent te = new TradeEvent { Price = e.Price, Volume = e.Volume, Time = e.Time };
            lock (_engineLock)
            {
                if (e.Instrument != Instrument) return;
                _book.ApplyTrade(te);
                Volatile.Write(ref _nowTicks, e.Time.Ticks);
                MaybeRunEngine(e.Time);
            }
        }

        // Ported from SizeMapHeat.cs MaybeRunEngine. Javier develops entirely in Market Replay, so
        // the clock-discontinuity guard is not an edge case -- without it the first drag of the
        // Playback slider freezes the wall census and reads as a broken strategy.
        private void MaybeRunEngine(DateTime now)
        {
            if (_lastEngineRun != DateTime.MinValue)
            {
                double dMs = (now - _lastEngineRun).TotalMilliseconds;
                if (dMs < -ReplayResetBackwardMs || dMs > ReplayResetForwardMs)
                    HandleReplayReset(now);
                else if (dMs < 0) { _lastEngineRun = now; return; }
                else if (dMs < WallEngineIntervalMs) return;
            }
            _lastEngineRun = now;

            if (_lastWallRun == DateTime.MinValue || (now - _lastWallRun).TotalMilliseconds >= WallEngineIntervalMs)
            {
                _lastWallRun = now;
                _tracker.Update(_book, now);
                Volatile.Write(ref _nodes, _tracker.GetSnapshot(now));
            }
        }

        private void HandleReplayReset(DateTime now)
        {
            _book = new BookMirror(_tick, TradeRetention);
            SeedFromSnapshot();
            _tracker = new WallTracker(_radarCfg);
            _lastWallRun = DateTime.MinValue;
            Volatile.Write(ref _nodes, null);
            Volatile.Write(ref _epochs, Volatile.Read(ref _epochs) + 1);
            // Deliberately does NOT touch zone memory or any order/position bookkeeping -- those
            // are strategy-thread-owned (see OnBarUpdate's epoch check) and a live order or open
            // position must never be abandoned just because the depth engine rebuilt its book.
        }

        private void SeedFromSnapshot()
        {
            try
            {
                if (Instrument == null || Instrument.MarketDepth == null) return;
                List<DepthLevel> bids = new List<DepthLevel>();
                List<DepthLevel> asks = new List<DepthLevel>();
                for (int i = 0; i < Instrument.MarketDepth.Bids.Count; i++)
                    bids.Add(new DepthLevel { Price = Instrument.MarketDepth.Bids[i].Price, Volume = Instrument.MarketDepth.Bids[i].Volume });
                for (int i = 0; i < Instrument.MarketDepth.Asks.Count; i++)
                    asks.Add(new DepthLevel { Price = Instrument.MarketDepth.Asks[i].Price, Volume = Instrument.MarketDepth.Asks[i].Volume });
                _book.ResetFromSnapshot(bids, asks);
            }
            catch (Exception) { }   // no snapshot yet is normal; not a reason to fail construction
        }

        // ================= STRATEGY THREAD =================

        protected override void OnBarUpdate()
        {
            if (IsInStrategyAnalyzer) return;
            if (CurrentBar < BarsRequiredToTrade) return;
            // M1: IsInStrategyAnalyzer only catches the Strategy Analyzer window. A live chart or
            // Market Replay still runs a State.Historical warmup pass before the real-time
            // transition, and NT8 fills an order submitted there as a backtest fill -- a phantom
            // position at the transition plus phantom CSV rows. BigPrintsStrategy.cs and
            // PatternZoneStrategy.cs both guard entries against this; this file never did.
            if (State != State.Realtime) return;

            int epoch = Volatile.Read(ref _epochs);
            if (epoch != _lastSeenEpoch)
            {
                _lastSeenEpoch = epoch;
                _zoneMemory.Clear();
                Print("[SizeMapZone] engine epoch changed (Replay rewind/rebuild) -- zone memory reset.");
            }

            IReadOnlyList<RadarNode> nodes = Volatile.Read(ref _nodes);
            UpdateZoneMemory(nodes);

            if (_cycleActive)
            {
                if (_workingEntryCount > 0) MaintainArmedZone();
                if (_runnerSignals.Count > 0) MaintainRunnerTrail(nodes);
                UpdateOpenLegExcursion();

                if (_workingEntryCount == 0 && _openLegs.Count == 0)
                {
                    EndCycle();
                }
                else if (Position.MarketPosition == MarketPosition.Flat && !AnyEntryOrderWorking())
                {
                    // B2: ground-truth safety net. EndCycle() above is reachable only when this
                    // file's OWN per-leg bookkeeping reaches zero -- but exit-on-session-close
                    // (every session), StopCancelClose's flatten+terminate (a "Close" order, not
                    // "Stop loss"/"Profit target", so OnExecutionUpdate's exit branch never sees
                    // it -- this is what just fired live), a manual DOM flatten, or disabling the
                    // strategy all bypass that per-leg path. Left unreconciled, _cycleActive stays
                    // true forever: no new zone ever arms again, and the trade this cycle just had
                    // never gets its CSV row -- indistinguishable from "no signals today". Reconcile
                    // against the platform's own truth instead of trusting a counter that can leak.
                    EndCycle(reconciled: true);
                }
                return;   // never look for a new zone mid-cycle
            }

            if (Position.MarketPosition != MarketPosition.Flat) return;   // max-position guard

            // Range filter BEFORE the trend check, and separate from it, because they fail for
            // opposite reasons: ComputeTrend says "the averages disagree", this says "the averages
            // agree but the market is not going anywhere". Conflating them would hide the second
            // case behind the first in the Output window.
            bool trending = MarketIsTrending();

            TrendState trend = ComputeTrend();
            if (trend == TrendState.None) return;
            if (!trending) return;

            RadarNode? candidate = FindArmableZone(nodes, trend);
            if (candidate != null) ArmZone(candidate.Value, trend);
        }

        // Long only when SMA20 > SMA50 > SMA200, short only when SMA20 < SMA50 < SMA200 -- see
        // the RequirePriceBeyondSma20 property for the exact, documented definition Javier reads
        // in the strategy dialog.
        private TrendState ComputeTrend()
        {
            double s20 = _sma20[0], s50 = _sma50[0], s200 = _sma200[0];
            bool bullStack = s20 > s50 && s50 > s200;
            bool bearStack = s20 < s50 && s50 < s200;
            if (bullStack && (!RequirePriceBeyondSma20 || Close[0] > s20)) return TrendState.Long;
            if (bearStack && (!RequirePriceBeyondSma20 || Close[0] < s20)) return TrendState.Short;
            return TrendState.None;
        }

        // The range filter. A stacked SMA is a statement about three averages, not about whether
        // the market is travelling -- and it orders itself cleanly at the extremes of a range,
        // which is the worst place a trend-continuation entry can be. On 2026-08-16 that armed a
        // short 5 ticks off the low of an hour-long 22-point band. TrendQuality.EfficiencyRatio
        // asks the question the stack cannot: net displacement over the path walked to get there.
        //
        // ALWAYS computes, even with the gate switched off, because efficiency_ratio goes into
        // every CSV row and the rows this gate would have rejected are exactly the ones that say
        // whether 0.30 is the right cut. Turning the filter off must not blind the measurement
        // that replaces the guess.
        private bool MarketIsTrending()
        {
            int count = Math.Min(_closeBuf.Length, CurrentBar + 1);
            for (int i = 0; i < count; i++) _closeBuf[i] = Close[i];
            _efficiency = TrendQuality.EfficiencyRatio(_closeBuf, count);

            if (MinEfficiencyRatio <= 0) return true;

            bool trending = _efficiency >= MinEfficiencyRatio;

            // Printed on transitions only. Per-bar would be 120 lines an hour of noise in the
            // window Javier watches for fills; never printing would make a filtered-out session
            // indistinguishable from a session where nothing set up.
            int state = trending ? 1 : 0;
            if (state != _efficiencyState)
            {
                _efficiencyState = state;
                Print(string.Format("[SizeMapZone] {0} -- efficiency {1:0.000} vs {2:0.000} over {3} bars.",
                    trending ? "trending, arming enabled" : "RANGE -- arming suspended",
                    _efficiency, MinEfficiencyRatio, count));
            }
            return trending;
        }

        // (a) "was promoted as a wall" is satisfied by mere presence in this snapshot:
        // LiquidityMemory only ever holds nodes that passed WallDetector.IsConfirmed at least once
        // (WallTracker.Promote). No further State/RawState filter is layered on top -- a level
        // that later resolved Pulled or Consumed is still a node this engine once confirmed, and
        // zone_price/zone_peak_size/resting_size_at_touch go into the CSV precisely so the forward
        // test can bucket by what actually happened afterward, rather than the strategy having
        // pre-decided it.
        private void UpdateZoneMemory(IReadOnlyList<RadarNode> nodes)
        {
            if (nodes == null) return;
            double price = Close[0];
            for (int i = 0; i < nodes.Count; i++)
            {
                RadarNode n = nodes[i];
                // (b)/facing: only the side that faces price matters -- a bid wall below price is
                // a long candidate, an ask wall above is a short candidate. Distance is measured on
                // the bar CLOSE, matching Calculate.OnBarClose for every other decision in this
                // file; a wick through the zone within one bar is not what arms or un-arms it.
                bool facing = (n.Side == Side.Bid && n.Price < price) || (n.Side == Side.Ask && n.Price > price);
                if (!facing) continue;

                double distNow = Math.Abs(price - n.Price) / _tick;
                long key = ZoneKey(n.Side, n.Price);
                ZoneMemory mem;
                _zoneMemory.TryGetValue(key, out mem);
                if (distNow > mem.MaxDistanceTicks) mem.MaxDistanceTicks = distNow;
                _zoneMemory[key] = mem;
            }
        }

        // (b) departed at least DepartureTicks, (c) now back within ArmingDistanceTicks. Closest
        // qualifying zone wins ties -- a documented default where the spec left the choice open.
        private RadarNode? FindArmableZone(IReadOnlyList<RadarNode> nodes, TrendState trend)
        {
            if (nodes == null) return null;
            bool isLong = trend == TrendState.Long;
            double price = Close[0];
            double bestDist = double.MaxValue;
            RadarNode? best = null;

            for (int i = 0; i < nodes.Count; i++)
            {
                RadarNode n = nodes[i];
                if (isLong ? n.Side != Side.Bid : n.Side != Side.Ask) continue;
                bool facing = isLong ? n.Price < price : n.Price > price;
                if (!facing) continue;

                long key = ZoneKey(n.Side, n.Price);
                ZoneMemory mem;
                if (!_zoneMemory.TryGetValue(key, out mem) || mem.MaxDistanceTicks < DepartureTicks) continue;

                // B6: OFF by default -- ships alongside the CSV columns needed to answer this
                // question from Replay data instead of guessing. Never filters the departure/arming
                // logic above; only narrows WHICH already-qualifying zone is allowed to arm.
                if (RequireFreshWall && (!n.InWindow || n.Confidence < MinConfidenceToArm)) continue;

                double distNow = Math.Abs(price - n.Price) / _tick;
                if (distNow > ArmingDistanceTicks) continue;

                if (distNow < bestDist) { bestDist = distNow; best = n; }
            }
            return best;
        }

        private long ZoneKey(Side side, double price)
        {
            // Same key scheme as WallTracker/LiquidityMemory (engine/WallTracker.cs Key()) --
            // duplicated rather than exposed, since it is one line and the engine's Key() is
            // private by design.
            return ((long)Math.Round(price / _tick)) * 2 + (side == Side.Ask ? 1 : 0);
        }

        // Places one LIMIT order per contract, all at the zone price, each its own entry signal so
        // each gets its own SetStopLoss/SetProfitTarget bracket -- the managed-order pattern for
        // scaled exits (distinct signal names), far less error-prone than hand-rolling partial
        // exits off one big fill. Queue position is invisible here (no MBO, see the file header):
        // a limit at a level with resting size in front of it may simply never fill.
        private void ArmZone(RadarNode zone, TrendState trend)
        {
            bool isLong = trend == TrendState.Long;
            double limitPrice = Instrument.MasterInstrument.RoundToTickSize(zone.Price);
            long key = ZoneKey(zone.Side, zone.Price);
            ZoneMemory mem;
            _zoneMemory.TryGetValue(key, out mem);

            _cycleActive = true;                 // in-flight flag BEFORE any Enter* call below
            _armedIsLong = isLong;
            _armedPrice = limitPrice;
            _targetsHitThisCycle.Clear();
            _legIndexBySignal.Clear();

            _armSnapshot = new ArmSnapshot
            {
                Time = Time[0],
                ZonePrice = limitPrice,
                ZonePeakSize = zone.PeakSize,
                RestingSizeAtTouch = zone.LastKnownSize,
                DepartureTicksAtArm = mem.MaxDistanceTicks,
                SmaState = isLong ? "20>50>200" : "20<50<200",
                InWindow = zone.InWindow,
                AgeSeconds = zone.AgeSeconds,
                Confidence = zone.Confidence,
                State = zone.State,
                RawState = zone.RawState,
                FirstSeenTicks = zone.FirstSeenTicks,
                EfficiencyRatio = _efficiency
            };

            double[] rMultiples = { Tp1RMultiple, Tp2RMultiple, Tp3RMultiple };
            int fixedLegs = Math.Min(ContractCount, FixedTargetLegs);

            for (int i = 0; i < ContractCount; i++)
            {
                string sig = LegSignal(isLong, i);
                _legIndexBySignal[sig] = i;
                _workingEntryCount++;             // BEFORE the Enter* call -- order-event race

                SetStopLoss(sig, CalculationMode.Ticks, StopLossTicks, false);
                if (i < fixedLegs)
                    SetProfitTarget(sig, CalculationMode.Ticks, StopLossTicks * rMultiples[i]);
                // else: the runner. No SetProfitTarget call at all -- no fixed target, by design.

                _entryOrders[i] = isLong
                    ? EnterLongLimit(0, true, 1, limitPrice, sig)
                    : EnterShortLimit(0, true, 1, limitPrice, sig);
            }

            Print(string.Format("[SizeMapZone] armed {0} x{1} @ {2} (peak {3}, resting {4}, departed {5:0.0}t, {6})",
                isLong ? "LONG" : "SHORT", ContractCount, limitPrice, zone.PeakSize, zone.LastKnownSize,
                mem.MaxDistanceTicks, _armSnapshot.SmaState));
        }

        private string LegSignal(bool isLong, int i)
        {
            string side = isLong ? "L" : "S";
            if (i == 0) return "SmzZone_" + side + "_Tp1";
            if (i == 1) return "SmzZone_" + side + "_Tp2";
            if (i == 2) return "SmzZone_" + side + "_Tp3";
            int runnerIdx = i - FixedTargetLegs + 1;
            return "SmzZone_" + side + "_Runner" + (runnerIdx > 1 ? runnerIdx.ToString() : "");
        }

        // Cancel unfilled legs if price leaves the arming band (either back out, or straight
        // through without filling) or if the trend flips. Already-filled legs are untouched --
        // CancelOrder on a terminal-state order is a no-op, so this loop naturally only cancels
        // what is still working.
        private void MaintainArmedZone()
        {
            double price = Close[0];
            TrendState trend = ComputeTrend();
            bool trendOk = _armedIsLong ? trend == TrendState.Long : trend == TrendState.Short;
            double distTicks = Math.Abs(price - _armedPrice) / _tick;
            bool withinBand = distTicks <= ArmingDistanceTicks;

            if (trendOk && withinBand) return;
            if (_cancelPending) return;
            _cancelPending = true;   // in-flight flag BEFORE CancelOrder

            for (int i = 0; i < _entryOrders.Length; i++)
            {
                Order o = _entryOrders[i];
                if (o != null && !Order.IsTerminalState(o.OrderState))
                    CancelOrder(o);
            }
            Print("[SizeMapZone] cancelling unfilled zone entries (" + (!trendOk ? "trend flipped" : "left arming distance") + ").");
        }

        // The runner's trail, made explicit: whenever a NEW qualifying zone appears on the same
        // side and beyond the runner's current stop in the favourable direction, move the stop to
        // StopLossTicks on the far side of THAT zone -- reusing the same tick count as the initial
        // stop, per the spec ("trailed to 8 ticks"), not a separate dial. Never loosens. "The next
        // place" is read as the NEAREST qualifying zone beyond the current stop, not the furthest
        // available -- ties broken by whichever tightens the least. If no new zone appears the
        // stop simply stays: the runner is then an unmanaged position beyond this trail, which is
        // the honest consequence of the rule as specified.
        private void MaintainRunnerTrail(IReadOnlyList<RadarNode> nodes)
        {
            if (nodes == null || nodes.Count == 0) return;
            bool isLong = _armedIsLong;
            double price = Close[0];

            bool haveCandidate = false;
            double bestStop = 0, bestGap = double.MaxValue, bestZonePrice = 0;

            for (int i = 0; i < nodes.Count; i++)
            {
                RadarNode n = nodes[i];
                if (isLong ? n.Side != Side.Bid : n.Side != Side.Ask) continue;
                bool behindPrice = isLong ? n.Price < price : n.Price > price;
                if (!behindPrice) continue;

                double candidateStop = Instrument.MasterInstrument.RoundToTickSize(
                    isLong ? n.Price - StopLossTicks * _tick : n.Price + StopLossTicks * _tick);
                bool tighter = isLong ? candidateStop > _runnerStopPrice : candidateStop < _runnerStopPrice;
                if (!tighter) continue;

                double gap = Math.Abs(candidateStop - _runnerStopPrice);
                if (gap < bestGap) { bestGap = gap; bestStop = candidateStop; bestZonePrice = n.Price; haveCandidate = true; }
            }
            if (!haveCandidate) return;

            if (SetRunnerStop(bestStop))
            {
                // Tag every currently-open runner leg as trailed (M2/exit_reason) -- this is the
                // ONLY call site allowed to set this: it is what "a trailed runner stop" means.
                foreach (string sig in _runnerSignals) _stopTrailed.Add(sig);
                Print(string.Format("[SizeMapZone] runner trail -> {0} ({1}t beyond zone {2})", _runnerStopPrice, StopLossTicks, bestZonePrice));
            }
        }

        // REVIEWER BLOCKER fix: every write to the runner group's shared stop -- the zone trail
        // above, the TP1 break-even sweep (ApplyBreakEvenTo), a rejected-stop retry (OnOrderUpdate),
        // and a late sibling-runner fill (OnExecutionUpdate) -- now routes through here instead of
        // writing _runnerStopPrice and calling SetStopLoss directly three separate ways. ">=  "/"<="
        // (not strict) is deliberate: a rejected-stop retry needs to resubmit the CURRENT price
        // verbatim (a strict "improves" check would treat "unchanged" as "worse" and silently
        // no-op the resend), and "unchanged" is by definition not adverse either. Also folds in B1's
        // market-side clamp, so every price this file ever hands NT8 for a runner stop has already
        // been through both guards paid for by the live crash. Returns whether it actually applied,
        // so MaintainRunnerTrail can tell a genuine trail move from a no-op.
        private bool SetRunnerStop(double candidate)
        {
            candidate = ClampStopToMarket(candidate);
            bool atLeastAsFavourable = _runnerStopPrice == 0
                || (_armedIsLong ? candidate >= _runnerStopPrice : candidate <= _runnerStopPrice);
            if (!atLeastAsFavourable) return false;

            _runnerStopPrice = candidate;
            foreach (string sig in new List<string>(_runnerSignals))   // M3: snapshot -- SetStopLoss can fill synchronously and mutate _runnerSignals mid-loop
                if (_openLegs.Contains(sig)) SetStopLoss(sig, CalculationMode.Price, _runnerStopPrice, false);
            return true;
        }

        // B1: never hand NT8 a stop at or through the current market -- exactly what produced
        // "Stop price can't be changed above the market" and the StopCancelClose termination on
        // Javier's first live Replay trade. A protective stop on a LONG is a sell stop and must sit
        // strictly below the bid; on a SHORT, a buy stop strictly above the ask. Same clamp idiom as
        // TBStrategy.cs's limit-price guard. GetCurrentBid/Ask return 0 when NT8 has no quote yet
        // (Replay warmup) -- nothing to clamp against, so the price passes through unchanged.
        private double ClampStopToMarket(double price)
        {
            if (_armedIsLong)
            {
                double bid = GetCurrentBid();
                return (bid > 0 && price >= bid) ? bid - _tick : price;
            }
            double ask = GetCurrentAsk();
            return (ask > 0 && price <= ask) ? ask + _tick : price;
        }

        // B2: ground truth for "is any entry order still working", read directly off the Order
        // objects rather than trusted from _workingEntryCount -- the counter is exactly the thing
        // that can leak (Expired, or Cancelled with a partial fill, previously left it stuck).
        private bool AnyEntryOrderWorking()
        {
            if (_entryOrders == null) return false;
            for (int i = 0; i < _entryOrders.Length; i++)
            {
                Order o = _entryOrders[i];
                if (o != null && !Order.IsTerminalState(o.OrderState)) return true;
            }
            return false;
        }

        private void UpdateOpenLegExcursion()
        {
            if (_openLegs.Count == 0) return;
            bool isLong = _armedIsLong;
            // M3: NT8's own docs -- OnExecutionUpdate can trigger in the middle of a call to
            // OnBarUpdate() under the Playback connection, which Javier develops in exclusively. An
            // exit fill for one of these legs mid-loop would both mutate _openLegs (InvalidOperation
            // on a live foreach) and remove its _fillPrice/_maeTicks/_mfeTicks entries out from under
            // this same iteration (KeyNotFoundException). Snapshot the set and re-check each
            // dictionary before touching it.
            foreach (string sig in new List<string>(_openLegs))
            {
                double entry, mfe, mae;
                if (!_fillPrice.TryGetValue(sig, out entry)) continue;
                if (!_mfeTicks.TryGetValue(sig, out mfe) || !_maeTicks.TryGetValue(sig, out mae)) continue;
                double favT = (isLong ? (High[0] - entry) : (entry - Low[0])) / _tick;
                double advT = (isLong ? (entry - Low[0]) : (High[0] - entry)) / _tick;
                if (favT > mfe) _mfeTicks[sig] = favT;
                if (advT > mae) _maeTicks[sig] = advT;
            }
        }

        private void ApplyBreakEvenTo(string sig)
        {
            if (!_fillPrice.ContainsKey(sig)) return;
            double be = Instrument.MasterInstrument.RoundToTickSize(_fillPrice[sig]);
            // REVIEWER BLOCKER: a runner leg's stop may already have trailed past break-even via
            // MaintainRunnerTrail -- route it through SetRunnerStop so this can only tighten, never
            // yank an already-trailed runner back to its own entry price. A non-runner (TP2/TP3) leg
            // has only two possible stop states ever (original or break-even) and break-even is
            // always the tighter of the two, so it just needs B1's market clamp.
            if (_runnerSignals.Contains(sig))
                SetRunnerStop(be);
            else
                SetStopLoss(sig, CalculationMode.Price, ClampStopToMarket(be), false);
            _stopIsBreakeven[sig] = true;
        }

        // B2: `reconciled` distinguishes the safety-net path (OnBarUpdate's ground-truth check) from
        // the normal per-leg path so the Output window log tells them apart -- if reconciliation
        // fires routinely rather than as a rare recovery, the per-leg bookkeeping leak is still there.
        private void EndCycle(bool reconciled = false)
        {
            if (reconciled)
            {
                // Any leg still in _openLegs here filled but never got a matching "Stop loss"/
                // "Profit target" execution -- StopCancelClose's own flatten order (or a session-
                // close exit, or a manual DOM flatten) closed it outside the per-leg path, so no
                // WriteResolvedRow call ever ran for it. Write a best-effort row now rather than
                // leave the trade this cycle just had with no CSV row at all: exit price/time are
                // approximated from the current bar since no execution event named this leg's exit.
                // exit_reason "Flat-Unknown" marks the approximation so it is never mistaken for a
                // matched execution.
                foreach (string sig in new List<string>(_openLegs))
                {
                    double fillPx;
                    double exitPx = Close[0];
                    double pnlTicks = _fillPrice.TryGetValue(sig, out fillPx)
                        ? (_armedIsLong ? (exitPx - fillPx) : (fillPx - exitPx)) / _tick : 0;
                    WriteResolvedRow(sig, fillPx, pnlTicks, "Flat-Unknown", Time[0]);
                }
                Print("[SizeMapZone] cycle ended by reconciliation (flat, no working entries) -- " +
                    "per-leg bookkeeping did not reach zero on its own (_workingEntryCount=" + _workingEntryCount +
                    ", _openLegs=" + _openLegs.Count + "). Likely StopCancelClose, exit-on-session-close, " +
                    "a manual flatten, or the strategy being disabled.");
            }

            // Force a fresh departure before this exact zone can arm again -- otherwise a trade
            // that resolves right back at the zone price would immediately re-arm on the same touch.
            _zoneMemory.Remove(ZoneKey(_armedIsLong ? Side.Bid : Side.Ask, _armedPrice));

            _cycleActive = false;
            _cancelPending = false;
            _beArmed = false;
            _runnerStopPrice = 0;
            _runnerSignals.Clear();
            _fillPrice.Clear();
            _fillTime.Clear();
            _maeTicks.Clear();
            _mfeTicks.Clear();
            _stopIsBreakeven.Clear();
            _stopTrailed.Clear();
            _stopRetried.Clear();
            _targetsHitThisCycle.Clear();
            _legIndexBySignal.Clear();
            _openLegs.Clear();          // B2: reconciliation can end a cycle with legs still open
            _workingEntryCount = 0;     // B2: force the counter to match reality on the reconciled path
            Array.Clear(_entryOrders, 0, _entryOrders.Length);
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            if (execution == null || execution.Order == null || !_cycleActive) return;
            Order order = execution.Order;

            int legIndex;
            if (_legIndexBySignal.TryGetValue(order.Name, out legIndex))
            {
                bool filledEnough = order.OrderState == OrderState.Filled
                    || order.OrderState == OrderState.PartFilled
                    || (order.OrderState == OrderState.Cancelled && order.Filled > 0);
                if (!filledEnough) return;

                string sig = order.Name;
                _fillPrice[sig] = order.AverageFillPrice;
                _fillTime[sig] = time;   // B6: holding_time_sec needs the real fill time, not arm time
                _openLegs.Add(sig);
                _maeTicks[sig] = 0; _mfeTicks[sig] = 0; _stopIsBreakeven[sig] = false;
                if (legIndex >= FixedTargetLegs) _runnerSignals.Add(sig);

                Print(string.Format("[SizeMapZone] {0} filled {1} @ {2}", sig, order.Filled, order.AverageFillPrice));

                // REVIEWER BLOCKER: was a bare `_runnerStopPrice = ...` assignment -- a LATE-filling
                // sibling runner (queue position is invisible here, see the file header) could
                // overwrite the group's already-trailed stop with its own untrailed initial value,
                // and MaintainRunnerTrail's next "tighter than _runnerStopPrice" comparison would then
                // trust that regressed baseline. Routed through SetRunnerStop so it can only apply
                // when at least as favourable, and gets B1's market clamp for free.
                if (legIndex >= FixedTargetLegs)
                    SetRunnerStop(Instrument.MasterInstrument.RoundToTickSize(
                        _armedIsLong ? order.AverageFillPrice - StopLossTicks * _tick : order.AverageFillPrice + StopLossTicks * _tick));

                // B1 FIX: break-even used to arm HERE, on THIS leg's own ENTRY fill -- but all four
                // legs are limit orders resting at the SAME zone price, so they fill together near
                // the market by construction. legIndex==0 firing on entry meant legs 2-4 got a stop
                // set to their own just-filled (at-the-market) price the instant they filled: either
                // an instant scratch, or NT8 rejects a stop at/through the market and StopCancelClose
                // terminates the strategy -- which is exactly what happened live. The real trigger
                // (TP1's TARGET fill, and only that) now lives in the exit branch below. This is only
                // the late-fill case: break-even was already armed by an earlier TP1 target fill, and
                // THIS leg is only filling now.
                if (_beArmed) ApplyBreakEvenTo(sig);
                return;
            }

            // Exit fills: Stop loss / Profit target, routed back to a leg via FromEntrySignal
            // (order.Name on these is always the generic "Stop loss"/"Profit target" string).
            if (order.Name != "Stop loss" && order.Name != "Profit target") return;
            string fromSig = order.FromEntrySignal;
            int fromLeg;
            if (fromSig == null || !_legIndexBySignal.TryGetValue(fromSig, out fromLeg)) return;
            if (order.OrderState != OrderState.Filled && order.OrderState != OrderState.PartFilled) return;

            double fillPx = _fillPrice.ContainsKey(fromSig) ? _fillPrice[fromSig] : order.AverageFillPrice;
            double pnlTicks = (_armedIsLong ? (price - fillPx) : (fillPx - price)) / _tick;

            if (order.Name == "Profit target" && fromLeg < FixedTargetLegs)
            {
                string tag = "TP" + (fromLeg + 1);
                if (!_targetsHitThisCycle.Contains(tag)) _targetsHitThisCycle.Add(tag);
            }

            // B1 FIX: this is the real break-even trigger -- TP1's own TARGET fill, and only that.
            // !_beArmed guards a PartFilled-then-Filled pair on TP1 from sweeping every other leg
            // twice. See the file header and the entry branch above for what this replaces and why.
            if (order.Name == "Profit target" && fromLeg == 0 && MoveToBreakEvenAfterTp1 && !_beArmed)
            {
                _beArmed = true;
                foreach (string openSig in new List<string>(_openLegs))   // M3: snapshot -- ApplyBreakEvenTo can fill synchronously and mutate _openLegs mid-loop
                    if (openSig != fromSig) ApplyBreakEvenTo(openSig);
            }

            // M2/exit_reason: Trail-Stop takes priority over BE-Stop -- a runner that reached
            // break-even and was LATER trailed further by MaintainRunnerTrail should read by its
            // final, more-favourable state, not the superseded break-even one. Before this fix both
            // a genuinely untouched initial stop AND a heavily-trailed runner wrote plain "Stop",
            // so the CSV could not separate a profitable trailed exit from a straight loser.
            string exitReason = order.Name == "Profit target" ? "Target"
                : (_stopTrailed.Contains(fromSig) ? "Trail-Stop"
                : (_stopIsBreakeven.ContainsKey(fromSig) && _stopIsBreakeven[fromSig] ? "BE-Stop" : "Stop"));

            Print(string.Format("[SizeMapZone] {0} exit={1} entry={2:0.00} exit={3:0.00} pnl={4:0.0;-0.0;0.0}t mae={5:0.0}t mfe={6:0.0}t",
                fromSig, exitReason, fillPx, price, pnlTicks,
                _maeTicks.ContainsKey(fromSig) ? _maeTicks[fromSig] : 0,
                _mfeTicks.ContainsKey(fromSig) ? _mfeTicks[fromSig] : 0));

            WriteResolvedRow(fromSig, fillPx, pnlTicks, exitReason, time);

            _openLegs.Remove(fromSig);
            _runnerSignals.Remove(fromSig);
            _fillPrice.Remove(fromSig);
            _fillTime.Remove(fromSig);
            _maeTicks.Remove(fromSig);
            _mfeTicks.Remove(fromSig);
            _stopIsBreakeven.Remove(fromSig);
            _stopTrailed.Remove(fromSig);
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice, OrderState orderState,
            DateTime time, ErrorCode error, string comment)
        {
            if (order == null || !_cycleActive) return;

            int legIndex;
            if (_legIndexBySignal.TryGetValue(order.Name, out legIndex))
            {
                // B2 FIX: every terminal state must decrement _workingEntryCount exactly once, or the
                // counter never reaches zero and the cycle can never end via the normal per-leg path
                // (OnBarUpdate's reconciliation check is the safety net for this, not a reason to
                // leave it leaking). Order.IsTerminalState covers Filled/Cancelled/Rejected/Expired --
                // Expired was missing entirely before, and Cancelled with filled > 0 (a partial fill
                // then cancelled) fell through both old branches and decremented nothing.
                if (!Order.IsTerminalState(orderState)) return;
                if (orderState != OrderState.Filled && filled == 0)
                    WriteUnfilledRow(legIndex, orderState == OrderState.Rejected ? "Rejected" : orderState.ToString(), time);
                _workingEntryCount--;   // resolved -- no longer something MaintainArmedZone needs to cancel
                return;
            }

            // A rejected protective stop leaves a live position unprotected -- worth one bounded
            // resubmit, same pattern as RlpLongStrategy.cs's _stopRetried. Not attempted for a
            // rejected profit target: that leg still has its stop, so the position stays protected
            // either way and a missed target is a smaller problem than an unprotected one.
            if (order.Name == "Stop loss" && orderState == OrderState.Rejected)
            {
                string fromSig = order.FromEntrySignal;
                int fromLeg;
                if (fromSig == null || !_legIndexBySignal.TryGetValue(fromSig, out fromLeg)) return;
                if (Position.MarketPosition == MarketPosition.Flat || !_openLegs.Contains(fromSig)) return;

                bool alreadyRetried = _stopRetried.ContainsKey(fromSig) && _stopRetried[fromSig];
                if (alreadyRetried)
                {
                    Print("[SizeMapZone] WARNING: Stop loss REJECTED again for " + fromSig + " -- giving up, POSITION UNPROTECTED on that leg. " + comment);
                    return;
                }
                _stopRetried[fromSig] = true;

                // REVIEWER BLOCKER FIX: used to always recompute from the ORIGINAL entry price +/-
                // StopLossTicks, discarding any break-even or zone-based trail already applied to
                // this leg -- a rejected-stop retry on a runner that had trailed well past entry
                // would throw it straight back to its untrailed initial stop. A runner leg resubmits
                // the group's own current, already-correct _runnerStopPrice through SetRunnerStop
                // (">=" lets an unchanged price still force the resend -- see that method's comment).
                // Any other leg has only two possible stop values ever (original or break-even), so
                // recomputing from its own break-even flag is already correct; it just needs B1's
                // market clamp before resubmission.
                double entry = _fillPrice.ContainsKey(fromSig) ? _fillPrice[fromSig] : 0;
                Print("[SizeMapZone] WARNING: Stop loss REJECTED for " + fromSig + " -- resubmitting once. " + comment);
                if (_runnerSignals.Contains(fromSig))
                {
                    double fallback = _armedIsLong ? entry - StopLossTicks * _tick : entry + StopLossTicks * _tick;
                    SetRunnerStop(_runnerStopPrice != 0 ? _runnerStopPrice : fallback);
                }
                else
                {
                    bool be = _stopIsBreakeven.ContainsKey(fromSig) && _stopIsBreakeven[fromSig];
                    double stopPx = be ? entry : (_armedIsLong ? entry - StopLossTicks * _tick : entry + StopLossTicks * _tick);
                    SetStopLoss(fromSig, CalculationMode.Price, ClampStopToMarket(Instrument.MasterInstrument.RoundToTickSize(stopPx)), false);
                }
            }
        }

        // B6: exitTime is the caller's actual execution/order-event time (or Time[0] as a labelled
        // best-effort from EndCycle's reconciliation path) -- before this fix every row shared the
        // ARM timestamp and no exit time was recorded at all, so holding time was undiscoverable.
        private void WriteResolvedRow(string sig, double fillPx, double pnlTicks, string exitReason, DateTime exitTime)
        {
            if (_log == null) return;
            DateTime fillTime;
            double holdingSec = _fillTime.TryGetValue(sig, out fillTime) ? (exitTime - fillTime).TotalSeconds : -1;
            _log.WriteRow(string.Join(",", new string[]
            {
                _armSnapshot.Time.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                sig,
                _armedIsLong ? "Long" : "Short",
                _armSnapshot.ZonePrice.ToString("F2"),
                _armSnapshot.ZonePeakSize.ToString(),
                _armSnapshot.RestingSizeAtTouch.ToString(),
                _armSnapshot.DepartureTicksAtArm.ToString("F1"),
                _armSnapshot.SmaState,
                _armSnapshot.ZonePrice.ToString("F2"),
                "1",
                fillPx.ToString("F2"),
                (_maeTicks.ContainsKey(sig) ? _maeTicks[sig] : 0).ToString("F1"),
                (_mfeTicks.ContainsKey(sig) ? _mfeTicks[sig] : 0).ToString("F1"),
                string.Join("|", _targetsHitThisCycle),
                exitReason,
                pnlTicks.ToString("F1"),
                _armSnapshot.InWindow.ToString(),
                _armSnapshot.AgeSeconds.ToString("F1"),
                _armSnapshot.Confidence.ToString("F3"),
                _armSnapshot.State.ToString(),
                _armSnapshot.RawState.ToString(),
                _armSnapshot.FirstSeenTicks.ToString(),
                exitTime.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                holdingSec >= 0 ? holdingSec.ToString("F1") : "",
                _armSnapshot.EfficiencyRatio.ToString("F3")
            }));
        }

        private void WriteUnfilledRow(int legIndex, string reason, DateTime resolvedTime)
        {
            if (_log == null) return;
            string sig = LegSignal(_armedIsLong, legIndex);
            _log.WriteRow(string.Join(",", new string[]
            {
                _armSnapshot.Time.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                sig,
                _armedIsLong ? "Long" : "Short",
                _armSnapshot.ZonePrice.ToString("F2"),
                _armSnapshot.ZonePeakSize.ToString(),
                _armSnapshot.RestingSizeAtTouch.ToString(),
                _armSnapshot.DepartureTicksAtArm.ToString("F1"),
                _armSnapshot.SmaState,
                _armSnapshot.ZonePrice.ToString("F2"),
                "0", "", "", "",
                string.Join("|", _targetsHitThisCycle),
                reason, "",
                _armSnapshot.InWindow.ToString(),
                _armSnapshot.AgeSeconds.ToString("F1"),
                _armSnapshot.Confidence.ToString("F3"),
                _armSnapshot.State.ToString(),
                _armSnapshot.RawState.ToString(),
                _armSnapshot.FirstSeenTicks.ToString(),
                resolvedTime.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                "",
                _armSnapshot.EfficiencyRatio.ToString("F3")
            }));
        }

        // The header gains columns as this file learns what the forward test needs to see, and the
        // filename is wall-clock dated -- so a re-run on a day whose file was written by an earlier
        // column set used to append wider rows under a narrower header, silently. Not hypothetical:
        // zone-decisions-ES-20260816.csv on this machine holds 34 rows of 16 fields and 88 of 24
        // under a 16-column header, which nothing can align afterwards. Roll to a sibling instead;
        // both files stay readable, which matters because the rows carry MARKET time and one file
        // legitimately spans several Replay days.
        private static string PathForCurrentSchema(string stem)
        {
            string path = stem + ".csv";
            for (int n = 2; n < 100; n++)
            {
                if (!File.Exists(path)) return path;
                // Unreadable == unverifiable, and unverifiable must never be appended to. File.Exists
                // saying yes does not make the open safe (a sharing violation, an antivirus or
                // cloud-sync agent holding the handle, an ACL hiccup all still throw), and
                // DecisionLog opens a FRESH handle per row minutes later -- so a failure that clears
                // in between turns "could not read the header once" into 25-field rows appended under
                // a 16-field header. Roll on failure, exactly as a mismatched header does.
                bool usable;
                try { using (StreamReader r = new StreamReader(path)) usable = r.ReadLine() == CsvHeader; }
                catch { usable = false; }
                if (usable) return path;
                path = stem + "-" + n + ".csv";
            }
            return path;
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "UNKNOWN";
            char[] c = s.ToCharArray();
            for (int i = 0; i < c.Length; i++)
                if (!char.IsLetterOrDigit(c[i]) && c[i] != '-' && c[i] != '_') c[i] = '_';
            return new string(c);
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Departure distance (ticks)", Description = "How far price must travel away from a promoted wall (the largest bar-close distance seen since the wall was found) before a return to it counts as a retest and can arm an entry.", Order = 1, GroupName = "01. Zone")]
        public int DepartureTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Arming distance (ticks)", Description = "Place the limit order once a bar closes within this many ticks of a qualifying zone. Cancelled if a later bar closes back outside this band while still unfilled, or if the trend flips.", Order = 2, GroupName = "01. Zone")]
        public int ArmingDistanceTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Require fresh wall (InWindow + confidence)", Description = "OPTIONAL, OFF by default so the first Replay run measures the entry rule exactly as specified. ON adds two requirements before a zone can arm: the wall must currently be InWindow (observed live within the last 10 depth levels, not just remembered from up to 300s ago) and its engine-tracked confidence must clear Min confidence to arm below.", Order = 3, GroupName = "01. Zone")]
        public bool RequireFreshWall { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1.0)]
        [Display(Name = "Min confidence to arm", Description = "Only enforced when Require fresh wall is ON. The wall's engine-tracked confidence (0-1) must be at least this to arm an entry.", Order = 4, GroupName = "01. Zone")]
        public double MinConfidenceToArm { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Also require price beyond SMA20", Description = "Trend definition: long only when SMA20 > SMA50 > SMA200, short only when SMA20 < SMA50 < SMA200 (stacked). ON adds: close must also be above SMA20 for longs / below SMA20 for shorts.", Order = 1, GroupName = "02. Trend")]
        public bool RequirePriceBeyondSma20 { get; set; }

        [NinjaScriptProperty]
        [Range(2, 2000)]
        [Display(Name = "Range filter lookback (bars)", Description = "How many closed bars the efficiency ratio measures over. Default 40 = 20 minutes on a 30-second chart. Longer sees the whole range and is slower to declare a trend; shorter reacts to each leg inside it.", Order = 2, GroupName = "02. Trend")]
        public int RangeFilterPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.0, 1.0)]
        [Display(Name = "Min efficiency ratio (0 = filter off)", Description = "Range filter. Net price displacement over the lookback divided by the total path walked to get there: 1 is a straight line, 0 is a round trip. No zone arms below this. 0 disables the gate but still logs the ratio on every row, which is how the right value gets measured. PROVISIONAL default 0.30, and both numbers below are measured over the 40-bar default rather than over a whole session: the most trend-like 40-bar window inside the hour of ES chop that motivated this filter scores 0.12, while a three-up-one-back leg scores 0.49. The margin is 2.6x, not the 10x the whole hour suggests.", Order = 3, GroupName = "02. Trend")]
        public double MinEfficiencyRatio { get; set; }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Stop loss (ticks)", Description = "Shared initial stop-loss for every contract, in ticks. Also the offset used when trailing the runner: its stop moves to this many ticks beyond the next qualifying zone found in its favor.", Order = 1, GroupName = "03. Exits")]
        public int StopLossTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 50)]
        [Display(Name = "TP1 (R multiple)", Description = "First contract's target, in multiples of the stop-loss (R). Default 3 = 3 x Stop loss ticks.", Order = 2, GroupName = "03. Exits")]
        public double Tp1RMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 50)]
        [Display(Name = "TP2 (R multiple)", Description = "Second contract's target, in multiples of the stop-loss (R). Default 4.", Order = 3, GroupName = "03. Exits")]
        public double Tp2RMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 50)]
        [Display(Name = "TP3 (R multiple)", Description = "Third contract's target, in multiples of the stop-loss (R). Default 5.", Order = 4, GroupName = "03. Exits")]
        public double Tp3RMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Contracts", Description = "Total contracts per zone entry. The first 3 (or fewer, if this is set below 3) take TP1/TP2/TP3 above; any beyond 3 are runners -- no fixed target, trailed as one group per the runner-trail rule.", Order = 5, GroupName = "03. Exits")]
        public int ContractCount { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Move to break-even after TP1", Description = "Once TP1 fills, every other still-open leg's stop moves to that leg's own fill price. Raises the trade's floor and locks in break-even sooner, at the cost of some average R on legs that would have pulled back through entry before eventually reaching their own target or the runner trail -- the standard scaled-exit trade-off. OFF leaves every leg at its original stop for the whole trade.", Order = 6, GroupName = "03. Exits")]
        public bool MoveToBreakEvenAfterTp1 { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 20.0)]
        [Display(Name = "Wall size multiple (K)", Description = "How many times the median level a resting size must be to count as a wall. PER INSTRUMENT: measured 1.5 for ES (rests ~70 lots/level), 4.0 for NQ -- see engine/RadarConfig.cs.", Order = 1, GroupName = "04. Wall Detection")]
        public double WallSizeMultiple { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10000)]
        [Display(Name = "Minimum wall lots", Description = "Absolute floor a wall must clear as well as the multiple, so a dead-thin book cannot promote a 6-lot level.", Order = 2, GroupName = "04. Wall Detection")]
        public int MinWallLots { get; set; }
        #endregion
    }
}
