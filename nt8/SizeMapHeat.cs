// SizeMapHeat — the only file in SizeMap that touches NinjaTrader.
//
// It contains no heatmap logic. It adapts events in (MarketDepthEventArgs -> DepthEvent ->
// BookMirror + ColumnRing) and pixels out (ColumnRing -> Rasterizer -> one DrawBitmap). Every
// decision about what the picture MEANS lives in engine/, which is portable, tested and
// harness-renderable; everything here is platform plumbing that cannot be tested off-platform.
//
// The guards below are not ceremony. Each one is a bug that has already been paid for once —
// in SizeMapProbe.cs on this machine, or in Trading-radar's RadarTab.cs on live tape.
//
// Phase 1 deliberately has no on-raster text: the 5x7 bitmap font is Phase 2, so the frame-time
// HUD goes to the Output window instead. That is also why the refusal paths below Print rather
// than draw — see Refuse().

#region Using declarations
using System;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Threading;
using NinjaTrader.Cbi;      // Operation (the depth Add/Update/Remove enum) lives here, not in Data
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using SizeMap.Engine;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
	public class SizeMapHeat : Indicator
	{
		// ================= depth thread — everything here under _engineLock =================
		private readonly object _engineLock = new object();
		private BookMirror      _book;
		private DepthBaseline   _depthBase;
		private ColumnRing      _ring;
		private SizeMapRecorder _rec;
		private WallTracker     _tracker;
		private RadarConfig     _radarCfg;
		private double          _tick = 0.25;
		private double          _barMs;              // 0 = the bar type carries no time model

		private DateTime _lastEngineRun   = DateTime.MinValue;
		private DateTime _lastDepthSample = DateTime.MinValue;
		private DateTime _lastWallRun     = DateTime.MinValue;

		// Ported verbatim from RadarTab.cs 105-106. These three numbers ARE the replay handling.
		// Must exceed the longest wall SizeMap will ever be asked to price. See the block at the
		// BookMirror construction for the measurement that set it.
		private static readonly TimeSpan TradeRetention = TimeSpan.FromSeconds(1500);

		private const double EngineIntervalMs      = 50;      // ~20 Hz forward throttle
		private const double ReplayResetBackwardMs = 2000;    // real rewinds jump seconds-minutes
		private const double ReplayResetForwardMs  = 60000;   // bigger than any real quiet-tape gap

		// ---- depth thread -> UI thread. Only these, only through Volatile, never a lock. ----
		private long _nowTicks;                 // market time of the newest event
		private int  _s0 = 8, _sCap = 320;      // the live ramp anchors, in lots
		private int  _levelsSeen;               // max levels actually observed this session
		private string _ladderAudit;            // 1 Hz structural audit, published for the Output line
		private int  _epochs;                   // replay rewinds / feed discontinuities handled

		// The wall snapshot, published exactly like the ring's index: the writer swaps a WHOLE
		// immutable list under Volatile, the reader takes one Volatile.Read and never a lock.
		// GetSnapshot already returns a fresh list per call, so there is nothing to copy and nothing
		// the depth thread can mutate under the render thread's feet.
		private System.Collections.Generic.IReadOnlyList<RadarNode> _nodes;

		// ================= UI thread only =================
		private readonly Palette   _palette = new Palette();
		private readonly Stopwatch _sw      = new Stopwatch();
		private SharpDX.Direct2D1.Bitmap _bmp;
		private object _rtSeen;                 // identity guard: the chart owns TWO render targets
		private int[]  _px;
		private int    _bmpW, _bmpH;
		private bool   _zReissued;
		private string _lastRefusal;
		private double _emaMs;
		private double _fps;
		private long   _lastFrameStamp;
		private int    _frame;
		private const int HudEveryFrames = 20;  // ~5 s at NT8's 4 fps repaint cap

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name                     = "SizeMapHeat";
				Description              = @"Depth liquidity heatmap: resting size in tick x 250 ms cells, drawn behind the bars.";
				Calculate                = Calculate.OnEachTick;
				IsOverlay                = true;    // share the bars' ChartScale
				IsChartOnly              = true;
				DrawOnPricePanel         = false;   // SetZOrder(-N) has been reported not to take
				                                    // effect when combined with it; we use zero Draw.*
				DisplayInDataBox         = false;
				IsAutoScale              = false;   // a heatmap must never move the price scale
				ScaleJustification       = ScaleJustification.Right;
				// Collecting must NOT stop when the tab is hidden, or the ring gets holes that look
				// exactly like a quiet market and cannot be told apart afterwards.
				IsSuspendedWhileInactive = false;

				MinutesOfHistory         = 30;
				WallSizeMultiple         = 1.5;   // measured for ES; raise toward 4.0 on NQ
				MinWallLots              = 40;
				RecordRaw                = true;
				ShowLegend               = true;
			}
			else if (State == State.DataLoaded)
			{
				// Everything is built here, before the first market event can arrive, and nothing is
				// ever allocated again on either hot path. State.Historical runs AFTER this, so the
				// ring and the palette exist by the time the z-order is set.
				_tick      = TickSize > 0 ? TickSize : 0.25;
				_barMs     = BarMsOf(BarsPeriod);
				// 30 s was two orders of magnitude shorter than the walls it measures. Measured on
				// real ES tape: p50 wall age 394 s, and 88.6% / 91.1% / 93.7% of walls outlive a 30 s
				// window. ConsumptionTracker counts trades from BookMirror's ring, so those trades were
				// already pruned and the ledger reported "cancelled" for liquidity that was BOUGHT —
				// a lie in the one number that is the entire product. Re-running the verdict at 1500 s
				// moved mean TradeBackedFraction 0.678 -> 0.945 with ZERO outcome flips and identical
				// episode boundaries, so the bias was pure and this removes it rather than trading it.
				// Cost: ES runs ~42 trades/s, so 1500 s is ~63k trades in the ring, low single-digit MB.
				_book      = new BookMirror(_tick, TradeRetention);
				_depthBase = new DepthBaseline(4096);
				// ponytail: RadarConfig's 24 thresholds are NQ PLACEHOLDERS and have never been
				// validated out of sample — only TickSize is known true here. Ceiling — on ES the wall
				// census will be wrong in one direction or the other, which is what the HUD's WALLS
				// counter is for. Upgrade path — calibrate against the recorded corpus, then either
				// change the defaults or expose the two that move (K_mult, MinAbsSize) as properties.
				// K_mult and MinAbsSize are per-INSTRUMENT and the shipped defaults are NQ numbers.
				// Measured on real ES tape: ES rests ~70 lots a level, so K_mult 4.0 demands 280 and
				// finds 9 walls in 82 minutes — a census of 0L 0R 0F on Javier's chart, which reads
				// as "broken" and is actually "asked for a wall four times bigger than this market
				// makes". At 1.5 ES lands in the design's live/remembered band. NQ is thinner and
				// wants the higher number, which is exactly why this cannot be one constant.
				_radarCfg  = new RadarConfig { TickSize = _tick, K_mult = WallSizeMultiple, MinAbsSize = MinWallLots };
				_tracker   = new WallTracker(_radarCfg);
				_ring      = new ColumnRing(Math.Max(2, MinutesOfHistory * 60 * 1000 / ColumnRing.BucketMs), 48);
				SeedFromSnapshot();

				if (RecordRaw)
				{
					_rec = new SizeMapRecorder(Instrument.MasterInstrument.Name, _tick, IsPlayback());
					// Called from the drain thread; Print marshals to the UI dispatcher and never
					// calls back into the recorder, which is the contract that file documents.
					_rec.Log = msg => Print("SizeMapHeat rec: " + msg);
					_rec.Start();
				}
			}
			else if (State == State.Historical)
			{
				// Bars sit at ZOrder 1, NinjaScript objects default to 10001. -1 is the band below
				// everything the platform draws, and it is only honoured from State.Historical.
				SetZOrder(-1);
			}
			else if (State == State.Terminated)
			{
				if (_rec != null) { _rec.Stop(); _rec = null; }   // flushes on its own thread
				ReleaseDeviceResources();
			}
		}

		protected override void OnBarUpdate() { }   // nothing here is bar-driven

		// ================= INSTRUMENT DEPTH THREAD =================
		// No Print. No LINQ. No file IO. No chart access. No DateTime.Now — every timestamp is
		// e.Time, because in accelerated Playback market time and wall time are minutes apart and
		// every window in this file is expressed in market time.
		//
		// Allocation: the per-EVENT path allocates nothing. The per-ENGINE-TICK path does, by more
		// than this comment used to claim. Benchmarked against real ES depth, Release:
		//
		//   4 Hz (shipped)  0.085 ms/call   70 KB/call  ->  0.34 ms and 280 KB per market second
		//   20 Hz           0.219 ms/call  204 KB/call  ->  4.38 ms and 4.07 MB per market second
		//
		// 5x the call rate costs 12.9x the CPU and 14.5x the garbage, so the 250 ms engine gate is
		// LOAD-BEARING, not a nicety. The cost is WallTracker.Update: TemporalMedian allocates a
		// List<long> and sorts it once per side AND once per level inside IsConfirmed -> Baseline,
		// plus four HashSet/List per Update. At 500x Playback the shipped config is ~170 ms of CPU
		// and ~140 MB/s of Gen0 per wall-clock second, inside _engineLock, on the shared instrument
		// depth thread. Tight but survivable; at 20 Hz it would be 2.2 s of CPU per wall second.
		// ponytail: left at 4 Hz. Ceiling — the gate is what makes it safe, so do not raise the rate.
		// Upgrade path — reusable buffers in TemporalMedian/MedianOf before touching it.

		protected override void OnMarketDepth(MarketDepthEventArgs e)
		{
			if (_ring == null) return;

			// RESET FIRST, above the price guard. A reset carries no meaningful price, so if the
			// `Price <= 0` test runs first the reconnect book-wipe becomes dead code and the map
			// goes silently stale after every reconnect. That bug was found and fixed once already
			// (RadarTab.cs 581) — this ordering is the fix.
			if (e.IsReset)
			{
				DepthEvent rs = new DepthEvent { IsReset = true, Time = e.Time };
				lock (_engineLock)
				{
					if (e.Instrument != Instrument) return;
					_book.ApplyDepth(rs);
					if (_rec != null) _rec.OnDepth(rs);
				}
				return;
			}
			if (e.Price <= 0) return;

			DepthOp op = e.Operation == Operation.Add    ? DepthOp.Add
			           : e.Operation == Operation.Update ? DepthOp.Update
			                                             : DepthOp.Remove;
			DepthEvent de = new DepthEvent
			{
				Side     = e.MarketDataType == MarketDataType.Ask ? Side.Ask : Side.Bid,
				Op       = op,
				Position = e.Position,
				Price    = e.Price,
				Volume   = e.Volume,
				Time     = e.Time,
				IsReset  = false
			};

			lock (_engineLock)
			{
				// Stale event from a PRIOR instrument mid-switch — dropped before it touches the book.
				if (e.Instrument != Instrument) return;
				_book.ApplyDepth(de);
				_ring.Accumulate(e.Time.Ticks, ColumnRing.RowFor(de.Price, _tick), de.Side, de.Volume);

				// The inside market, stored per column. This is also the seam the bid/ask touch trace
				// needs: it is the trace's data, recorded here for free because the book is already hot.
				DepthLevel bb, ba;
				_ring.SetInside(e.Time.Ticks,
					_book.TryBestBid(out bb) ? ColumnRing.RowFor(bb.Price, _tick) : ColumnRing.NoRow,
					_book.TryBestAsk(out ba) ? ColumnRing.RowFor(ba.Price, _tick) : ColumnRing.NoRow);

				if (_rec != null) _rec.OnDepth(de);
				Volatile.Write(ref _nowTicks, e.Time.Ticks);
				MaybeRunEngine(e.Time);
			}
		}

		protected override void OnMarketData(MarketDataEventArgs e)
		{
			if (_ring == null) return;
			if (e.MarketDataType != MarketDataType.Last || e.Price <= 0) return;

			TradeEvent te = new TradeEvent { Price = e.Price, Volume = e.Volume, Time = e.Time };
			lock (_engineLock)
			{
				if (e.Instrument != Instrument) return;
				// Read the inside BEFORE the trade is applied: the aggressor is classified against
				// the quote prevailing at trade time, not the one the trade just changed.
				DepthLevel bb, ba;
				double bid = _book.TryBestBid(out bb) ? bb.Price : 0;
				double ask = _book.TryBestAsk(out ba) ? ba.Price : 0;
				_book.ApplyTrade(te);
				if (_rec != null) _rec.OnTrade(te, ask > 0 ? e.Price >= ask : e.Price > bid);
				Volatile.Write(ref _nowTicks, e.Time.Ticks);
				MaybeRunEngine(e.Time);
			}
		}

		// The clock-discontinuity guard, ported from RadarTab.cs 628-665. Three branches, and their
		// ORDER is the whole point. Javier develops this entire project in Market Replay, so this is
		// not an edge case: without it the first drag of the Playback slider freezes the picture and
		// reads as a render bug.
		private void MaybeRunEngine(DateTime now)
		{
			if (_lastEngineRun != DateTime.MinValue)
			{
				double dMs = (now - _lastEngineRun).TotalMilliseconds;

				// (1) far BACKWARD (rewind/restart) or far FORWARD (scrub-ahead, session rollover —
				//     which would otherwise paint a stale book as live). Rebuild, then fall through.
				if (dMs < -ReplayResetBackwardMs || dMs > ReplayResetForwardMs)
					HandleReplayReset(now);
				// (2) small BACKWARD step (sub-2 s rewind, or an out-of-order feed tick): don't run,
				//     but DO re-base the clock down to `now`. Leaving _lastEngineRun at the forward
				//     high-water mark and early-returning froze the engine until replay time climbed
				//     back past it — that was the bug, and an early return alone reintroduces it.
				else if (dMs < 0) { _lastEngineRun = now; return; }
				// (3) normal forward throttle; _lastEngineRun stays at the last ACTUAL run.
				else if (dMs < EngineIntervalMs) return;
			}
			_lastEngineRun = now;

			// The wall engine, at the DATA QUANTUM (250 ms) rather than at the 20 Hz engine rate, and
			// both halves of that are load-bearing.
			//
			// Honesty: a column is 250 ms. Promoting or killing a wall four times inside one column
			// paints a state the picture cannot show, so the extra work buys nothing anyone can see.
			//
			// Cost: WallDetector.Baseline is a temporal median over BaselineWindow, and its sample
			// count is (call rate x levels x window) — so the sort is O(rate log rate) per call and
			// the total is QUADRATIC in the call rate. At 20 Hz over 30 s that is 6k samples sorted
			// twice per call, 20 times a market second, on the instrument depth thread shared with
			// every chart and DOM on this symbol; in 500x Playback that is market seconds arriving
			// 500x faster than wall time. At 4 Hz it is 1.2k samples and ~0.4 ms per market second.
			// Stalling the depth thread does not make SizeMap slow, it makes NinjaTrader slow.
			if (_lastWallRun == DateTime.MinValue || (now - _lastWallRun).TotalMilliseconds >= ColumnRing.BucketMs)
			{
				_lastWallRun = now;
				_tracker.Update(_book, now);
				Volatile.Write(ref _nodes, _tracker.GetSnapshot(now));
			}

			// 1 Hz, NOT per engine run: at 20 Hz this resamples the same resting book 20x and just
			// autocorrelates itself into a confident wrong percentile (ADR 2026-07-03).
			if (_lastDepthSample == DateTime.MinValue || (now - _lastDepthSample).TotalSeconds >= 1.0)
			{
				_lastDepthSample = now;
				System.Collections.Generic.IReadOnlyList<DepthLevel> b = _book.Levels(Side.Bid);
				System.Collections.Generic.IReadOnlyList<DepthLevel> a = _book.Levels(Side.Ask);
				for (int i = 0; i < b.Count; i++) _depthBase.Add(b[i].Volume);
				for (int i = 0; i < a.Count; i++) _depthBase.Add(a[i].Volume);
				_depthBase.EndBatch();
				PublishScale(b.Count > a.Count ? b.Count : a.Count);

				// Published from HERE, on the depth thread inside _engineLock, because the render
				// thread must never read _book -- same reason _levelsSeen is published rather than
				// read. 1 Hz, one HashSet of at most MaxLevels: free. A string because it is a
				// diagnostic, and a reference assignment is atomic.
				BookMirror.LadderStats sb = _book.Audit(Side.Bid), sa = _book.Audit(Side.Ask);
				Volatile.Write(ref _ladderAudit, string.Format(
					"bid {0}lv span {1:0.0}t dup {2} ooo {3}  |  ask {4}lv span {5:0.0}t dup {6} ooo {7}",
					sb.Count, sb.SpanTicks, sb.Duplicates, sb.OutOfOrder,
					sa.Count, sa.SpanTicks, sa.Duplicates, sa.OutOfOrder));
			}
		}

		// NT8 exposes no "replay was reset" event, so a large jump in e.Time is the most reliable
		// mechanism-agnostic signal. REBUILD, never Clear: a cleared object keeps whatever internal
		// bookkeeping its Clear() forgot, and that is how pre-rewind state survives into replayed
		// history it never saw.
		private void HandleReplayReset(DateTime now)
		{
			_book = new BookMirror(_tick, TradeRetention);
			SeedFromSnapshot();
			_depthBase.Reset();                 // rewound history must not inherit the old distribution
			_lastDepthSample = DateTime.MinValue;
			_ring.Reset(now.Ticks);             // publishes -1 first, so a mid-wipe reader draws nothing
			// REBUILD, not OnReset(): OnReset only blinds the nodes, so a wall born before a rewind
			// would keep being remembered — and drawn — over history it was never observed in.
			_tracker = new WallTracker(_radarCfg);
			_lastWallRun = DateTime.MinValue;
			Volatile.Write(ref _nodes, null);
			if (_rec != null) _rec.OnEpochBreak(now);   // close the file; never splice two tape epochs
			Volatile.Write(ref _epochs, Volatile.Read(ref _epochs) + 1);
			// No Print here: this runs on the depth thread and Print marshals to the UI dispatcher.
			// The counter above surfaces it in the HUD line instead.
		}

		// Prime the ladder from the platform's own snapshot so a chart opened mid-session has a book
		// immediately instead of waiting for every level to be quoted again (RadarTab.cs 511-520).
		private void SeedFromSnapshot()
		{
			try
			{
				if (Instrument == null || Instrument.MarketDepth == null) return;
				System.Collections.Generic.List<DepthLevel> bids = new System.Collections.Generic.List<DepthLevel>();
				System.Collections.Generic.List<DepthLevel> asks = new System.Collections.Generic.List<DepthLevel>();
				for (int i = 0; i < Instrument.MarketDepth.Bids.Count; i++)
					bids.Add(new DepthLevel { Price = Instrument.MarketDepth.Bids[i].Price, Volume = Instrument.MarketDepth.Bids[i].Volume });
				for (int i = 0; i < Instrument.MarketDepth.Asks.Count; i++)
					asks.Add(new DepthLevel { Price = Instrument.MarketDepth.Asks[i].Price, Volume = Instrument.MarketDepth.Asks[i].Volume });
				_book.ResetFromSnapshot(bids, asks);
			}
			catch (Exception) { }   // no snapshot yet is normal; it is not a reason to fail construction
		}

		private void PublishScale(int levels)
		{
			if (levels > Volatile.Read(ref _levelsSeen)) Volatile.Write(ref _levelsSeen, levels);

			long p85 = _depthBase.P85;
			if (p85 <= 0) return;   // still warming up (< 300 samples): keep the compiled defaults

			// ponytail: visual-spec §2.4 anchors s0 on P50, and DepthBaseline — vendored from
			// Trading-radar and re-synced by diff — only exposes P85. P85/4 reproduces the spec's
			// own worked NQ example (P85 40 -> s0 8..10). Ceiling: on an instrument whose level
			// sizes are less skewed the ramp foot sits too high and small levels wash out. Upgrade
			// path: the HUD prints S0 and CAP every 20 frames, so the recorded corpus settles the
			// ratio and it becomes one number here — not a percentile fork of a vendored file.
			int s0  = (int)Math.Max(1, p85 / 4);
			int cap = (int)Math.Max(8 * s0, 8 * p85);
			Volatile.Write(ref _s0, s0);
			Volatile.Write(ref _sCap, cap);
		}

		// ================= UI THREAD =================

		// Fires on device create AND destroy — on destroy RenderTarget is already null, which is why
		// creation cannot live here and the identity compare in OnRender is the belt.
		public override void OnRenderTargetChanged()
		{
			ReleaseDeviceResources();
			_rtSeen = RenderTarget;
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			// A full-panel raster means every click in the chart "hits" us; without this the hit-test
			// pass would run the whole render and a double-click would open our properties dialog.
			if (IsInHitTest) return;
			if (RenderTarget == null || RenderTarget.IsDisposed) return;
			if (chartControl == null || chartScale == null || ChartBars == null || ChartPanel == null) return;
			if (_ring == null) return;

			// The chart owns two render targets (the window one and the WIC one used for hit-test,
			// taskbar thumbnails and resize snapshots). A bitmap made on one is invalid on the other,
			// and the symptom is D2DERR_WRONG_RESOURCE_DOMAIN or an access violation, not a blank chart.
			if (!ReferenceEquals(RenderTarget, _rtSeen))
			{
				ReleaseDeviceResources();
				_rtSeen = RenderTarget;
			}

			// Snapshot the panel rect: W/H can change between two reads inside one frame while the
			// user drags a panel divider.
			int panelX = ChartPanel.X, panelY = ChartPanel.Y, pw = ChartPanel.W, ph = ChartPanel.H;
			if (pw <= 0 || ph <= 0) return;

			// Restore what was READ, not a hardcoded default: the D2D default is PerPrimitive, the
			// target is shared, and NinjaTrader draws after us.
			SharpDX.Direct2D1.AntialiasMode oldAA = RenderTarget.AntialiasMode;
			try
			{
				_sw.Restart();

				// Known first-apply bug: the object can render on top until the chart is reloaded.
				// SetZOrder is idempotent, so re-issuing it once costs nothing.
				if (!_zReissued && State == State.Realtime) { _zReissued = true; SetZOrder(-1); }

				string why = Refusal(chartControl, chartScale, pw, ph);
				if (why != null) { Refuse(why); return; }
				if (_lastRefusal != null)
				{
					// Draw.TextFixed creates a PERSISTENT NinjaScript object. "no bars loaded" fires
					// during every chart load, so without this the caption survives forever: a second
					// post-blit primitive on every frame for the rest of the session, reading
					// "SizeMap — no bars loaded" over a working heatmap.
					try { RemoveDrawObject("SizeMapRefusal"); } catch { }
					_lastRefusal = null;
				}

				long now = Volatile.Read(ref _nowTicks);
				if (now == 0) return;                       // no depth yet: nothing to claim
				int published = _ring.PublishedIndex;
				if (published < 0) return;                  // just after a rewind. Drawing nothing is the truth.

				// Ramp stop 0 IS "no resting size", so it has to be whatever NT8 actually painted.
				// Measured on this machine: ~#1F1F1F, not the #232424 the probe hardcodes. Both calls
				// return immediately when nothing moved.
				_palette.Rebuild(ChartBackground(chartControl));
				_palette.SetScale(Volatile.Read(ref _s0), Volatile.Read(ref _sCap));

				RasterView view = BuildView(chartControl, chartScale, panelX, panelY, ph, now);
				if (!EnsureBitmap(pw, ph)) return;

				System.Collections.Generic.IReadOnlyList<RadarNode> nodes = Volatile.Read(ref _nodes);
				Rasterizer.Rasterize(_ring, _px, pw, ph, view, _palette, nodes);

				// The HUD and the legend, written into the SAME int[] as the heat. Phase 1 could
				// only Print them because there was no way to draw text without a DrawText call;
				// the 5x7 font is what moved them onto the chart while leaving the post-blit
				// primitive count at exactly one.
				//
				// Integer scale only. M22ToDevice is WPF's vertical device transform, so 1.0 at
				// 100% and 1.5 at 150%; a 1-bit font scaled by 1.5 would need resampling, and
				// resampled text over a field whose colours ARE the data is a grey lie.
				int scale = chartControl.M22ToDevice >= 1.25 ? 2 : 1;
				Chrome.DrawHud(_px, pw, ph, HudInfoOf(view, nodes), scale);
				if (ShowLegend) Chrome.DrawLegend(_px, pw, ph, _palette, scale);

				_bmp.CopyFromMemory(_px, pw * 4);

				RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased;
				// SharpDX.RectangleF, NOT SharpDX.Mathematics.Interop.RawRectangleF: the Raw* types
				// live in a third assembly NinjaTrader.Custom.csproj does not reference either.
				// NearestNeighbor because Linear would invent resolution between 250 ms columns and
				// 1-tick rows — and with a 1:1 dest rect it is a pure copy anyway.
				RenderTarget.DrawBitmap(
					_bmp,
					new SharpDX.RectangleF(panelX, panelY, pw, ph),
					1f,
					SharpDX.Direct2D1.BitmapInterpolationMode.NearestNeighbor);
				// That DrawBitmap is the ONLY post-blit primitive, by design. Everything else is an
				// int write into _px.

				_sw.Stop();
				Hud(pw, ph, view, _sw.Elapsed.TotalMilliseconds, nodes);
			}
			catch (Exception ex)
			{
				// A throw escaping OnRender disables the indicator and is far more destructive than
				// a wrong pixel.
				Print("SizeMapHeat render error: " + ex);
				ReleaseDeviceResources();
			}
			finally
			{
				// Its own try/catch, because the most likely cause of a throw in the body above is
				// the render target being disposed mid-frame — and then this line dereferences that
				// same disposed target and throws UNHANDLED, out of the finally, defeating the catch
				// that exists to prevent exactly that. A finally that can throw is not a guard.
				try { RenderTarget.AntialiasMode = oldAA; } catch { }
			}
		}

		// Everything the projection needs, rebuilt from scratch every frame. Nothing derived from a
		// previous frame survives, so scroll, zoom, resize and bar-spacing changes need no
		// invalidation path — there is no cached projection to invalidate.
		private RasterView BuildView(ChartControl cc, ChartScale cs, int panelX, int panelY, int ph, long nowTicks)
		{
			// ROWS: ONE anchor per frame. A per-row GetYByValue returns int, so consecutive tick rows
			// come out 13, 14, 13, 14 px and drift against NT8's own gridlines — that specific defect
			// is why the platform's own depth map reads as smeared slabs.
			// NOT GetPixelsForDistance(_tick). It is typed float but returns a whole number of
			// pixels — measured on this machine: 4.000 where the true height of a tick was 4.439,
			// 5.000 where it was 4.590, 3.000 where it was 3.168. A sub-pixel residual per tick is
			// invisible on one row and lethal across a panel, because the row map is one anchor plus
			// a constant: the error accumulates linearly and put the heat up to 89 px (20 ticks)
			// away from the candles at ordinary zooms. Every PROJ err reading matched that
			// prediction to within a pixel.
			//
			// So measure the tick over the WHOLE visible range instead of asking for one tick.
			// GetYByValue also returns int, but one pixel of quantisation spread over ~1050 is a
			// 0.1% error rather than a 10% one.
			double span   = cs.MaxValue - cs.MinValue;
			int    yBot   = cs.GetYByValue(cs.MinValue);
			int    yTop   = cs.GetYByValue(cs.MaxValue);
			float pxPerTick = (span > 0 && yBot > yTop)
				? (float)((yBot - yTop) * _tick / span)
				: cs.GetPixelsForDistance(_tick);      // degenerate scale: fall back rather than divide by zero
			if (pxPerTick <= 0) pxPerTick = 1f;
			int   anchorRow = (int)Math.Round(cs.MinValue / _tick);
			float anchorY   = cs.GetYByValue(anchorRow * _tick) - panelY;   // panel-local: the raster is blitted at (panelX, panelY)

			// COLUMNS: from the bar PITCH, never GetXByTime — that is slot-based, so every timestamp
			// inside a bar returns the same x and 250 ms resolution silently collapses to bar resolution.
			int i0 = ChartBars.FromIndex, i1 = ChartBars.ToIndex;
			int xLastCentre = cc.GetXByBarIndex(ChartBars, i1);
			float pitch = i1 > i0
				? (xLastCentre - cc.GetXByBarIndex(ChartBars, i0)) / (float)(i1 - i0)
				: cc.GetBarPaintWidth(ChartBars);
			if (pitch <= 0) pitch = 1f;

			float pxPerMs = (float)(pitch / _barMs);

			// GetXByBarIndex returns the bar CENTRE, and NT8 stamps a time-based bar with its period
			// END, so the bar's own timestamp sits at its RIGHT edge.
			DateTime lastClose = ChartBars.Bars.GetTime(i1);
			float    xRight    = xLastCentre + pitch * 0.5f - panelX;

			// NowX is the right edge of the newest 250 ms bucket, not "now": the rasterizer lays every
			// older column off it at PxPerBucket.
			//
			// ponytail: time -> x is linear across the visible window. Ceiling — a session break
			// inside the ring's 30 minutes (the daily maintenance halt) would shift columns older
			// than the gap by the gap's width. Upgrade path — RasterView already isolates this in six
			// fields; swap NowX/PxPerBucket for the SlotTicks[]/SlotX[] bar-edge pair and only
			// Rasterizer.DrawColumns changes.
			long  bucketEnd = (ColumnRing.BucketOf(nowTicks) + 1) * ColumnRing.BucketTicks;
			float nowX      = xRight + (float)((bucketEnd - lastClose.Ticks) / (double)TimeSpan.TicksPerMillisecond) * pxPerMs;

			// SELF-CHECK, and it costs two calls a frame. The row map is ONE anchor plus a constant
			// pxPerTick, which is only true if NinjaTrader's own price scale is exactly linear over
			// the visible range. Measured on a zoomed-out ES chart, the heat sat up to 96 px BELOW
			// the candles — an error that grew linearly away from the anchor, which is the signature
			// of a wrong pxPerTick rather than a wrong anchor. Rather than infer that from
			// screenshots, ask NT8 where a price goes and compare. If _projErr is not ~0 the
			// projection is lying and the whole raster is misplaced.
			// Snap the probe to a tick. Without it the check compares the y of a ROUNDED tick
			// against the y of an unrounded price, so its own noise floor is pxTick/2 — which is
			// exactly what the first twenty readings showed (max |err| 3.9 px, every one of them
			// under half a tick). A metric whose noise is bigger than the defect it hunts is not
			// a metric.
			double probeP = Math.Round((cs.MinValue + (cs.MaxValue - cs.MinValue) * 0.85) / _tick) * _tick;
			int    ntY    = cs.GetYByValue(probeP) - panelY;
			float  ourY   = anchorY - ((float)Math.Round(probeP / _tick) - anchorRow) * pxPerTick;
			_projErr      = ourY - ntY;
			_projPxTick   = pxPerTick;
			_projSpan     = cs.MaxValue - cs.MinValue;

			return new RasterView(anchorRow, anchorY, pxPerTick, nowTicks, nowX, pxPerMs * ColumnRing.BucketMs, _tick);
		}

		private float  _projErr;      // our row map minus NT8's, in px, at 85% up the visible range
		private float  _projPxTick;
		private double _projSpan;

		// The projections above are only true on a linear price axis and an equidistant, time-based
		// x-axis. Where they are not, SizeMap draws nothing: a plausible lie about where liquidity sat
		// is worse than an empty panel, because the empty panel is the only one you can't trade off.
		private string Refusal(ChartControl cc, ChartScale cs, int pw, int ph)
		{
			if (cs.Properties.YAxisScalingType == YAxisScalingType.Logarithmic)
				return "log price axis — the single-anchor row map is linear and would misplace every row";
			if (cc.BarSpacingType == BarSpacingType.TimeBased)
				return "TimeBased bar spacing — x is not linear in bar slots and GetSlotIndexByTime throws";
			// The ramp is monotone only against a ground darker than its first stop. Above that,
			// "nothing resting" would render brighter than "8 lots resting" — the ramp lying about
			// its own direction. Refusing is honest; painting our own dark plate over a light chart
			// is not, because NT8 then draws its light-theme gridlines and dark candles on top of it.
			if (ChartBackground(cc) == 0)
				return "chart background is too light (or is a gradient) for the one dark ramp v1 ships — use a dark theme";
			if (_barMs <= 0)
				return "bar type " + BarsPeriod.BarsPeriodType + " has no intrabar time model — use Second or Minute bars";
			if (ChartBars.Bars == null || ChartBars.Bars.Count == 0 || ChartBars.ToIndex < 0)
				return "no bars loaded";
			if (pw > RenderTarget.MaximumBitmapSize || ph > RenderTarget.MaximumBitmapSize)
				return "panel " + pw + "x" + ph + " exceeds MaximumBitmapSize " + RenderTarget.MaximumBitmapSize;
			return null;
		}

		// ponytail: the refusal reason goes to the Output window, not onto the chart. Ceiling — a
		// refusing chart looks identical to a quiet one unless you open Output. Upgrade path — the
		// 5x7 bitmap font is Phase 2's first piece; when it lands this becomes one Text() call into
		// _px and the post-blit primitive count stays at exactly one.
		private void Refuse(string why)
		{
			if (why == _lastRefusal) return;
			_lastRefusal = why;
			Print("SizeMapHeat: drawing nothing — " + why);
			// Also say it ON the chart. A refusing panel is indistinguishable from a broken one, and
			// Javier's own projects run 150-tick and 30-second charts — dropping SizeMap on a tick
			// chart is the single most likely first contact with this indicator. The Output window
			// is not somewhere he has any reason to be looking.
			// This is the refusal path, so no bitmap is blitted and the "exactly one post-blit
			// primitive" budget is untouched.
			// ponytail: Draw.TextFixed, not the 5x7 raster font, which is Phase 2's first piece.
			try
			{
				NinjaTrader.NinjaScript.DrawingTools.Draw.TextFixed(
					this, "SizeMapRefusal", "SizeMap — " + why,
					NinjaTrader.NinjaScript.DrawingTools.TextPosition.BottomLeft,
					System.Windows.Media.Brushes.Gray, ChartControl.Properties.LabelFont,
					System.Windows.Media.Brushes.Transparent,
					System.Windows.Media.Brushes.Transparent, 0);
			}
			catch { }   // a drawing failure must never be worse than the thing it explains
		}

		// Returns 0 when the theme cannot carry the ramp, so the caller refuses instead of drawing.
		// Substituting our own dark ground here — the previous behaviour — meant filling the panel
		// with LUT index 0, i.e. painting an opaque near-black plate over a light chart and then
		// letting NT8 draw its light-theme gridlines and dark candles on top of it. Unreadable. And
		// the threshold was 0.18 while the ramp's actual precondition is Palette.MaxBackgroundLuminance
		// (0.0406), so every grey theme in between inverted the ramp silently.
		// ponytail: a light-theme ramp is a whole second palette, audited separately. Refusing is the
		// honest placeholder. Upgrade path — build the second ramp when Javier asks for a light chart.
		private uint ChartBackground(ChartControl cc)
		{
			uint bg = SolidBgra(cc.Properties.ChartBackground, 0);
			if (bg == 0) return 0;                                  // gradient/image: luminance unknowable
			return Palette.BackgroundIsUsable(bg) ? bg : 0;
		}

		private static uint SolidBgra(System.Windows.Media.Brush b, uint fallback)
		{
			System.Windows.Media.SolidColorBrush s = b as System.Windows.Media.SolidColorBrush;
			if (s == null) return fallback;   // gradient or image background: luminance is unknowable
			System.Windows.Media.Color c = s.Color;
			byte a = (byte)Math.Round(c.A * s.Opacity);   // brush Opacity is separate from Color.A
			return ((uint)a << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
		}

		private static double RelLum(uint argb)
		{
			return 0.2126 * Lin((argb >> 16) & 0xFF) + 0.7152 * Lin((argb >> 8) & 0xFF) + 0.0722 * Lin(argb & 0xFF);
		}

		private static double Lin(uint ch)
		{
			double v = ch / 255.0;
			return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
		}

		private static double BarMsOf(BarsPeriod bp)
		{
			if (bp == null || bp.Value <= 0) return 0;
			switch (bp.BarsPeriodType)
			{
				case BarsPeriodType.Second: return bp.Value * 1000.0;
				case BarsPeriodType.Minute: return bp.Value * 60000.0;
				// Tick / Volume / Range / Renko carry no intrabar time model at all, and Day+ has
				// nothing to say at 250 ms. Both are refused rather than approximated.
				default: return 0;
			}
		}

		// ---------------------------------------------------------------- device resources

		// Bitmap(RenderTarget, Size2, BitmapProperties), found by signature so no SharpDX.DXGI type
		// is ever named or resolved at compile time. Cached: the lookup runs once per session.
		private static System.Reflection.ConstructorInfo bitmapCtor;
		private static System.Reflection.ConstructorInfo BitmapCtor()
		{
			if (bitmapCtor != null) return bitmapCtor;
			foreach (System.Reflection.ConstructorInfo ci in typeof(SharpDX.Direct2D1.Bitmap).GetConstructors())
			{
				System.Reflection.ParameterInfo[] p = ci.GetParameters();
				if (p.Length == 3
					&& p[0].ParameterType.Name == "RenderTarget"
					&& p[1].ParameterType.Name == "Size2")
				{
					bitmapCtor = ci;
					return bitmapCtor;
				}
			}
			// Loud, not silent: a SharpDX shape change must surface at the first frame, not as a
			// blank chart nobody can explain.
			throw new InvalidOperationException(
				"SizeMap: Bitmap(RenderTarget, Size2, BitmapProperties) not found in SharpDX "
				+ typeof(SharpDX.Direct2D1.Bitmap).Assembly.GetName().Version);
		}

		private bool EnsureBitmap(int w, int h)
		{
			if (_bmp != null && !_bmp.IsDisposed && _bmpW == w && _bmpH == h) return true;
			ReleaseDeviceResources();

			_px = new int[w * h];
			// Taking the format from the target means no SharpDX.DXGI type is NAMED here.
			SharpDX.Direct2D1.BitmapProperties props =
				new SharpDX.Direct2D1.BitmapProperties(RenderTarget.PixelFormat);

			// But not naming one is not enough. `new Bitmap(...)` makes the compiler load every
			// Bitmap constructor for overload resolution, and one of them takes a SharpDX.DXGI.Surface
			// — so the plain `new` is CS0012 even though this file never mentions DXGI. Reflection
			// skips overload resolution entirely and needs no reference.
			//
			// ponytail: binds by signature at runtime. Ceiling — a SharpDX upgrade that changed this
			// constructor would fail at load instead of at compile. Upgrade path: add
			// bin/SharpDX.DXGI.dll in Editor > References and this becomes a plain `new`.
			_bmp = (SharpDX.Direct2D1.Bitmap)BitmapCtor().Invoke(
				new object[] { RenderTarget, new SharpDX.Size2(w, h), props });
			_bmpW = w; _bmpH = h;
			return _bmp != null;
		}

		private void ReleaseDeviceResources()
		{
			if (_bmp != null)
			{
				try { _bmp.Dispose(); } catch (Exception) { }
				_bmp = null;
			}
			_bmpW = _bmpH = 0;
		}

		// ---------------------------------------------------------------- what the chart says
		//
		// One set of numbers, two readouts. The raster HUD and the Output line are built from the
		// same helpers on purpose: two censuses that could disagree would make both untrustworthy,
		// and the census is the only tuning instrument K_mult has.
		private Chrome.HudInfo HudInfoOf(RasterView v, System.Collections.Generic.IReadOnlyList<RadarNode> nodes)
		{
			Chrome.HudInfo i = new Chrome.HudInfo();
			i.Instrument = Instrument != null ? Instrument.FullName : null;
			// NT8 exposes no "levels this feed was configured for", so DEPTH is left unset and the
			// badge prints OBS alone. A configured number we cannot read is not a number we may
			// print on the one line whose entire job is feed honesty.
			i.DepthLevels = 0;
			i.Observed = Volatile.Read(ref _levelsSeen);
			i.ColumnMs = ColumnMsOf(v);
			i.RowTicks = RowTicksOf(v);
			i.S0 = Volatile.Read(ref _s0);
			i.SCap = Volatile.Read(ref _sCap);
			i.WallsKnown = nodes != null;
			Census(nodes, out i.WallsLive, out i.WallsRemembered, out i.WallsFaint);
			// LAST frame's EMA: this frame is still being drawn, so its own time does not exist yet.
			// At 4 fps a 250 ms stale readout is the freshest honest one available.
			i.FrameMs = _emaMs;
			i.Fps = _fps;
			return i;
		}

		// WALLS live / remembered / faint. K_mult IS the design, and this census is its only tuning
		// instrument — spec §5.1's target band is 2-6 live and 10-25 remembered. A steady 0L is not
		// "a quiet market", it is a threshold set for the wrong instrument.
		private static void Census(System.Collections.Generic.IReadOnlyList<RadarNode> nodes,
		                           out int live, out int mem, out int faint)
		{
			// EXACTLY the renderer's predicate, and that is not a style point. These two disagreed:
			// the census counted `InWindow` alone while DrawWalls also demanded `State == Wall`, so
			// the HUD advertised a population 87% of which never reached a pixel — and a whole
			// verdict was reasoned about that population. A census must count what is drawn or it is
			// not a census, it is a second opinion. The buckets are exhaustive by construction.
			live = mem = faint = 0;
			if (nodes == null) return;
			for (int i = 0; i < nodes.Count; i++)
			{
				if (nodes[i].InWindow) live++;
				else if (nodes[i].Confidence >= 0.25) mem++;
				else faint++;
			}
		}

		// The honesty rule made mechanical: at 6 px bars a 250 ms bucket is 0.025 px, so 239 of 240
		// buckets are invisible and one wins by rounding. Say what a column and a row actually are.
		private static int ColumnMsOf(RasterView v)
		{
			return (int)(ColumnRing.BucketMs * Math.Max(1, Math.Ceiling(1.0 / v.PxPerBucket)));
		}

		private static int RowTicksOf(RasterView v)
		{
			return (int)Math.Max(1, Math.Ceiling(1.0 / v.PxPerTick));
		}

		//
		// Phase 1 replaced the estimate "~4.4 ms of a ~60 ms budget" with a number measured on
		// Javier's own panel and his own tape, and it went to the Output window because there was
		// no way to draw text into the raster yet. The 5x7 font landed in Phase 2 and the same
		// numbers are now ON the chart — but Print stays: the Output window is how a session gets
		// reported, and it carries PROJ, the ring counters and the recorder state, which are
		// diagnostics rather than HUD.
		private void Hud(int pw, int ph, RasterView v, double ms,
		                 System.Collections.Generic.IReadOnlyList<RadarNode> nodes)
		{
			_emaMs = _emaMs == 0 ? ms : _emaMs * 0.9 + ms * 0.1;

			// Measured, not assumed. NT8 caps chart repaint at ~4 fps, but a chart that is being
			// starved repaints slower and the HUD has to say so — a frame time of 1.4 ms at 0.8 fps
			// is a very different picture from 1.4 ms at 4 fps.
			long stamp = Stopwatch.GetTimestamp();
			if (_lastFrameStamp != 0)
			{
				double dt = (stamp - _lastFrameStamp) / (double)Stopwatch.Frequency;
				if (dt > 0.0005 && dt < 5.0)
					_fps = _fps == 0 ? 1.0 / dt : _fps * 0.9 + (1.0 / dt) * 0.1;
			}
			_lastFrameStamp = stamp;

			if (++_frame % HudEveryFrames != 0) return;

			int wLive, wMem, wFaint;
			Census(nodes, out wLive, out wMem, out wFaint);

			int columnMs = ColumnMsOf(v);
			int rowTicks = RowTicksOf(v);

			Print(string.Format(
				"SizeMapHeat {0}  {1:0.00}ms (ema {2:0.00}, {3:0.0}fps)  panel {4}x{5}  col {6}ms  row {7}t  s0 {8} cap {9}  obs {10}L  walls {11}L {12}R {13}F",
				Instrument.MasterInstrument.Name, ms, _emaMs, _fps, pw, ph, columnMs, rowTicks,
				Volatile.Read(ref _s0), Volatile.Read(ref _sCap), Volatile.Read(ref _levelsSeen), wLive, wMem, wFaint));

			// PROJ is the row map grading itself against NinjaTrader. err is how far our y for a
			// known price lands from where NT8 puts it, near the top of the visible range: ~0 means
			// the linear map holds, tens of px means every row on screen is in the wrong place.
			Print(string.Format(
				"   PROJ err {0:+0.0;-0.0;0.0}px  pxTick {1:0.000}  span {2:0.00}pts  anchor {3}@{4:0.0}",
				_projErr, _projPxTick, _projSpan, v.AnchorRow, v.AnchorY));

			Print(string.Format(
				"   ring pub {0} ovf {1} late {2}  epochs {3}  rec {4}",
				_ring.PublishedIndex, _ring.Overflows, _ring.LateDrops, Volatile.Read(ref _epochs),
				_rec == null ? "off"
					: string.Format("{0:0.0}MB drop {1} pend {2}{3}{4}",
						_rec.BytesWritten / 1048576.0, _rec.Dropped, _rec.Pending,
						_rec.Capped ? " CAPPED" : "", _rec.Failed ? " FAILED" : "")));

			string audit = Volatile.Read(ref _ladderAudit);
			if (audit != null) Print("   ladder " + audit);
		}

		private static bool IsPlayback()
		{
			try { return NinjaTrader.Cbi.Connection.PlaybackConnection != null; }
			catch (Exception) { return false; }
		}

		#region Properties
		[NinjaScriptProperty]
		// 390 = one RTH session (09:30-16:00), which is the number a trader actually asks for. The
		// ring is 408 B per 250 ms column (24 B header + 48 cells x 8 B), so the dial is linear in
		// memory: 30 min = 7200 col = 2.9 MB, 120 min = 28800 col = 11.7 MB, 390 min = 93600 col
		// = 38.2 MB. Allocated once at DataLoaded and never touched again.
		//
		// Frame time is NOT linear in this dial. It is linear in how many columns the PANEL shows,
		// which the rasterizer bounds explicitly. Measured on the harness, 1738x900 (the panel this
		// was profiled on), 26 levels per column, p50 of 30 frames:
		//
		//              span 100s      span 30m       span 266m      span 390m
		//   30 min     2.04 ms        6.06 ms        4.22 ms          -
		//   120 min    2.21 ms        6.15 ms       13.66 ms          -
		//   390 min    2.04 ms        6.34 ms       29.70 ms       41.89 ms (max 52.8)
		//
		// Read the top row: capacity is FREE at the zoom this instrument is for. It only costs when
		// the chart is zoomed out far enough to put the whole ring on the panel, and then it is not
		// waste — every one of those columns is drawn. Cost is ~0.45 us per on-panel column, so the
		// real wall is the zoom: a whole session shown at once is ~42 ms of a ~60 ms budget, which
		// is why 390 is the top of the range and not 1440.
		[Range(1, 390)]
		[Display(Name = "Minutes of history", Description = "Depth of the column ring. 30 min = 7200 columns ~= 2.9 MB; 390 min = one RTH session ~= 38 MB. Frame cost follows the zoom, not this dial.", Order = 1, GroupName = "SizeMap")]
		public int MinutesOfHistory { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Record raw tape", Description = "Write the depth/tape corpus to Documents/NinjaTrader 8/SizeMap. Depth has no history — a day not recorded is a day that can never be calibrated against.", Order = 2, GroupName = "SizeMap")]
		public bool RecordRaw { get; set; }

		[NinjaScriptProperty]
		[Range(1.0, 20.0)]
		[Display(Name = "Wall size multiple (K)", Description = "How many times the median level a resting size must be to count as a wall. PER INSTRUMENT: measured 1.5 for ES (rests ~70 lots/level), 4.0 for NQ. Watch the WALLS token in the HUD — the design band is 2-6 live and 10-25 remembered. A steady 0L is this dial, not a quiet market.", Order = 4, GroupName = "SizeMap")]
		public double WallSizeMultiple { get; set; }

		[NinjaScriptProperty]
		[Range(1, 10000)]
		[Display(Name = "Minimum wall lots", Description = "Absolute floor a wall must clear as well as the multiple, so a dead-thin book cannot promote a 6-lot level.", Order = 5, GroupName = "SizeMap")]
		public int MinWallLots { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show legend", Description = "The 196x78 key in the bottom-LEFT corner (the right side is where the newest bars are, and the candles paint through it). It hides itself on a panel under 500x300 regardless.", Order = 3, GroupName = "SizeMap")]
		public bool ShowLegend { get; set; }
		#endregion
	}
}
