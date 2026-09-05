# SizeMapZoneStrategy — not portable to PropSim

## Reason

`SizeMapZoneStrategy`'s only entry signal is a "zone": a price level the engine
confirmed as a **wall** in the Level 2 order book (resting size far above the
median level, tracked over time), that price then departed from by N ticks and
returned to. That signal comes entirely from `OnMarketDepth` (the MBP depth
ladder) via `BookMirror` -> `WallTracker` -> `RadarNode`, not from the trade
tape. See `nt8/SizeMapZoneStrategy.cs:24-27` (file header): "Queue position is
invisible in NinjaScript... BookMirror is a positional ladder mirror" and
`FindArmableZone`/`ArmZone` (lines 559-657), which only ever consult
`Volatile.Read(ref _nodes)` -- wall nodes produced by the depth-only
`WallTracker.Update(_book, now)` in `MaybeRunEngine` (lines 365-383).

PropSim's `tape` gives every print with size and aggressor side (`tape["vol"]`,
`tape["side"]`) but carries **no resting order-book depth** -- no bid/ask
ladder, no per-level size, no wall concept at all (confirmed against
`plugins.py --template`'s tape schema: `ts, px, vol, side` only). There is no
tape-derived proxy for "a resting wall of size N at price P persisted for M
seconds" -- that is a DOM-only fact.

This is confirmed independently by the strategy's own file header and the
project README (`README.md` "Limits"): *"It cannot be backtested. `OnMarketDepth`
never fires on historical bars, so the heatmap is blank and the strategy never
arms. Market Replay or a live/Sim connection only."* If NT8's own Strategy
Analyzer cannot backtest this strategy on historical bars, a tape-only replay
engine cannot either -- the missing input (L2 depth) is the same in both cases.

`BookMirror` also has trade-only methods (`ApplyTrade`, `AggressorDelta`,
`TradedAt`, `RecentAlternations`) that *are* tape-portable in principle, but
`SizeMapZoneStrategy` does not use any of them for its entry/exit logic -- only
the depth-derived `WallTracker` nodes.

## Verdict

Not portable. Falls under the explicit L1 quotes / DOM exclusion. No plugin
written; nothing to selfcheck.
