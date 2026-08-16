Read all of it: the 19 Engine files (9 target + 3 optional), `RadarTab.cs` 511-665 + `HandleReplayReset` 1123-1153, `BigPrints.cs` / `BigPrintsRecorder.cs` / `BigPrintsDiscriminator.cs`, the SharpDX probe, and the live `Custom/` tree. The PNG encoder in §5 is not a sketch — I compiled and ran it (`dotnet run`, CRC + zlib validated by an independent Python decoder, image inspected).

---

# SizeMap — file-by-file technical architecture

## 0. Collision facts (verified on disk, not assumed)

| Namespace / type | Lives in live `Custom.dll` at | Consequence |
|---|---|---|
| `TradingRadar.Engine` (all 19 types) | `Custom/AddOns/LiquidityRadar/*.cs` | Copying any Engine file **unchanged** = CS0101 → entire `NinjaTrader.Custom.dll` fails → LiquidityRadar + BigPrints + PatternZone + VeeSnap + 20 strategies all die. **Namespace MUST be rewritten.** |
| `BigPrints`, `BigPrintsRecorder`, `BigPrintsAiClient` (`NinjaTrader.NinjaScript.Indicators`) | `Custom/Indicators/BigPrints*.cs` | **Do not copy any BigPrints file at all.** `internal` gives zero protection — `Custom.dll` is ONE assembly, so `internal class BigPrintsRecorder` is as collidable as a public one. |
| `BigPrintsDiscriminator`, `DiscriminatorLog` (`NinjaTrader.NinjaScript.Strategies`) | `Custom/Strategies/` | Same. Not copied. |
| `SizeMap*` anything | nothing | Free. `Custom/Indicators/` and `Custom/Strategies/` contain no `SizeMap*` file. |

Chosen namespaces: **`SizeMap.Engine`** for everything portable, **`NinjaTrader.NinjaScript.Indicators`** for the 3 NT-bound files (whose type names all start `SizeMap`).

---

## 1. Repo tree

`/home/javlo/Code Projects/main-project/projects/Trading/SizeMap/` (currently empty, zero commits)

```
SizeMap/
├── README.md                              readme-craft pass, screenshots, the honesty rules      ~180
├── LICENSE                                MIT (matches BigPrints)                                  21
├── .gitignore                             bin/ obj/ build/.stage/ *.smr *.smc out/                 12
├── SizeMap.slnx                           Engine + Tests + Harness                                  9
│
├── Engine/                                netstandard2.0, LangVersion 7.3, zero deps
│   ├── SizeMap.Engine.csproj                                                                        9
│   ├── ATTRIBUTION.md                     what came from Trading-radar, at which commit            ~25
│   │   ── vendored from Trading-radar, namespace rewritten, otherwise byte-identical ──
│   ├── Primitives.cs                      Side/DepthOp/NodeState/Outcome, DepthEvent, RadarNode     59
│   ├── RadarConfig.cs                     every threshold; no literal lives in logic                46
│   ├── BookMirror.cs                      positional MBP ladder + trade ring + aggressor inference 195
│   ├── WallDetector.cs                    4-criteria confirmation (K_mult, MinAbs, persist, flicker)149
│   ├── WallTracker.cs                     the single engine entry point; emits RadarNode[]         150
│   ├── LiquidityMemory.cs                 Confidence decay, NodeState, the memory band             167
│   ├── EpisodeClassifier.cs               Absorbed/Pulled/Consumed **or refuses to classify**       175
│   ├── ConsumptionTracker.cs              Fraction / Drop / Traded / **TradeBackedFraction**         28
│   ├── DepthBaseline.cs                   rolling P85 of observed level sizes                        56
│   │   ── new, SizeMap-only ──
│   ├── SizeMapConfig.cs                   BucketMs=250, RingColumns=7200, CellsPerColumn=48, caps   ~55
│   ├── ColumnRing.cs                      the time×price×size ring (§3)                            ~185
│   ├── WallMarkRing.cs                    wall objects with birth/death in time — the mark layer    ~125
│   ├── Rasterizer.cs                      **pure** ColumnRing → int[] (§4)                          ~225
│   ├── Palette.cs                          builds the 256-entry premultiplied BGRA LUT + categoricals ~95
│   ├── PrintCluster.cs                    BigPrints' cluster rule, ported (§2c)                      ~75
│   └── RawRecord.cs                       the 16-byte packed struct + read/write (§7a)               ~80
│
├── NinjaTrader/                           the only files that touch NT8 / SharpDX
│   ├── SizeMapHeat.cs                     the Indicator: OnMarketDepth/Data/Render (§10)           ~520
│   ├── SizeMapRecorder.cs                 off-thread raw + column writer (§7)                      ~265
│   └── SizeMapWarmStart.cs                reads .smc back into a ColumnRing on DataLoaded (§7b)    ~110
│
├── Harness/                               net8.0 console — visual iteration at `dotnet run` speed
│   ├── SizeMap.Harness.csproj                                                                       11
│   ├── Program.cs                         .smr/.smc → ColumnRing → Rasterize → PNG                  ~65
│   └── Png.cs                             zero-dependency PNG encoder (§5, compiled + verified)      58
│
├── Tests/                                 xunit, mirrors Trading-radar/Tests
│   ├── SizeMap.Tests.csproj                                                                         16
│   ├── ColumnRingTests.cs                                                                          ~230
│   ├── RasterizerTests.cs                                                                          ~260
│   ├── RecorderRoundTripTests.cs                                                                   ~120
│   ├── PrintClusterTests.cs                                                                         ~70
│   └── golden/ramp.png, golden/two-walls.png     checked-in raster goldens                           —
│
├── build/
│   ├── stage-custom.sh                    stage SizeMap **+ live LiquidityRadar + BigPrints** for nt8c  38
│   └── deploy.sh                          §9                                                         72
│
└── docs/
    ├── design.md                          this document, kept current                              ~400
    └── palette.md                         every hex + its 0xAARRGGBB, ramp validator output        ~110
```

**Vendored total: 1,025 lines, unmodified except one `namespace` line each.** New code: ~1,290 lines engine + NT, ~680 tests, ~135 harness.

**Not taken, and why** (the ladder ran):
- `TapeSpeed` / `TapeAcceleration` — z-scores are strategy inputs. Nothing on a heatmap renders a z-score. Add when a HUD asks for one.
- `BigPrintTracker` — `Net(now)` is a signed scalar for a controller, not a visual. The cluster *rule* is what SizeMap wants; that's `PrintCluster.cs`, 75 new lines, no vendoring.
- `PressureModel`, `AbsorbController`, `ReactiveController`, `ControllerStateMachine`, `CockpitBanner`, `InstrumentPresets`, `ReactiveExecution` — all execution-side. SizeMap does not trade.

---

## 2. Copy manifest

### (a) Trading-radar Engine → SizeMap/Engine — 9 files

| Source | Destination | Change |
|---|---|---|
| `projects/Trading/Trading-radar/Engine/{Primitives,RadarConfig,BookMirror,WallDetector,WallTracker,LiquidityMemory,EpisodeClassifier,ConsumptionTracker,DepthBaseline}.cs` | `projects/Trading/SizeMap/Engine/<same>.cs` | **`sed 's/^namespace TradingRadar\.Engine$/namespace SizeMap.Engine/'`** — nothing else. No reformatting, no field pruning, no "cleanup". |

