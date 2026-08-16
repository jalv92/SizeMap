using System;
using System.Collections.Generic;

namespace SizeMap.Engine
{
    // Mirrors the positional MBP depth stream and a short ring of recent trades.
    // Pure: no NT, no clock. All time arrives via event/parameter timestamps.
    public class BookMirror
    {
        private struct Trade { public double Price; public long Volume; public DateTime Time; public Side Aggressor; }

        private readonly double _tick;
        private readonly TimeSpan _tradeRetention;
        // Safety bound only, NOT a feed assumption: the ladder is however deep the feed sends.
        // NT Brokerage/Continuum tops out near 10, Rithmic delivers 40+. Spec §47: never
        // hardcode ladder size. This exists purely so a misbehaving stream cannot grow the list
        // without limit.
        //
        // 2026-08-16, 6 PM ET: the old 128 stopped being a safety bound and became a measurement.
        // The HUD read `obs 128L` on ES, which is this cap saturating, not the feed reporting -- so
        // the one readout whose entire job is feed honesty was reporting my own ceiling back at me.
        // Three other fields agreed the feed had genuinely deepened that session: `walls 13L 0R 0F`
        // (nothing outside the window left to remember, against 6L 2R 4F that morning) and the
        // colour scale collapsing from s0 23 / cap 752 to s0 7 / cap 224 as the sampled size
        // distribution filled up with thin far levels.
        //
        // ponytail: 512 is the next bound, not a measured need. If `obs` ever prints 512, distrust
        // it exactly the same way and raise this again -- a saturated cap always looks like a
        // real number.
        private readonly int _maxLevels;
        // Bids kept descending by price, asks ascending — same order NT delivers by Position.
        private readonly List<DepthLevel> _bids = new List<DepthLevel>();
        private readonly List<DepthLevel> _asks = new List<DepthLevel>();
        private readonly List<Trade> _trades = new List<Trade>();

        public BookMirror(double tickSize, TimeSpan tradeRetention, int maxLevels = 512)
        {
            _tick = tickSize;
            _tradeRetention = tradeRetention;
            _maxLevels = maxLevels < 1 ? 1 : maxLevels;
        }

        // How deep this mirror will hold. Read it instead of assuming a ladder size.
        public int MaxLevels { get { return _maxLevels; } }

        public TimeSpan TradeRetention { get { return _tradeRetention; } }

        private List<DepthLevel> SideList(Side s) { return s == Side.Bid ? _bids : _asks; }
        private bool SamePrice(double a, double b) { return Math.Abs(a - b) < _tick / 2.0; }

        /// Is this price inside the range the ladder can currently SEE on this side?
        ///
        /// This is the distinction that cost Phase 3 its answer. Every consumer asks the book for
        /// the size at a price and gets 0 for anything absent — but 0 means two completely
        /// different things: "this level emptied" and "this level is past the far end of a window
        /// that only holds N levels". Measured on 5.8 M records of ES:
        ///
        ///   pos 0..9   Remove: 78,876 + 173 + ... , size 0 on 100.0% of them   -> genuinely gone
        ///   pos 10     Remove: 79,188,              size > 0 on 100.0% of them -> scrolled out
        ///
        /// and pos 9 took 79,192 Adds against those 79,188 removals: one level in at the far end
        /// pushes one out. That is the window sliding, not liquidity dying — and 89-92% of those
        /// levels were back within one second holding >= 90% of the size they left with, a median
        /// of 230 lots still resting. It is why `PULLED` fired once in 11,325 episodes: a wall
        /// scrolling out is indistinguishable from a wall being cancelled unless you ask this.
        ///
        /// Derived from the ladder's own extent rather than from `pos == 10`, so it stays true when
        /// the feed goes to 40 levels and the far end moves from ~2.25 points to ~10.
        ///
        /// ponytail: says nothing about a price the book has never quoted on this side — empty
        /// ladder returns false. Ceiling — right after a reset everything reads out-of-window, which
        /// is the honest answer while the book rebuilds.
        public bool IsWithinWindow(Side side, double price)
        {
            var list = SideList(side);
            if (list.Count == 0) return false;
            double best = list[0].Price;
            double far = list[list.Count - 1].Price;
            double half = _tick / 2.0;
            return side == Side.Bid
                ? price <= best + half && price >= far - half
                : price >= best - half && price <= far + half;
        }

        public void ApplyDepth(DepthEvent e)
        {
            if (e.IsReset) { _bids.Clear(); _asks.Clear(); return; }
            var list = SideList(e.Side);
            switch (e.Op)
            {
                case DepthOp.Add:
                    {
                        int pos = e.Position < 0 ? 0 : (e.Position > list.Count ? list.Count : e.Position);
                        list.Insert(pos, new DepthLevel { Price = e.Price, Volume = e.Volume });
                        if (list.Count > MaxLevels) list.RemoveAt(list.Count - 1);
                        break;
                    }
                case DepthOp.Update:
                    {
                        if (e.Position >= 0 && e.Position < list.Count)
                            list[e.Position] = new DepthLevel { Price = e.Price, Volume = e.Volume };
                        break;
                    }
                case DepthOp.Remove:
                    {
                        if (e.Position >= 0 && e.Position < list.Count)
                            list.RemoveAt(e.Position);
                        break;
                    }
            }
        }

