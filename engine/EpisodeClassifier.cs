using System;
using System.Collections.Generic;

namespace SizeMap.Engine
{
    public struct EpisodeResult
    {
        public Side Side;
        public double Price;
        public Outcome Outcome;
        public long Traded;
        public long Cancelled;
        public DateTime ResolvedAt;
    }

    public struct ErosionRead
    {
        public Side Side;
        public double Price;
        public long SizeAtOpen;
        public long Displayed;
        public long Traded;
        public long Cancelled;
        public double Frac;        // cancelled / sizeAtOpen — drop NOT explained by trades
        public bool Approaching;   // quote still >= D_pull ticks away and not crossed
    }

    // Attributes every size decrease at a tracked price by cross-referencing Last prints.
    // Stateful per open episode; pure (no clock, no NT).
    public class EpisodeClassifier
    {
        private class Episode
        {
            public Side Side;
            public double Price;
            public long SizeAtOpen;
            public DateTime OpenTime;
            // Crossed is a latch, but for a resolved episode it is also the instant read: Update()
            // resolves on the same pass that first sees displayed == 0. No CrossedAtVanish field —
            // it was added, measured identical to this on 5,622 episodes, and removed.
            public bool Crossed;           // inside quote ever crossed P
            public bool QuoteAwayAtVanish; // quote was >= D_pull ticks away when size hit 0
            public bool Vanished;
        }

        private readonly RadarConfig _cfg;
        private readonly double _tick;
        private readonly Dictionary<long, Episode> _open = new Dictionary<long, Episode>();
        private readonly Queue<EpisodeResult> _resolved = new Queue<EpisodeResult>();

        public EpisodeClassifier(RadarConfig cfg) { _cfg = cfg; _tick = cfg.TickSize; }

        private long Key(Side s, double price) { return ((long)Math.Round(price / _tick)) * 2 + (s == Side.Ask ? 1 : 0); }
        private TimeSpan Scaled(TimeSpan ts) { return TimeSpan.FromTicks((long)(ts.Ticks * _cfg.VolGovernor)); }
        private Side ConsumingAggressor(Side wallSide) { return wallSide == Side.Ask ? Side.Ask : Side.Bid; }

        public bool HasOpenEpisode(Side side, double price) { return _open.ContainsKey(Key(side, price)); }

        public void OnApproach(Side side, double price, long sizeAtOpen, DateTime now)
        {
            long k = Key(side, price);
            if (_open.ContainsKey(k)) return;
            _open[k] = new Episode { Side = side, Price = price, SizeAtOpen = sizeAtOpen, OpenTime = now };
        }

        public void Update(BookMirror book, DateTime now)
        {
            if (_open.Count == 0) return;
            var toResolve = new List<Episode>();
            foreach (var ep in _open.Values)
            {
                long displayed = CurrentVolume(book, ep.Side, ep.Price);
                bool crossedNow = QuoteCrossed(book, ep.Side, ep.Price);
                if (crossedNow) ep.Crossed = true;

                if (displayed == 0 && !ep.Vanished)
                {
                    ep.Vanished = true;
                    ep.QuoteAwayAtVanish = QuoteTicksAway(book, ep.Side, ep.Price) >= _cfg.D_pull;
                }

                bool timedOut = now - ep.OpenTime >= Scaled(_cfg.T_episode);
                if (displayed == 0 || ep.Crossed || timedOut) toResolve.Add(ep);
            }

            foreach (var ep in toResolve)
            {
                EpisodeResult r;
                if (TryClassify(ep, book, now, out r)) _resolved.Enqueue(r);
                _open.Remove(Key(ep.Side, ep.Price));
            }
        }