That is the whole change. Rationale for touching nothing else: the 9 files carry ~1,000 lines of comments recording *why* each constant is what it is (the `dC_confirm` blind→live rule, the even-count median floor choice, `TrustedSize`'s 95-96% phantom-abandon incident, `EpisodeClassifier.TryClassify` returning `false` on ambiguity). Every edit is a chance to lose one, and a future re-sync from Trading-radar becomes a `sed` + `diff` instead of a merge.

`RadarConfig` keeps its React/controller fields (`A_absorb`, `RefillRatioTrigger`, `P_max`…) even though SizeMap only reads about half. Deleting them is a merge conflict generator for zero bytes saved.

`ATTRIBUTION.md` records the Trading-radar commit SHA the 9 files came from, so `git diff` against upstream stays meaningful.

### (b) BigPrints → SizeMap — **zero files copied**

Every BigPrints type name is already taken in `Custom.dll` (§0). What is reused is the *logic*, re-typed into `SizeMap.Engine`:

**The cluster rule** (`BigPrints.cs` 546-612), verbatim semantics, into `PrintCluster.cs`:

```
aggressor:   price >= ask -> buy ;  price <= bid -> sell ;  strictly inside -> NOT an aggressor
fold into the open cluster iff:
    isBuy == _clusterIsBuy
 && (t - _clusterLastTime).TotalMilliseconds  <= ClusterMilliseconds   //  150   (SetDefaults, line 228)
 && (t - _clusterStartTime).TotalMilliseconds <= MaxClusterSpanMs      // 1500   (const,       line  74)
otherwise: finalize the open cluster, open a new one on this print
emit iff  _clusterVolume >= MinVolume                                  //  150   (SetDefaults, line 226)
```

Also ported, because they are hard-won:
- **The epoch guard** (`BigPrints.cs` 533-542): `if (_lastDetectorTime != default(DateTime) && e.Time < _lastDetectorTime.AddSeconds(-2))` → wipe detector memory. The `default(DateTime)` test is **load-bearing**: `DateTime.MinValue.AddSeconds(-2)` throws, and this line runs before every other guard. A Playback slider rewind does *not* re-run `DataLoaded`, so without this the cluster window splices two tape epochs.
- **`MaxClusterMemory = 300`** (count cap, not time cap) with the note that 50 caused a silent accumulation undercount on busy tape.
- The other BigPrints constants for reference, **not** ported (they belong to the accumulation/stop-run detectors SizeMap has no use for): `AccumMinClusters=3`, `AccumWindowSec=180`, `StopRunTicks=40`, `StopRunWindowSec=10`, `AutoMaxFilesPerSession=40`, `SoundCooldownMs=750`.

`BigPrintsDiscriminator.cs` — read; **nothing taken**. It is a post-hoc Reversal/Continuation verdict engine (`Verdict`, `Context`, `SecBar`, `Outcome`) for a strategy. A heatmap does not need a verdict, and `internal class BigPrintsDiscriminator` in `NinjaTrader.NinjaScript.Strategies` is a live type name.

### (c) RadarTab.cs → SizeMapHeat.cs — the 40 lines that must be ported

Not copied (RadarTab is an AddOn tab, SizeMapHeat is an Indicator), **re-typed with the comments intact**:

| Source | What | Where it lands |
|---|---|---|
| `RadarTab.cs` 581-605 | `OnMarketDepth` mapping: `IsReset` first, `Price <= 0` reject, `Operation → DepthOp`, and the **`if (e.Instrument != _instrument) return;` inside the lock** (stale event from a prior instrument, dropped *before* touching the book) | `SizeMapHeat.OnMarketDepth` |
| `RadarTab.cs` 607-623 | `OnMarketData`: `MarketDataType.Last` + `Price > 0` only; best-bid/ask captured **before** the trade is applied | `SizeMapHeat.OnMarketData` |
| `RadarTab.cs` 627-653 | **`MaybeRunEngine`'s clock-discontinuity handling — the 40 lines.** Three distinct branches, and the order is the whole point: (1) `deltaMs < -2000 || deltaMs > 60000` → full reset; (2) `deltaMs < 0` small-backward → **re-base `_lastEngineRun = now` and return**, do not just early-return (the old bug: leaving the high-water mark froze the engine until replay time climbed back past it); (3) `deltaMs < EngineIntervalMs` → normal 20 Hz throttle, `_lastEngineRun` untouched. | `SizeMapHeat.MaybeRunEngine` |
| `RadarTab.cs` 105-106 | `ReplayResetBackwardMs = 2000`, `ReplayResetForwardMs = 60000`, `EngineIntervalMs = 50` | `SizeMapConfig` consts |
| `RadarTab.cs` 511-520 | `SeedFromSnapshot(Instrument)` — prime `BookMirror` from `inst.MarketDepth.Bids/Asks` so a chart opened mid-session has a book immediately | `SizeMapHeat.SeedFromSnapshot` |
| `RadarTab.cs` 1123-1153 | `HandleReplayReset`: **rebuild** `BookMirror` + `WallTracker` (never "clear"), re-seed from snapshot, `_depthBase.Reset()`, `_lastDepthSample = MinValue`, `_medianEwma = 0`. SizeMap adds: `_ring.Reset(now)`, `_marks.Reset()`, `_recorder.OnEpochBreak(now)` | `SizeMapHeat.HandleReplayReset` |
| `RadarTab.cs` 666-672 | The 1-Hz `DepthBaseline` sampling cadence (**not** per engine run — 20 Hz would resample the same resting book 20× and just autocorrelate) | `SizeMapHeat.MaybeRunEngine` |

---

## 3. ColumnRing

### Bucket policy

- **Column = 250 ms of market time**, `SizeMapConfig.BucketMs = 250`. Matches NT8's 4 fps repaint cap exactly: one column per frame, so no column is ever painted twice and none is ever skipped.
- Bucket boundaries are **absolute**, not relative to the first event: `bucketIndex = eventTicks / (BucketMs * TimeSpan.TicksPerMillisecond)`. Two charts on two instruments produce alignable columns; a restart resumes the same grid.
- The open (head) column **accumulates the max** per (row, side) across the events inside it — not the last, not the sum. Max is the honest aggregator: "the largest size that rested at this price during this 250 ms". Last would hide a wall that flashed and pulled inside the bucket; sum would invent liquidity that never coexisted.
- Row = **absolute price-tick index**, `(int)Math.Round(price / TickSize)`. NQ 25 000 → 100 000; ES 6 000 → 24 000. `int` is never close to overflowing and **there is no rebasing, ever** — that is the whole point of tick space.

### What a cell holds

```csharp
// 8 bytes, no padding. Bid and ask in ONE cell, NOT a sign convention.
[StructLayout(LayoutKind.Sequential, Pack = 2)]
public struct DepthCell
{
    public int    Row;   // absolute price-tick index
    public ushort Bid;   // max resting bid size in this bucket, clamped to 65535
    public ushort Ask;   // max resting ask size in this bucket, clamped to 65535
}
```

**Why not a signed int.** A sign convention (`+bid / -ask`) cannot express *both*, and within one 250 ms bucket the same price genuinely is bid at the start and ask at the end whenever the market crosses it — which is precisely the moment a trader cares about. It also destroys the difference between "observed, size 0" and "not observed", and constraint #3 forbids inventing data. Two `ushort` costs zero extra bytes after alignment.

**Clamp at 65535**: measured ES/NQ MBP-10 resting sizes top out in the low thousands (`es-resting-order-size-measured`: 77% are 1 lot, mean 1.52 *orders*; aggregated level sizes peak ~2-4k). A clamp that never fires is free; if it ever fires, `Overflows` counts it.

**The ring is sparse by column.** Absence of a cell means *not observed* — which is the honest state for 86% of the chart's vertical space at 10 levels of depth. Dense storage would have to invent a value there. Sparse also collapses the memory: dense at 4096 rows × 7200 columns × 4 B = 118 MB, sparse = 2.8 MB.

### Column header

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct ColumnHeader
{
    public long StartTicks;   // DateTime.Ticks of the bucket start, MARKET time. 0 = never written.
    public int  BestBidRow;   // int.MinValue = unknown
    public int  BestAskRow;
    public int  Count;        // cells used, 0..CellsPerColumn
}
```

`BestBidRow/BestAskRow` are stored per column so the rasterizer can draw the inside-market spine without re-deriving it, and so a replayed .smc is self-contained.

### Anchoring — how a column survives scroll and zoom

Nothing in the ring is in pixels or bar indices. A cell is `(absolute tick index, absolute market DateTime.Ticks)`. Scroll, zoom, bar-spacing change, panel resize and bar-type change are **all** projection changes, handled once per frame in `Rasterize` and cached nowhere. There is no invalidation path to get wrong because there is no cached projection.

### Capacity math — 30 minutes

```
columns   = 30 min × 60 s / 0.250 s                    = 7 200
cells/col = 10 bid + 10 ask + headroom for a book that
            walks a few ticks inside 250 ms            =    48
headers   = 7 200 × 24 B                               =   173 KB
cells     = 7 200 × 48 × 8 B                           = 2 765 KB
total                                                  ≈ 2.87 MB per instrument
```

At Apex/Rithmic 40 levels, `CellsPerColumn` goes to 96 → 5.7 MB. Both are nothing; the config carries the knob.

Overflow policy: on a full column, replace the cell with the **smallest** `max(Bid,Ask)` and `Overflows++`. O(48) scan on a path that runs ≤ a few hundred times/second. Never drop the *new* value silently — a wall arriving late in a busy bucket is the value that matters.

### Publish protocol

```csharp
public sealed class ColumnRing
{
    private readonly ColumnHeader[] _cols;      // length Capacity
    private readonly DepthCell[]    _cells;     // length Capacity * CellsPerColumn
    private          int  _head;                // writer-only; the column being accumulated
    private          long _headBucket;          // writer-only
    private          int  _published = -1;      // ONLY cross-thread field

    // ---- depth thread only ----
    public void Accumulate(long eventTicks, int row, Side side, long size);
    public void SetInside(int bestBidRow, int bestAskRow);
    public void Reset(long nowTicks);

    // ---- any thread ----
    public int  PublishedIndex   => Volatile.Read(ref _published);
    public int  Capacity         { get; }
    public int  CellsPerColumn   { get; }
    public long Overflows        => Volatile.Read(ref _overflows);
    public ref readonly ColumnHeader Header(int idx);          // idx must be <= PublishedIndex
    public void CopyCells(int idx, DepthCell[] dst, out int n);// or index _cells directly, read-only
}
```

The writer's `Accumulate` mutates only column `_head`. When `eventTicks` crosses into a new bucket:

```csharp
// bucket roll — the ONLY place _published moves
_cols[_head].StartTicks = _headBucketStartTicks;   // finish the header
_headBucket = newBucket;
Volatile.Write(ref _published, _head);             // RELEASE: everything above is now visible
_head = (_head + 1) % Capacity;
_cols[_head].Count = 0; _cols[_head].StartTicks = 0;   // clear the NEW head, never the old one
```

`Volatile.Write` is a release fence: every cell and header write that happened before it is guaranteed visible to any thread that subsequently does `Volatile.Read(ref _published)`. On x64 (NT8 is x64-only) the read side is a plain load — free.

**Why the reader never takes the depth lock:**

1. It never reads the head. It reads `p = Volatile.Read(ref _published)` and only touches columns `p, p-1, … p-N`. Those are finished and the writer will not touch them again for `Capacity` more buckets.
2. Blocking the depth thread is the failure mode that matters. NT8 delivers depth on the instrument's dispatcher; a handler that waits on the UI thread's ~5 ms raster pass back-pressures the feed and loses events. A heatmap that stutters is ugly; a heatmap with holes in the data is a lie.
3. The cost of not locking is bounded and stated: a reader that lags more than `Capacity` columns (30 minutes) could be overrun by the writer. A frame is 250 ms. `// ponytail: reader-overrun ceiling is 30 min of lag; add a per-column generation counter if a frame ever takes minutes.`

`Reset` (replay rewind / instrument switch) is the one writer op that is not append-only. It sets `_published = -1` **first** (via `Volatile.Write`), then wipes. A reader that observes `-1` draws nothing that frame — which is the truth after a rewind.

---

## 4. Rasterizer — pure

Zero `using SharpDX`, zero `using NinjaTrader`. The whole file references `System` only. This is what makes the harness (§5) and the golden tests (§8) possible.

```csharp
// Everything the projection needs, all of it produced fresh on the UI thread each frame.
public readonly struct RasterView
{
    public readonly int    Width, Height;     // panel pixels
    public readonly int    TopRow, BotRow;    // absolute tick rows at y=0 and y=Height-1
    public readonly long[] SlotTicks;         // ascending market DateTime.Ticks, one per visible bar edge
    public readonly int[]  SlotX;             // pixel x of the same edge (same length, ascending)
    public readonly int    SlotCount;
    public readonly int    Background;        // 0xAARRGGBB premultiplied; 0x00000000 = leave chart visible
    public readonly int    GridGapPx;         // 1 when pxPerTick >= 4, else 0
}

// px.Length must be >= Width*Height. Fully overwritten every call — no stale-frame path exists.
// ramp256: index 0 = 0x00000000 (transparent); 1..255 = the magnitude ramp, PREMULTIPLIED BGRA.
// sizeToIdx: 1024-entry LUT, size -> ramp index. Built once per frame (§ below), so the inner
// loop is two array lookups and one store — no Math.Log per pixel.
public static void Rasterize(
    ColumnRing ring, int publishedThrough, int columnsBack,
    int[] px, in RasterView v, int[] ramp256, byte[] sizeToIdx);
```

### Coordinate math

**tick → row → y.** Rows are the ring's own coordinate; no conversion needed.

```csharp
double pxPerTick = (double)v.Height / (v.TopRow - v.BotRow + 1);
// a row occupies [y0, y1)
int y0 = (int)((v.TopRow - row)     * pxPerTick);
int y1 = (int)((v.TopRow - row + 1) * pxPerTick);
if (y1 <= y0) y1 = y0 + 1;                 // zoomed out: sub-pixel tick still gets one row
y1 -= v.GridGapPx;                         // zoomed in: leave the tick grid visible
```

- `pxPerTick < 1` (zoomed out, several ticks per pixel row): rows collide on the same `y0`. Resolve with **max**, never sum — `if (idx > px[o]) px[o] = idx` after converting through the ramp. Summing sizes across ticks would paint liquidity that does not exist at any single price.
- `pxPerTick > 1` (zoomed in, a tick spans several pixel rows): fill `[y0, y1)` with the identical value. That is not smoothing — the value genuinely is constant across the tick. When `pxPerTick >= 4`, `GridGapPx = 1` leaves the bottom pixel row at background so the 0.25-tick grid is *visible*, which is the honesty tell for constraint #3.

**column → x.** Not linear time. NT8's x-axis is per-bar-slot, so on tick bars, range bars or Renko a linear time→x map is simply wrong. `SlotTicks[]`/`SlotX[]` carry the chart's own bar edges (built on the UI thread from `ChartBars.FromIndex..ToIndex`), and the rasterizer interpolates inside the slot:

```csharp
// binary search: the slot whose [SlotTicks[i], SlotTicks[i+1]) contains the column
int i = LowerBound(v.SlotTicks, v.SlotCount, col.StartTicks);
if (i < 0 || i >= v.SlotCount - 1) continue;                   // outside the visible range: clip
double f  = (double)(col.StartTicks - v.SlotTicks[i]) / (v.SlotTicks[i+1] - v.SlotTicks[i]);
int    x0 = v.SlotX[i] + (int)(f * (v.SlotX[i+1] - v.SlotX[i]));
int    x1 = x0 + Math.Max(1, (int)(BucketTicks * (v.SlotX[i+1] - v.SlotX[i]) / (double)(v.SlotTicks[i+1] - v.SlotTicks[i])));
```

- Several columns per pixel (bar spacing tight, e.g. 100 minutes across 1600 px = 12.6 columns/px): they collide on the same `x0` and resolve with **max**, same rule.
- One column across several pixels (zoomed in): `x1 > x0+1` and the span is filled.

**What happens when the visible price range or bar spacing changes between frames: nothing, by construction.** `RasterView` is a value type built from `chartScale.MinValue/MaxValue` and the current bar slots on every single `OnRender`. Nothing derived from a previous frame's projection is retained. The only cross-frame state is `px` (the reusable buffer, fully overwritten) and the SharpDX `Bitmap` object, which is rebuilt only when `Width`/`Height` change.

### The magnitude scale

Depth sizes are heavy-tailed — median level ~10-30 lots, walls 400+. Linear mapping paints a black chart with three bright dots. SizeMap uses log:

```
idx(size) = 1 + (int)(254.0 * Math.Log(1 + size) / Math.Log(1 + rampMax))     for size >= 1
rampMax   = max(DepthBaseline.P85 * 6, 64)     // MEASURED, adaptive, recomputed once per second
```

`DepthBaseline` (the vendored file) already produces P85 over a rolling ring with a 300-sample warm-up and a `Reset()` for rewinds. Using it here means the ramp ceiling is a measured percentile of *this instrument's this session's* depth, not a hardcoded 400 — the exact ADR-2026-07-03 argument, reused for color instead of for arming.

`sizeToIdx` is a `byte[1024]` filled once per frame (1024 `Math.Log` calls ≈ 15 µs) so the inner loop stays two lookups.

### The ramp (working default — the palette agent owns the final values)

One perceptually-monotone cool ramp, blue → ice. Hue never enters green or red territory, so it cannot compete with candle bodies or with bid/ask semantics. OKLab L measured, strictly increasing:

| ramp idx | hex | OKLab L | alpha | LUT entry (premultiplied `0xAARRGGBB`) |
|---|---|---|---|---|
| 0 | — | — | 0x00 | `0x00000000` (not observed / size 0) |
| 1 | `#0A1626` | 0.198 | 0x50 | `0x5003070C` |
| 52 | `#12395E` | 0.338 | 0x76 | `0x76081A2B` |
| 103 | `#1A6FA0` | 0.517 | 0x9A | `0x9A104361` |
| 154 | `#2FA8C9` | 0.681 | 0xB8 | `0xB8227991` |
| 205 | `#8FDDEE` | 0.853 | 0xCC | `0xCC72B1BE` |
| 255 | `#E8F8FF` | 0.969 | 0xD8 | `0xD8C5D2D8` |

Chart background reference `#0B0E14`, OKLab L = 0.164 — below the coldest stop, so even ramp index 1 is above the ground.

Alpha rises with magnitude (0x50 → 0xD8) so cold liquidity lets candles through and a 400-lot wall dominates. **Premultiplication is mandatory**: the probe uses `AlphaMode.Premultiplied`, so the LUT stores `A, R*A/255, G*A/255, B*A/255`.

**Byte-order note, load-bearing.** `SharpDX.DXGI.Format.B8G8R8A8_UNorm` means memory order B,G,R,A. On little-endian x64 an `int` read from that memory is `A<<24 | R<<16 | G<<8 | B` = literally `0xAARRGGBB`. So `int[] px` entries are written as `0xAARRGGBB` and `Marshal.Copy`'d straight in. No byte swizzling anywhere.

### The categorical layer — NOT in the raster

Remembered liquidity is **not** the ramp at lower alpha. That is the rejection criterion. Remembered walls are discrete objects drawn as Direct2D primitives after the blit, in a hue family the ramp never visits:

| Layer | hex | `0xAARRGGBB` | Form |
|---|---|---|---|
| Live confirmed wall | `#F2F6FA` | `0xFFF2F6FA` | 2 px solid horizontal rule, full width from birth to now |
| Remembered (blind, decaying) | `#E0A33A` | `0xFFE0A33A` | 1 px **dashed** (4 on / 4 off), length = life span, opacity = `Confidence` |
| Absorbed | `#F0B429` | `0xFFF0B429` | filled 5 px triangle at the death x |
| Pulled (cancelled — the fake) | `#7A8899` | `0xFF7A8899` | hollow 5 px diamond, desaturated |
| Consumed (traded through) | `#B06CD6` | `0xFFB06CD6` | 5 px cross |
| Text primary | `#D7DEE8` | `0xFFD7DEE8` | DirectWrite, text token — never the data color |
| Text dim | `#8A94A6` | `0xFF8A94A6` | legend/HUD secondary |

A stranger reading a screenshot sees: continuous ice-blue field = observed depth; dashed amber = remembered, no longer observed. Categorically different mark *form*, not an alpha slider.

**Primitive budget**: `WallMarkRing` returns at most `MaxMarks = 48` marks, sorted by `Confidence` descending. 48 marks × (1 line + 1 glyph) = 96, plus legend 12, plus HUD 8 text runs = ~116 primitives after the single `DrawBitmap`. Inside the stated "low hundreds".

---

## 5. Headless PNG harness

`Harness/Program.cs` reads a recorded `.smr` (or `.smc`), replays it into a `ColumnRing`, calls **the same `SizeMap.Engine.Rasterizer.Rasterize`** the indicator calls, and writes a PNG. Palette and layout iteration then costs `dotnet run` (~1 s) instead of copy → F5 in NT8 → reconnect Replay → wait for a wall (~3 min).

**PNG with no NuGet.** `System.Drawing.Common` is Windows-only on .NET 8 and is a package; `ImageSharp` is a package. Neither is needed: PNG is signature + IHDR + one zlib'd IDAT + IEND, and `System.IO.Compression.DeflateStream` supplies raw DEFLATE. zlib = `0x78 0x01` header + DEFLATE + big-endian Adler-32. Chunk CRC-32 is a 6-line table-less loop. **58 lines, zero dependencies.**

This is compiled and verified, not proposed — `dotnet run` produced a 1 551-byte file that an independent Python decoder confirmed: signature OK, every chunk CRC matches `zlib.crc32`, IDAT inflates to exactly `h*(1+w*3)` bytes, IHDR = `(320, 120, 8, 2, 0, 0, 0)`.

```csharp
// Harness/Png.cs — px entries are 0xAARRGGBB; alpha is composited onto `bg` and dropped (color type 2).
static class Png
{
    public static void Write(string path, int[] px, int w, int h)
    {
        byte[] raw = new byte[h * (1 + w * 3)];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0;                                       // filter: None
            for (int x = 0; x < w; x++)
            {
                int c = px[y * w + x];
                raw[o++] = (byte)(c >> 16); raw[o++] = (byte)(c >> 8); raw[o++] = (byte)c;
            }
        }
        using (var fs = File.Create(path))
        {
            fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
            byte[] ihdr = new byte[13];
            Be(ihdr, 0, w); Be(ihdr, 4, h);
            ihdr[8] = 8; ihdr[9] = 2;                           // 8-bit, truecolour RGB
            Chunk(fs, "IHDR", ihdr);
            Chunk(fs, "IDAT", Zlib(raw));
            Chunk(fs, "IEND", new byte[0]);
        }
    }

    static byte[] Zlib(byte[] d)
    {
        using (var ms = new MemoryStream())
        {
            ms.WriteByte(0x78); ms.WriteByte(0x01);
            using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(d, 0, d.Length);
            uint a = 1, b = 0;
            for (int i = 0; i < d.Length; i++) { a = (a + d[i]) % 65521; b = (b + a) % 65521; }
            uint ad = (b << 16) | a;
            ms.WriteByte((byte)(ad >> 24)); ms.WriteByte((byte)(ad >> 16));
            ms.WriteByte((byte)(ad >> 8));  ms.WriteByte((byte)ad);
            return ms.ToArray();
        }
    }

    static void Chunk(Stream s, string type, byte[] body)
    {
        byte[] len = new byte[4]; Be(len, 0, body.Length); s.Write(len, 0, 4);
        byte[] td = { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
        s.Write(td, 0, 4); s.Write(body, 0, body.Length);
        uint c = 0xFFFFFFFF; c = Crc(c, td); c = Crc(c, body); c ^= 0xFFFFFFFF;
        byte[] cb = new byte[4]; Be(cb, 0, (int)c); s.Write(cb, 0, 4);
    }

    static uint Crc(uint c, byte[] d)
    {
        for (int i = 0; i < d.Length; i++)
        { c ^= d[i]; for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (uint)(-(int)(c & 1))); }
        return c;
    }

    static void Be(byte[] b, int o, int v)
    { b[o] = (byte)(v >> 24); b[o+1] = (byte)(v >> 16); b[o+2] = (byte)(v >> 8); b[o+3] = (byte)v; }
}
```

```
dotnet run --project Harness -- \
    --in  ~/nt8-capture/sm-raw-NQ-20260816.smr \
    --at  "14:32:10" --span 12m --w 1600 --h 900 --out out/frame.png
```

`Program.cs` (~65 lines): open file → verify magic + version → replay records into `BookMirror` + `ColumnRing` until `--at` → build a synthetic `RasterView` (uniform slots for a 1-min chart, or `--slots file.csv` to replay a real chart's bar edges) → `Rasterize` → `Png.Write`. The `--slots` path is what makes a golden test reproduce a real chart's non-linear x-axis exactly.

---

## 6. Threading

### Instrument depth thread — `OnMarketDepth` / `OnMarketData`

Runs on NT8's per-instrument dispatcher. Under `lock (_engineLock)`:

| Cadence | Work | Allocations |
|---|---|---|
| every depth event (~200-500/s on NQ) | `_book.ApplyDepth(de)` (`List<DepthLevel>` insert/update/remove, no `new`) | **0** |
| every depth event | `_ring.Accumulate(ticks, row, side, size)` — array writes into a preallocated struct array | **0** |
| every Last print | `_book.ApplyTrade(te)` — `_trades.Add` (amortized, ring pre-sized) + `_cluster.OnPrint(...)` | **0 amortized** |
| every event | `_recorder.Enqueue(rec)` — one 16-byte struct into a preallocated ring | **0** |
| 4 Hz (bucket roll) | finish header, `Volatile.Write(ref _published, _head)`, harvest `WallMarkRing` from the last snapshot | **0** |
| ≤ 20 Hz (`EngineIntervalMs = 50`) | `_tracker.Update(_book, now)` → `WallDetector` + `EpisodeClassifier` + `LiquidityMemory` | **allocates** |
| 1 Hz | `_depthBase.Add(...)` per level + `EndBatch()` (one array copy + sort of ≤4096 longs) | 1 array |

**Rules that must never be broken on the per-event path:**
- **No allocation** — no `new`, no `List<T>` growth (pre-size), no boxing, no lambda capture, no string concatenation, no `string.Format`.
- **No `Print` / `Output.Process`** — NT8 marshals both to the UI dispatcher. One `Print` per depth event is a guaranteed feed backlog.
- **No LINQ.** Every `Where`/`Select` allocates an enumerator and a closure per call.
- **No file IO.** Not `File.AppendText`, not `Flush`, not `Directory.CreateDirectory`. RadarTab has this exact note at line 903.
- **No `DateTime.Now` / `Environment.TickCount`.** Every timestamp is `e.Time` — market time. In accelerated Playback the two diverge by minutes, and every window in the engine is expressed in market time. (BigPrints documents the one legitimate exception: the audio cooldown, because audio overlap is a physical real-time phenomenon. SizeMap has no audio.)
- **No chart access.** `ChartControl`, `ChartScale`, `ChartBars`, `Draw.*` are UI-thread objects. Touching them here is a race, not a slowdown.
- **No waiting on the UI thread.** No `Dispatcher.Invoke` (use `InvokeAsync` if ever needed).

The 20 Hz engine tier **does** allocate (`BookMirror.MedianOf` news a `List<long>`, `WallDetector.UpdateSide` news a `HashSet<long>`). That is inherited, measured in production by Trading-radar through full Market Replay sessions, and gated behind the same 50 ms throttle. Stating it honestly beats pretending the whole thread is allocation-free: **the per-event tier is zero-alloc, the 20 Hz tier is bounded-alloc, and the two are separated by `MaybeRunEngine`.**

### UI thread — `OnRender`

Called by NT8 at most once per 250 ms. Never takes `_engineLock`.

1. `int p = _ring.PublishedIndex;` (one `Volatile.Read`)
2. snapshot the mark layer: `_marks.CopyVisible(_markBuf, out int nMarks)` — `_markBuf` is preallocated, `WallMarkRing` uses the same published-index protocol
3. build `RasterView` from `chartScale.MinValue/MaxValue` and the visible bar edges into two **preallocated** arrays `_slotTicks[]`/`_slotX[]`
4. `Rasterizer.Rasterize(_ring, p, cols, _px, in view, _ramp, _sizeToIdx)` — writes into the reusable `int[]`
5. `Marshal.Copy(_px, 0, _pxNative, w*h)` then `_bmp.CopyFromMemory(_pxNative, w*4)` — the bitmap object is **not** recreated per frame (the probe's per-call `new Bitmap` was a probe, not the shipping pattern)
6. `RenderTarget.AntialiasMode = Aliased; RenderTarget.DrawBitmap(_bmp, dest, 1f, NearestNeighbor);`
7. marks, legend, HUD as primitives
8. **`RenderTarget.AntialiasMode = PerPrimitive;`** — the render target is shared with the chart. Leaving it `Aliased` aliases everyone else's candles. This is a real gotcha, not a nicety.

Allocations on the render path: **zero.** `_px`, `_pxNative`, `_ramp`, `_sizeToIdx`, `_markBuf`, `_slotTicks`, `_slotX` and every `SolidColorBrush` / `TextFormat` are created in `OnRenderTargetChanged` or on the first frame at a given size, and disposed there.

### Recorder drain thread

One background `Task` started at `State.DataLoaded`, ended at `State.Terminated`. Loop: `Task.Delay(250)` → read `Volatile.Read(ref _writeIdx)` → copy the new span out of the ring → one `FileStream.Write` (64 KB buffer, `FileOptions.SequentialScan`) → repeat. It never touches `_engineLock` and the depth thread never waits on it. Overflow (drain starved) increments `Dropped` and is shown in the HUD — **never** blocks the producer. This is BigPrintsRecorder's `FinalizeLocked` discipline (detach the buffer, hand it to `Task.Run`, never touch a file on the market thread) restructured for a continuous stream instead of event windows.

---

## 7. The recorder

Two files, one drain task, one purpose each. `(a)` can regenerate `(b)`; `(b)` exists so warm-start does not have to replay 30 minutes of raw events on `DataLoaded`.

### (a) Raw event writer — the calibration corpus

`~/Documents/NinjaTrader 8/SizeMap/sm-raw-<INSTRUMENT>-<yyyyMMdd>[-<part>].smr`. One file per instrument per day; `part` increments on a Playback epoch break.

**Header — 32 bytes, offsets:**

| off | size | type | field |
|---|---|---|---|
| 0 | 4 | ascii | magic `"SMR1"` |
| 4 | 2 | ushort | `version = 1` |
| 6 | 2 | ushort | `headerBytes = 32` |
| 8 | 8 | long | `t0Ticks` — `DateTime.Ticks` of the file's time origin (market time) |
| 16 | 8 | double | `tickSize` |
| 24 | 4 | int | `recordBytes = 16` |
| 28 | 4 | int | `flags` — bit0 `1` = Market Replay, bit1 `1` = this file is a continuation part |

**Record — 16 bytes, `[StructLayout(LayoutKind.Sequential, Pack = 1)]`:**

| off | size | type | field |
|---|---|---|---|
| 0 | 4 | int | `dtMs` — ms since `t0Ticks` (86 400 000 ms in a day; `int` is safe to 24.8 days) |
| 4 | 4 | int | `row` — absolute price-tick index |
| 8 | 4 | int | `size` — resting size (depth) or print size (trade) |
| 12 | 1 | byte | `kind` — `0` DepthBid, `1` DepthAsk, `2` TradeBuy, `3` TradeSell, `4` FeedReset, `5` EpochBreak, `6` Heartbeat |
| 13 | 1 | byte | `pos` — ladder position 0..254; `255` = n/a |
| 14 | 1 | byte | `op` — `0` Add, `1` Update, `2` Remove; `255` = n/a |
| 15 | 1 | byte | `seq` — low 8 bits of a monotonic counter, gap detection |

**Volume**: NQ RTH ≈ 400 events/s × 16 B = 6.4 KB/s = **23 MB/h ≈ 160 MB per RTH day**. `MaxBytesPerDay = 512 MB` default; on the cap the recorder stops, logs once and shows `REC CAPPED` in the HUD (BigPrints' `_maxFiles` idea, expressed in bytes because the stream is continuous).

Fixed-width and packed on purpose: `mmap`/`np.fromfile(dtype=...)` reads it directly in Python for calibration (K_mult, MinAbsSize, T_persist, F_flicker, ramp `rampMax`) with no parser.

### (b) Column snapshot — the warm-start

`sm-cols-<INSTRUMENT>-<yyyyMMdd>[-<part>].smc`, appended by the same drain task once per published column.

**Header — 32 bytes:**

| off | size | type | field |
|---|---|---|---|
| 0 | 4 | ascii | magic `"SMC1"` |
| 4 | 2 | ushort | `version = 1` |
| 6 | 2 | ushort | `headerBytes = 32` |
| 8 | 8 | double | `tickSize` |
| 16 | 4 | int | `bucketMs = 250` |
| 20 | 4 | int | `cellsPerColumn` (48, or 96 on a 40-level feed) |
| 24 | 4 | int | `columnBytes = 20 + cellsPerColumn*8` — **fixed stride, so warm-start seeks instead of scanning** |
| 28 | 4 | int | `flags` |

**Column block — fixed `columnBytes`:**

| off | size | type | field |
|---|---|---|---|
| 0 | 8 | long | `startTicks` |
| 8 | 4 | int | `bestBidRow` (`int.MinValue` = unknown) |
| 12 | 4 | int | `bestAskRow` |
| 16 | 2 | ushort | `count` |
| 18 | 2 | ushort | `reserved = 0` — keeps the cell array 4-byte aligned at offset 20 |
| 20 + 8k | 4 | int | `cell[k].Row` |
| 24 + 8k | 2 | ushort | `cell[k].Bid` |
| 26 + 8k | 2 | ushort | `cell[k].Ask` |

Fixed stride costs ~220 unused bytes per column on a quiet book and buys `Seek(fileLen - 7200*columnBytes)` — the warm-start reads exactly the last 30 minutes with one seek and one bulk read (2.5 MB, ~20 ms), no parsing loop.

**Volume**: 404 B/column × 4/s = 1.6 KB/s = **5.8 MB/h**. Free.

`SizeMapWarmStart.Load(path, ring)` runs at `State.DataLoaded`, **before** any market event, and sets `_published` to the last loaded index. Constraint #6 is then satisfied for every chart opened after the first: the left side of the chart is populated instantly.

### What is reused from `BigPrintsRecorder`, by name

1. **Detach-then-`Task.Run`** (`FinalizeLocked`, 336-382): buffers are swapped out under the lock, the file write happens on a background task. Never a `FileStream` on a market thread.
2. **`BackwardsTolSecs = 2` and `JumpForwardSecs = 30`**, and `TimeCheckLocked`'s **branch order** (272-282): flush/close the current window **before** the jump test, because a quiet tape gap can exceed 30 s exactly at a boundary and the jump branch would throw away a fully-formed capture. SizeMap: backward > 2 s or forward > 30 s → close the file, `EpochBreak` record, open `-partN`, `ring.Reset()`. **Never splice two tape epochs into one file.**
3. **The `default(DateTime)` guard before any `AddSeconds(-x)`** — `DateTime.MinValue.AddSeconds(-2)` throws; verified against the runtime, `BigPrints.cs` 535-537. It runs before every other guard.
4. **A failed write must not consume the cap** (line 379, `_filesWritten--`). SizeMap: a failed flush does not advance the flushed-through watermark, so the next drain retries the same bytes.
5. **`t0` in the header, every record's time as an integer offset** (`Ms(t0,t)`, 384-385).
6. **The callback rule**, verbatim in the header comment: callbacks fire under the lock *only* because the wired handlers are provably non-blocking and never re-enter. Do not wire a handler that calls back.
7. **A hard runaway cap** (`_maxFiles`) → SizeMap's `MaxBytesPerDay`.

Deliberately **not** reused: the JSON/Newtonsoft writer (100× the bytes, allocates per record — fatal for a continuous stream), the Off/Armed/Recording trigger state machine (SizeMap records continuously; there is no trigger), and `double[][]` book snapshots (allocates per snapshot).

---

## 8. Test plan

New xunit tests, `Tests/`, referencing `SizeMap.Engine` only. Naming follows Trading-radar's existing suite.

**`ColumnRingTests.cs`**
1. `Accumulate_TakesMaxWithinBucket_NotLast` — 400 then 120 at the same row in one bucket → cell reads 400.
2. `Accumulate_KeepsBidAndAskInTheSameCell` — same row bid 200 and ask 300 in one bucket → both survive; the ask does not overwrite the bid.
3. `BucketRoll_PublishesOnlyCompletedColumns` — after N events inside one bucket, `PublishedIndex` still points at the previous column; the head is never readable.
4. `Publish_IsMonotonicAcrossWrap` — 3 × Capacity buckets, `PublishedIndex` advances by exactly 1 each roll and wraps correctly.
5. `Row_IsAbsoluteTickSpace_SurvivesA500PointWalk` — price walks 22 000 → 22 500 on NQ, no rebasing, no row collision, no overflow.
6. `Overflow_ReplacesSmallestCell_AndCounts` — 60 distinct rows into a 48-cell column → the 48 largest survive, `Overflows == 12`.
7. `Capacity30Minutes_IsExactly7200Columns` — the arithmetic itself, so a `BucketMs` change is caught.
8. `Reset_ClearsPublishedFirst_ThenWipes` — after `Reset`, `PublishedIndex == -1` and no stale header has a non-zero `StartTicks`.
9. `BackwardTimestamp_WithinBucket_DoesNotRoll` — out-of-order feed tick 40 ms behind → same column, no roll, no lost cell.
10. `ConcurrentWriter_ReaderSeesNoTornColumn` — 1 writer task, 200 k events, 1 reader task spinning on `PublishedIndex`; every observed column asserts `Count <= CellsPerColumn`, `StartTicks != 0`, rows unique, and `Count` matching the number of non-default cells.
11. `Accumulate_AllocatesNothing` — `GC.GetAllocatedBytesForCurrentThread()` delta over 100 k `Accumulate` calls **== 0**. This is the test that enforces §6's first rule.

**`RasterizerTests.cs`**
1. `EmptyRing_FillsBufferWithBackground_Exactly` — every entry equals `view.Background`, no leftovers.
2. `TopRow_MapsToScanlineZero_BotRow_ToHeightMinusOne` — the two endpoints of the row→y map.
3. `ZoomOut_MultipleTicksPerPixel_TakesMax_NeverSum` — rows with 100 and 300 collide on one y; result is `idx(300)`, and explicitly **not** `idx(400)`.
4. `ZoomIn_TickSpansFourRows_FillsThree_LeavesGridGap` — `pxPerTick == 4` → 3 painted rows + 1 background row.
5. `ZoomIn_TickSpansThreeRows_NoGridGap` — `GridGapPx == 0` below the threshold; the gap must not eat data at moderate zoom.
6. `ColumnsNarrowerThanPixel_TakeMax` — 12 columns into one x, brightest wins.
7. `ColumnWiderThanPixel_FillsItsFullSpan` — no 1 px stripes with holes when zoomed in.
8. `NonLinearBarSlots_PlaceColumnsInsideTheirOwnSlot` — a tick-bar chart where slot 3 spans 8 s and slot 4 spans 90 s; a column at slot 4 + 45 s lands at the slot's midpoint, not at linear-time x.
9. `RowsOutsideVisibleRange_AreClipped_NoException` — 10 000 random rows, half far outside `[BotRow, TopRow]`; no `IndexOutOfRangeException`, no wrapped writes.
10. `ColumnsOutsideVisibleTime_AreClipped` — same for `SlotTicks` bounds, including the empty-`SlotCount` case.
11. `SizeToRampIndex_IsMonotoneNonDecreasing` — for all sizes 0..1023.
12. `RampIndexZero_IsFullyTransparent` — `ramp[0] == 0x00000000`, so "not observed" can never paint.
13. `ViewChangeBetweenFrames_LeavesNoStalePixels` — rasterize view A, then view B into the same buffer; every pixel matches a fresh-buffer rasterize of B.
14. `Rasterize_IsDeterministic` — same ring + same view twice → byte-identical `int[]`.
15. `Rasterize_AllocatesNothing` — allocated-bytes delta over 100 frames **== 0**.
16. `Golden_TwoWallsAndAMemoryBand_MatchesCheckedInPng` — builds a synthetic ring (one 400-lot ask wall, one 250-lot bid wall that pulls, an eroding band), rasterizes, encodes with the harness' `Png`, byte-compares to `golden/two-walls.png`. **This is the test that makes the harness pay for itself** — a palette or coordinate regression shows up as a failing byte compare, not as "the chart looks a bit off".

**`RecorderRoundTripTests.cs`**
1. `RawRecord_IsExactly16Bytes` — `Marshal.SizeOf<RawRecord>() == 16`, catching a `Pack` regression.
2. `RawRoundTrip_PreservesEveryField` — 50 k random records written and read back identical.
3. `ColumnBlock_StrideMatchesHeader` — file length is `32 + n*columnBytes` for every `cellsPerColumn`.
4. `WarmStart_LoadsLast7200Columns_AndSetsPublished` — file with 20 000 columns → ring holds the newest 7 200, `PublishedIndex == 7199`.
5. `EpochBreak_ClosesFileAndOpensPart2` — a 5-minute backward jump; part 1 ends at the break, part 2 starts clean, no record crosses.
6. `MinValueTimestamp_DoesNotThrow` — the `default(DateTime)` guard, directly.

**`PrintClusterTests.cs`**
1. `SameSideWithin150ms_Folds`
2. `Gap151ms_StartsANewCluster`
3. `Span1501ms_StartsANewCluster_EvenWithNoGap` — the `MaxClusterSpanMs` cap, which is the one BigPrints added later.
4. `InsideSpreadPrint_IsNotAnAggressor_AndDoesNotBreakTheCluster`
5. `BelowMinVolume_EmitsNothing`
6. `BackwardJumpBeyondTwoSeconds_ClearsClusterMemory`

---

## 9. `build/deploy.sh`

```bash
#!/usr/bin/env bash
# SizeMap -> NinjaTrader 8 Custom/. Stages, rewrites the namespace, proves coexistence
# with the LIVE LiquidityRadar + BigPrints, deploys, then verifies byte-for-byte.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CUSTOM="${NT8_CUSTOM:-/mnt/c/Users/javlo/Documents/NinjaTrader 8/bin/Custom}"
ADDON="$CUSTOM/AddOns/SizeMap"
IND="$CUSTOM/Indicators"
STAGE="$ROOT/build/.stage/Custom"

[ -d "$CUSTOM" ] || { echo "FATAL: Custom not found: $CUSTOM"; exit 1; }
case "$ADDON" in */AddOns/SizeMap) ;; *) echo "FATAL: refusing rm -rf '$ADDON'"; exit 1;; esac

# --- 1. stage. THE namespace rewrite: TradingRadar.Engine is already compiled into the live
#        Custom.dll via AddOns/LiquidityRadar/ — copying it again is CS0101 and takes the
#        WHOLE assembly down (LiquidityRadar, BigPrints, every strategy).
rm -rf "$ROOT/build/.stage"
mkdir -p "$STAGE/AddOns/SizeMap" "$STAGE/Indicators"
for f in "$ROOT"/Engine/*.cs; do
  sed 's/^namespace TradingRadar\.Engine$/namespace SizeMap.Engine/' "$f" \
      > "$STAGE/AddOns/SizeMap/$(basename "$f")"
done
cp "$ROOT"/NinjaTrader/*.cs "$STAGE/Indicators/"

# --- 2. prove the rewrite happened and the folders declare what they must
if grep -rn 'namespace TradingRadar\.Engine' "$STAGE/AddOns/SizeMap/"; then
  echo "FATAL: TradingRadar.Engine survived the sed -> CS0101 vs LiquidityRadar"; exit 1
fi
for f in "$STAGE/AddOns/SizeMap"/*.cs; do
  grep -q '^namespace SizeMap\.Engine$' "$f" || { echo "FATAL: $f lacks 'namespace SizeMap.Engine'"; exit 1; }
done
for f in "$STAGE/Indicators"/*.cs; do
  grep -q '^namespace NinjaTrader\.NinjaScript\.Indicators$' "$f" || { echo "FATAL: $f is not an Indicators-namespace file"; exit 1; }
done

# --- 3. COEXISTENCE build: SizeMap next to the LIVE LiquidityRadar + BigPrints. A SizeMap-only
#        stage compiles clean even with a duplicate type, so it proves nothing.
cp -r "$CUSTOM/AddOns/LiquidityRadar" "$STAGE/AddOns/"
cp "$CUSTOM"/Indicators/BigPrints*.cs "$STAGE/Indicators/"
nt8c build --custom-dir "$STAGE" || { echo "FATAL: coexistence build failed"; exit 1; }
rm -rf "$STAGE/AddOns/LiquidityRadar"; rm -f "$STAGE"/Indicators/BigPrints*.cs

# --- 4. deploy. AddOns/SizeMap is exclusively ours -> wipe it, so a renamed/deleted file
#        can never linger and duplicate a type.
rm -rf "$ADDON"; mkdir -p "$ADDON"
cp "$STAGE/AddOns/SizeMap/"*.cs "$ADDON/"
cp "$STAGE/Indicators/"*.cs     "$IND/"

# --- 5a. no duplicate basenames between Indicators/ and Strategies/ (the 2026-08-02 CS0101 cascade)
dups=$(find "$IND" "$CUSTOM/Strategies" -maxdepth 1 -name 'SizeMap*.cs' -printf '%f\n' | sort | uniq -d)
[ -z "$dups" ] || { echo "FATAL: duplicate basenames: $dups"; exit 1; }

# --- 5b. no engine file leaked into Indicators/ or Strategies/
if grep -rln 'namespace SizeMap\.Engine' "$IND" "$CUSTOM/Strategies" --include='*.cs' 2>/dev/null | grep .; then
  echo "FATAL: a SizeMap.Engine file landed outside AddOns/SizeMap"; exit 1
fi

# --- 5c. cmp every deployed file against what we staged
fail=0
for f in "$STAGE/AddOns/SizeMap"/*.cs; do cmp -s "$f" "$ADDON/$(basename "$f")" || { echo "MISMATCH: $(basename "$f")"; fail=1; }; done
for f in "$STAGE/Indicators"/*.cs;     do cmp -s "$f" "$IND/$(basename "$f")"   || { echo "MISMATCH: $(basename "$f")"; fail=1; }; done
[ "$(ls -1 "$ADDON"/*.cs | wc -l)" -eq "$(ls -1 "$STAGE/AddOns/SizeMap"/*.cs | wc -l)" ] \
  || { echo "FATAL: AddOns/SizeMap has stray files"; fail=1; }
[ $fail -eq 0 ] || exit 1

# --- 5d. LiquidityRadar must be untouched
grep -q '^namespace TradingRadar\.Engine$' "$CUSTOM/AddOns/LiquidityRadar/Primitives.cs" \
  || { echo "FATAL: LiquidityRadar was modified"; exit 1; }

echo "OK: $(ls -1 "$ADDON"/*.cs | wc -l) engine + $(ls -1 "$STAGE/Indicators"/*.cs | wc -l) NT files deployed."
echo "Now: NinjaScript Editor -> F5."
```

---

## 10. Indicator skeleton — `NinjaTrader/SizeMapHeat.cs`

```csharp
#region Using declarations
using System;
using System.Runtime.InteropServices;
using System.Threading;
using SharpDX;
using SharpDX.Direct2D1;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using SizeMap.Engine;                       // the rewritten namespace, NOT TradingRadar.Engine
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SizeMapHeat : Indicator
    {
        // ---- engine state: instrument thread, all of it under _engineLock ----
        private readonly object _engineLock = new object();
        private RadarConfig    _cfg;
        private BookMirror     _book;
        private WallTracker    _tracker;
        private DepthBaseline  _depthBase;
        private ColumnRing     _ring;         // cross-thread, but ONLY via Volatile _published
        private WallMarkRing   _marks;        // same protocol
        private SizeMapRecorder _rec;
        private DateTime _lastEngineRun   = DateTime.MinValue;
        private DateTime _lastDepthSample = DateTime.MinValue;
        // ported verbatim from RadarTab.cs 101-106 — these three numbers ARE the replay handling
        private const double EngineIntervalMs       = 50;      // ~20 Hz forward throttle
        private const double ReplayResetBackwardMs  = 2000;    // real rewinds jump seconds-minutes
        private const double ReplayResetForwardMs   = 60000;   // bigger than any real quiet gap

        // ---- render state: UI thread only, allocated once ----
        private SharpDX.Direct2D1.Bitmap _bmp;
        private IntPtr _pxNative = IntPtr.Zero;
        private int[]  _px;  private int _bmpW, _bmpH;
        private int[]  _ramp;      private byte[] _sizeToIdx;
        private long[] _slotTicks; private int[]  _slotX;
        private WallMark[] _markBuf;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "SizeMapHeat";
                Description = "Depth heatmap with wall memory: shows whether size was TRADED or CANCELLED.";
                Calculate                = Calculate.OnEachTick;
                IsOverlay                = true;
                DrawOnPricePanel         = true;
                DisplayInDataBox         = false;
                PaintPriceMarkers        = false;
                // must stay collecting when the tab is hidden, or the ring gets holes
                IsSuspendedWhileInactive = false;
                BucketMs = 250; MinutesOfHistory = 30; RecordRaw = true;
            }
            else if (State == State.Configure) { /* no extra series */ }
            else if (State == State.DataLoaded)
            {
                // ORDER MATTERS: build everything BEFORE the first market event can arrive.
                _cfg       = new RadarConfig { TickSize = TickSize };
                _book      = new BookMirror(TickSize, TimeSpan.FromSeconds(30));
                _tracker   = new WallTracker(_cfg);
                _depthBase = new DepthBaseline(4096);
                _ring      = new ColumnRing(MinutesOfHistory * 60 * 1000 / BucketMs, 48, TickSize, BucketMs);
                _marks     = new WallMarkRing(512);
                _rec       = new SizeMapRecorder(SizeMapPaths.Dir, Instrument.MasterInstrument.Name, RecordRaw);
                SizeMapWarmStart.Load(_rec.ColumnPath, _ring);   // constraint #6: chart opens populated
                SeedFromSnapshot(Instrument);                    // RadarTab.cs 511-520
                _rec.Start();                                    // background drain task
            }
            else if (State == State.Terminated)
            {
                _rec?.Stop();          // flushes on its own thread, never here
                ReleaseRenderResources();
            }
        }

        protected override void OnBarUpdate() { }   // nothing bar-driven; all state is event-driven

        // ================= INSTRUMENT DEPTH THREAD =================
        // Zero allocation. No Print. No LINQ. No file IO. No chart access. No DateTime.Now.
        protected override void OnMarketDepth(MarketDepthEventArgs e)
        {
            // 1. reset first — a reset carries no price and must never reach the mapping below
            if (e.IsReset)
            {
                lock (_engineLock)
                {
                    if (e.Instrument != Instrument) return;
                    _book.ApplyDepth(new DepthEvent { IsReset = true });
                    _tracker.OnReset(e.Time);
                    _rec.OnFeedReset(e.Time);
                }
                return;
            }
            // 2. junk price
            if (e.Price <= 0) return;
            // 3. map (struct, stack-allocated, no `new` on the heap)
            DepthOp op = e.Operation == Operation.Add    ? DepthOp.Add
                       : e.Operation == Operation.Update ? DepthOp.Update
                                                         : DepthOp.Remove;
            DepthEvent de = new DepthEvent {
                Side = e.MarketDataType == MarketDataType.Ask ? Side.Ask : Side.Bid,
                Op = op, Position = e.Position, Price = e.Price,
                Volume = e.Volume, Time = e.Time, IsReset = false };

            lock (_engineLock)
            {
                // 4. stale event from a PRIOR instrument (mid-switch) — drop BEFORE touching the book
                if (e.Instrument != Instrument) return;
                _book.ApplyDepth(de);
                // 5. ring accumulate: every event, max per (row,side), zero alloc
                _ring.Accumulate(e.Time.Ticks, RowOf(e.Price), de.Side, e.Volume);
                _rec.Enqueue(e.Time, RowOf(e.Price), (int)e.Volume, de.Side, op, e.Position);
                // 6. bucket roll + throttled engine (both live in MaybeRunEngine)
                MaybeRunEngine(e.Time);
            }
        }

        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (e.MarketDataType != MarketDataType.Last || e.Price <= 0) return;
            lock (_engineLock)
            {
                if (e.Instrument != Instrument) return;
                // capture the inside BEFORE applying the trade — the cluster rule classifies
                // the aggressor against the quote PREVAILING AT TRADE TIME
                DepthLevel bb, ba;
                double bid = _book.TryBestBid(out bb) ? bb.Price : 0;
                double ask = _book.TryBestAsk(out ba) ? ba.Price : 0;
                _book.ApplyTrade(new TradeEvent { Price = e.Price, Volume = e.Volume, Time = e.Time });
                _cluster.OnPrint(e.Time, e.Price, e.Volume, bid, ask);   // BigPrints rule, ported
                _rec.EnqueueTrade(e.Time, RowOf(e.Price), (int)e.Volume, e.Price >= ask);
                MaybeRunEngine(e.Time);
            }
        }

        // The 40 lines from RadarTab.cs 627-653. Three branches, and the ORDER is the whole point.
        private void MaybeRunEngine(DateTime now)
        {
            if (_lastEngineRun != DateTime.MinValue)
            {
                double dMs = (now - _lastEngineRun).TotalMilliseconds;
                // (1) far BACKWARD (rewind/restart) or far FORWARD (scrub-ahead / session rollover,
                //     which would otherwise paint a stale book as live). Rebuild, then fall through.
                if (dMs < -ReplayResetBackwardMs || dMs > ReplayResetForwardMs)
                    HandleReplayReset(now);
                // (2) small BACKWARD step: do NOT run, but DO re-base the clock down to `now`.
                //     Leaving _lastEngineRun at the forward high-water mark froze the engine
                //     until replay time climbed back past it — that was the bug.
                else if (dMs < 0) { _lastEngineRun = now; return; }
                // (3) normal forward throttle; _lastEngineRun stays at the last ACTUAL run
                else if (dMs < EngineIntervalMs) { /* still roll the bucket below */ }
                else goto RUN;
                _ring.MaybeRoll(now.Ticks);          // the 4 Hz publish never depends on the 20 Hz engine
                return;
            }
        RUN:
            _lastEngineRun = now;
            _tracker.Update(_book, now);
            // 1 Hz depth sampling — NOT per engine run: 20 Hz would resample the same resting
            // book 20x and just autocorrelate (ADR 2026-07-03).
            if (_lastDepthSample == DateTime.MinValue || (now - _lastDepthSample).TotalSeconds >= 1.0)
            {
                _lastDepthSample = now;
                var b = _book.Levels(Side.Bid); var a = _book.Levels(Side.Ask);
                for (int i = 0; i < b.Count; i++) _depthBase.Add(b[i].Volume);
                for (int i = 0; i < a.Count; i++) _depthBase.Add(a[i].Volume);
                _depthBase.EndBatch();
            }
            if (_ring.MaybeRoll(now.Ticks))          // bucket closed -> publish
                _marks.Harvest(_tracker.GetSnapshot(now), _tracker.ErosionReads(_book, now), _book, now);
        }

        private void HandleReplayReset(DateTime now)   // RadarTab.cs 1123-1153
        {
            _book    = new BookMirror(_cfg.TickSize, TimeSpan.FromSeconds(30));  // REBUILD, not Clear
            SeedFromSnapshot(Instrument);
            _tracker = new WallTracker(_cfg);          // drop stale wall/episode/confidence memory
            _depthBase.Reset();                        // rewound history must not inherit the old distribution
            _lastDepthSample = DateTime.MinValue;
            _ring.Reset(now.Ticks);                    // publishes -1 first, then wipes
            _marks.Reset();
            _cluster.Reset();
            _rec.OnEpochBreak(now);                    // close the file, open -partN. Never splice epochs.
        }

        // ================= UI THREAD =================
        public override void OnRenderTargetChanged()
        {
            ReleaseRenderResources();                  // device-dependent resources die with the target
            if (RenderTarget == null) return;
            _ramp      = Palette.BuildRamp256();       // premultiplied 0xAARRGGBB
            _sizeToIdx = new byte[1024];
            _markBuf   = new WallMark[SizeMapConfig.MaxMarks];
            _slotTicks = new long[512]; _slotX = new int[512];
            // the Bitmap itself is created lazily in OnRender, keyed on panel size
        }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            // guards, in order: chart -> device -> data
            if (chartControl == null || chartScale == null || ChartBars == null) return;
            if (RenderTarget == null || _ramp == null) return;
            int w = ChartPanel.W, h = ChartPanel.H;
            if (w <= 0 || h <= 0) return;
            if (!EnsureBitmap(w, h)) return;                       // rebuild only on size change
            int published = _ring.PublishedIndex;                  // ONE Volatile.Read
            if (published < 0) return;                             // post-reset: draw nothing. Honest.

            Palette.FillSizeLut(_sizeToIdx, Math.Max(_depthBase.P85 * 6, 64));   // MEASURED ramp ceiling
            int slots = BuildSlots(chartControl, _slotTicks, _slotX);            // per-BAR edges, not linear time
            var view = new RasterView(w, h,
                                      RowOf(chartScale.MaxValue), RowOf(chartScale.MinValue),
                                      _slotTicks, _slotX, slots,
                                      0x00000000,                                // transparent ground: candles show through
                                      (h / (double)(RowOf(chartScale.MaxValue) - RowOf(chartScale.MinValue) + 1)) >= 4 ? 1 : 0);

            Rasterizer.Rasterize(_ring, published, _colsVisible, _px, in view, _ramp, _sizeToIdx);
            Marshal.Copy(_px, 0, _pxNative, w * h);
            _bmp.CopyFromMemory(_pxNative, w * 4);                 // reuse the bitmap; never `new` per frame

            var prevAA = RenderTarget.AntialiasMode;
            RenderTarget.AntialiasMode = AntialiasMode.Aliased;    // a smoothed heatmap is mush
            RenderTarget.DrawBitmap(_bmp, new RectangleF(ChartPanel.X, ChartPanel.Y, w, h),
                                    1f, BitmapInterpolationMode.NearestNeighbor);
            RenderTarget.AntialiasMode = prevAA;                   // MUST restore: the target is SHARED
                                                                   // with the chart. Aliased candles otherwise.

            int n = _marks.CopyVisible(_markBuf, view);            // <= 48
            DrawMarks(_markBuf, n, chartControl, chartScale);      // ~96 primitives
            DrawLegend(chartControl);                              // ~12, mandatory, text tokens only
            DrawHud(chartControl);                                 // ~8: levels seen, ring overflows, REC state
        }

        private bool EnsureBitmap(int w, int h)
        {
            if (_bmp != null && _bmpW == w && _bmpH == h) return true;
            ReleaseBitmapOnly();
            _px       = new int[w * h];
            _pxNative = Marshal.AllocHGlobal(w * h * 4);
            var pf    = new PixelFormat(SharpDX.DXGI.Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied);
            _bmp      = new SharpDX.Direct2D1.Bitmap(RenderTarget, new Size2(w, h), new BitmapProperties(pf));
            _bmpW = w; _bmpH = h;
            return true;
        }

        private int RowOf(double price) { return (int)Math.Round(price / TickSize); }
        private void SeedFromSnapshot(Instrument inst) { /* RadarTab.cs 511-520 verbatim */ }
        private void ReleaseRenderResources() { ReleaseBitmapOnly(); _ramp = null; }
        private void ReleaseBitmapOnly()
        {
            if (_bmp != null) { _bmp.Dispose(); _bmp = null; }
            if (_pxNative != IntPtr.Zero) { Marshal.FreeHGlobal(_pxNative); _pxNative = IntPtr.Zero; }
            _bmpW = _bmpH = 0;
        }
    }
}
```

---

### Two judgement calls worth flagging

**The raster paints observed depth only; remembered liquidity is the mark layer.** At 10 levels the live book covers 2.50 points per side — about 14% of the chart's height. Filling the other 86% with a low-alpha version of the same ramp is exactly the dishonesty the rules reject. What actually lives up there is a handful of discrete wall objects, and discrete objects want glyphs, not a field. So the heat is a narrow bright river that follows price, and the memory band is dashed amber rules extending left from each wall's birth. That also keeps the post-blit primitive count at ~116.

**The x-axis is built from the chart's bar slots, not from linear time.** On a 1-minute chart the two coincide, which is why it is tempting to skip; on tick bars, range bars or Renko a linear time→x map puts every column in the wrong place, and the bug would only show on the bar types Javier actually scalps. Two preallocated arrays passed into a pure function costs ~15 lines and removes the whole failure class.