        public void ApplyTrade(TradeEvent t)
        {
            Side aggressor = InferAggressor(t.Price);
            _trades.Add(new Trade { Price = t.Price, Volume = t.Volume, Time = t.Time, Aggressor = aggressor });
            // Prune relative to the newest trade time (deterministic, no clock).
            DateTime cutoff = t.Time - _tradeRetention;
            int i = 0;
            while (i < _trades.Count && _trades[i].Time < cutoff) i++;
            if (i > 0) _trades.RemoveRange(0, i);
        }

        // Last >= best ask => buy aggressor (lifted the offer); Last <= best bid => sell aggressor.
        private Side InferAggressor(double price)
        {
            if (_asks.Count > 0 && price >= _asks[0].Price - _tick / 2.0) return Side.Ask; // hit the ask = buy aggressor
            if (_bids.Count > 0 && price <= _bids[0].Price + _tick / 2.0) return Side.Bid; // hit the bid = sell aggressor
            // Inside the spread / unknown: attribute by nearest touch.
            if (_asks.Count > 0 && _bids.Count > 0)
                return Math.Abs(price - _asks[0].Price) <= Math.Abs(price - _bids[0].Price) ? Side.Ask : Side.Bid;
            return Side.Ask;
        }

        public void ResetFromSnapshot(IList<DepthLevel> bids, IList<DepthLevel> asks)
        {
            _bids.Clear(); _asks.Clear();
            if (bids != null) _bids.AddRange(bids);
            if (asks != null) _asks.AddRange(asks);
            // Same bound as the incremental path, or a snapshot could seed a deeper book
            // than Add/Update/Remove is allowed to maintain.
            if (_bids.Count > _maxLevels) _bids.RemoveRange(_maxLevels, _bids.Count - _maxLevels);
            if (_asks.Count > _maxLevels) _asks.RemoveRange(_maxLevels, _asks.Count - _maxLevels);
        }

        public IReadOnlyList<DepthLevel> Levels(Side side) { return SideList(side); }

        public bool TryBestBid(out DepthLevel best)
        {
            if (_bids.Count > 0) { best = _bids[0]; return true; }
            best = default(DepthLevel); return false;
        }

        public bool TryBestAsk(out DepthLevel best)
        {
            if (_asks.Count > 0) { best = _asks[0]; return true; }
            best = default(DepthLevel); return false;
        }

        public long MedianSize(Side side) { return MedianOf(SideList(side), double.NaN); }

        public long MedianSizeExcluding(Side side, double price) { return MedianOf(SideList(side), price); }

        private long MedianOf(List<DepthLevel> list, double excludePrice)
        {
            var v = new List<long>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                if (!double.IsNaN(excludePrice) && SamePrice(list[i].Price, excludePrice)) continue;
                v.Add(list[i].Volume);
            }
            if (v.Count == 0) return 0;
            v.Sort();
            int mid = v.Count / 2;
            // Even count: lower-middle (floor) — conservative baseline, matches test expectations.
            return (v.Count % 2 == 1) ? v[mid] : v[mid - 1];
        }

        public long TradedAt(double price, DateTime since, Side? aggressorFilter)
        {
            long sum = 0;
            for (int i = 0; i < _trades.Count; i++)
            {
                var tr = _trades[i];
                if (tr.Time < since) continue;
                if (!SamePrice(tr.Price, price)) continue;
                if (aggressorFilter.HasValue && tr.Aggressor != aggressorFilter.Value) continue;
                sum += tr.Volume;
            }
            return sum;
        }

        // Running order-flow imbalance: buy-aggressor volume minus sell-aggressor volume
        // over retained trades with Time >= since. Side.Ask aggressor = buy (lifted offer).
        public long AggressorDelta(DateTime since)
        {
            long buy = 0, sell = 0;
            for (int i = 0; i < _trades.Count; i++)
            {
                Trade tr = _trades[i];
                if (tr.Time < since) continue;
                if (tr.Aggressor == Side.Ask) buy += tr.Volume; else sell += tr.Volume;
            }
            return buy - sell;
        }

        public Side AggressorOf(double price) { return InferAggressor(price); }

        public struct TapeWindow { public int Prints; public long BuyVol; public long SellVol; }

        public TapeWindow WindowSince(DateTime since)
        {
            TapeWindow w = new TapeWindow();
            for (int i = 0; i < _trades.Count; i++)
            {
                Trade tr = _trades[i];
                if (tr.Time < since) continue;
                w.Prints++;
                if (tr.Aggressor == Side.Ask) w.BuyVol += tr.Volume; else w.SellVol += tr.Volume;
            }
            return w;
        }

        // Aggressor sign changes across the last `lookback` retained trades (oldest->newest).
        public int RecentAlternations(int lookback)
        {
            if (lookback <= 0) return 0;
            int n = _trades.Count;
            int start = lookback >= n ? 0 : n - lookback;
            int alts = 0;
            bool have = false; Side prev = Side.Ask;
            for (int i = start; i < n; i++)
            {
                Side a = _trades[i].Aggressor;
                if (have && a != prev) alts++;
                prev = a; have = true;
            }
            return alts;
        }
    }
}