        // Returns true only when a real condition fired (spec §6.3). Ambiguous timeouts
        // (no trades, no cancellation, no cross) produce no outcome — memory is untouched.
        private bool TryClassify(Episode ep, BookMirror book, DateTime now, out EpisodeResult r)
        {
            long displayed = CurrentVolume(book, ep.Side, ep.Price);
            long drop = Math.Max(0, ep.SizeAtOpen - displayed);
            // ponytail: trade attribution currently sums over the whole episode lifetime, not within
            // W_assoc of each size decrease (spec §6.3); W_assoc is reserved and will be wired +
            // calibrated during Market Replay testing via a debug data-capture path (TODO).
            long traded = book.TradedAt(ep.Price, ep.OpenTime, ConsumingAggressor(ep.Side));
            long cancelled = Math.Max(0, drop - traded);

            Outcome o;
            // PHASE 3, and do not re-litigate this without reading the numbers.
            //
            // `Consumed` winning on `ep.Crossed` alone is why PULLED fired once in 7,454 episodes.
            // Two fixes were implemented and measured on three distinct ES tapes at the shipped 4 Hz
            // (docs/verdict, 5,622 episodes):
            //
            //  A. Test Pulled FIRST when the level emptied and the quote had not crossed AT THAT
            //     INSTANT. Zero label changes — 1434/1434, 695/695, 3493/3493 identical. It cannot
            //     fire: Update() resolves an episode in the SAME pass that sets Vanished, so
            //     `ep.Crossed` at resolution IS the cross at the vanish instant. A latch set in an
            //     earlier pass would have resolved the episode in that earlier pass. Pinned by
            //     Crossed_latching_resolves_in_the_same_pass_so_reordering_cannot_help.
            //  B. Arm episodes at D_approach = 2 instead of 1. Zero new pulls, and refusals went
            //     37.7/33.4/46.0% -> 56.5/53.5/64.0%. Deaths observed barely moved (775->794,
            //     416->428, 1704->1720): the set of wall deaths is a property of the tape, not of the
            //     arming distance, and every added episode timed out without seeing one.
            //
            // The real blocker is that QuoteCrossed(Ask, P) is `bestAsk > P`, which removing the
            // level at P makes true by construction — it restates the level's own death instead of
            // reporting a market fact. Emptied-and-crossed stayed 99.9-100.0% under EVERY variant.
            // Before anyone rewrites it: the 15-21% of deaths that are majority-cancelled were
            // measured for forward information (mid at +10/+30/+60 s vs the trade-explained deaths)
            // and carry NONE — 9 tests, every p >= 0.14, signs disagreeing across tapes. Making the
            // glyph reachable is cheap; making it mean something is the part that is not done.
            if (ep.Crossed)
                o = Outcome.Consumed;
            else if (traded >= _cfg.A_absorb * ep.SizeAtOpen
                     && traded >= _cfg.RefillRatioTrigger * Math.Max(drop, 1))
                o = Outcome.Absorbed;
            else if (cancelled > traded && ep.QuoteAwayAtVanish)
                o = Outcome.Pulled;
            else
            {
                r = default(EpisodeResult);
                return false;   // ambiguous: do not touch memory
            }

            r = new EpisodeResult { Side = ep.Side, Price = ep.Price, Outcome = o, Traded = traded, Cancelled = cancelled, ResolvedAt = now };
            return true;
        }

        public bool TryTakeResolved(out EpisodeResult r)
        {
            if (_resolved.Count > 0) { r = _resolved.Dequeue(); return true; }
            r = default(EpisodeResult); return false;
        }

        // Per open episode: how much of the size drop is unexplained by trades (cancellation)
        // while the quote is still approaching. Frac>0 & Approaching = partial pull (spec §7).
        public IReadOnlyList<ErosionRead> ErosionReads(BookMirror book, DateTime now)
        {
            var outl = new List<ErosionRead>();
            foreach (var ep in _open.Values)
            {
                long displayed = CurrentVolume(book, ep.Side, ep.Price);
                long drop = Math.Max(0, ep.SizeAtOpen - displayed);
                long traded = book.TradedAt(ep.Price, ep.OpenTime, ConsumingAggressor(ep.Side));
                long cancelled = Math.Max(0, drop - traded);
                bool approaching = QuoteTicksAway(book, ep.Side, ep.Price) >= _cfg.D_pull
                                   && !QuoteCrossed(book, ep.Side, ep.Price);
                double frac = ep.SizeAtOpen > 0 ? (double)cancelled / ep.SizeAtOpen : 0.0;
                outl.Add(new ErosionRead
                {
                    Side = ep.Side, Price = ep.Price, SizeAtOpen = ep.SizeAtOpen,
                    Displayed = displayed, Traded = traded, Cancelled = cancelled,
                    Frac = frac, Approaching = approaching
                });
            }
            return outl;
        }

        private long CurrentVolume(BookMirror book, Side side, double price)
        {
            var levels = book.Levels(side);
            for (int i = 0; i < levels.Count; i++)
                if (Math.Abs(levels[i].Price - price) < _tick / 2.0) return levels[i].Volume;
            return 0;
        }

        // For an Ask wall: crossed if best ask moved above P. For a Bid wall: best bid below P.
        private bool QuoteCrossed(BookMirror book, Side side, double price)
        {
            if (side == Side.Ask)
                return book.TryBestAsk(out var a) && a.Price > price + _tick / 2.0;
            return book.TryBestBid(out var b) && b.Price < price - _tick / 2.0;
        }

        private int QuoteTicksAway(BookMirror book, Side side, double price)
        {
            DepthLevel q;
            bool has = side == Side.Ask ? book.TryBestBid(out q) : book.TryBestAsk(out q);
            if (!has) return int.MaxValue;
            return (int)Math.Round(Math.Abs(price - q.Price) / _tick);
        }
    }
}
