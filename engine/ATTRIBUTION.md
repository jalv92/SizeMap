# Where this engine came from

`Primitives.cs`, `RadarConfig.cs`, `BookMirror.cs`, `WallDetector.cs`, `WallTracker.cs`,
`LiquidityMemory.cs`, `EpisodeClassifier.cs`, `ConsumptionTracker.cs`, `DepthBaseline.cs`
and `BigPrintTracker.cs` are vendored from **Trading-radar** (`jalv92/Trading-radar`),
commit `26ee906`, and are byte-identical to it except for one line each: the namespace,
rewritten `TradingRadar.Engine` -> `SizeMap.Engine`.

That rewrite is not cosmetic. `TradingRadar.Engine` is already compiled into the live
`NinjaTrader.Custom.dll` at `Custom/AddOns/LiquidityRadar/`, and NinjaTrader compiles
every `.cs` under `Custom/` into **one** assembly. Shipping these files under their
original namespace would be CS0101 for every type in them — taking down not just
SizeMap but every indicator and strategy on the machine.

The fork is **one-way**. Trading-radar and SizeMap diverge from here; there is no sync
back. The tests came across with the code so the vendored half stays verified on its own.
