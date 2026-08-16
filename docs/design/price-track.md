# SizeMap — Price Track: decision and final spec

---

## 1. THE HONEST ANSWER

**Yes. And nothing breaks.**

SizeMap can draw its own price representation, and NT8's bars can be made to draw nothing. Two mechanisms, both verified:

**Shipped (recommended):** a 25-line custom `ChartStyle` whose `OnRender` is empty. It compiles clean (`nt8c check` → `OK, 0 warnings`; file at `…/scratchpad/NullStyle.cs`). Ships as a second `.cs` beside the indicator, deployed to `Documents\NinjaTrader 8\bin\Custom\ChartStyles\` — the namespace decides the folder, same rule as indicators. Registration key is `ChartStyleType = 1770` (NT advises >1023; collision is non-fatal, NT logs *"Multiple ChartStyles with the same ChartStyleType were found. Ignoring {0}."*).

> **Click path, once, per chart:** right-click → **Data Series…** → select the instrument series → **Chart style = "SizeMap Null (no bars)"** → OK. Persists in the workspace/template.

**Zero-install fallback (verified by reading `@CandleStyle.cs` line by line):** with the stock Candlestick style and `Wick matches body = true` (the default), body fill, border and both wicks all resolve to `UpBrushDX`/`DownBrushDX`. So **three colour pickers set to Transparent** — *Candle up bars*, *Candle down bars*, *Doji* — erase the bars completely with no code at all. This is also the test in §7.

**What survives — all of it.** `ChartStyle.OnRender` is a pure painter; the `Bars` object still holds every value.

| | Survives | Basis |
|---|---|---|
| Price (y) axis, gridlines, time axis | yes | INFERRED-strong — scale is computed from data, not pixels |
| `ChartScale.MinValue/MaxValue`, auto-scaling | **yes** | VERIFIED — driven by `ChartBars.Properties.AutoScale` + `OnCalculateMinMax()`, which read `Close.GetValueAt(index)`, not the painter |
| Last-price marker | yes | VERIFIED — independent `PaintPriceMarker` |
| Crosshair / Data Box readout | yes | VERIFIED — independent `DisplayInDataBox` |
| Chart Trader order lines, position line, executions | yes | INFERRED — separate render layer, separate properties (`PlotExecutions`, `PositionPenWinner/Loser`, …) |

**The one thing that would break it, which we therefore never do:** `ChartStyle.IsVisible = false`. NT support's stated caveat is that some series must stay visible for the time and price scales to work — UNVERIFIED behaviourally. Do not build on it.

**Two hard prohibitions for the indicator code:**
- **Never write to `ChartBars.Properties.*` brushes.** Docs: *"NOT guaranteed to take effect."* Worse, that object is the live, shared, serialized style — the change is written into the workspace and **does not revert when the indicator is removed**. It permanently alters the user's template. (`ChartStyle.IsTransparent` is read-only anyway: `error CS0200`.)
- Belt and braces regardless: set `IsAutoScale = true` in `SetDefaults` and override `OnCalculateMinMax()` to return SizeMap's own visible band (heat extent ∪ trace extent ± 2 ticks). ~10 lines, VERIFIED API. Then the scale is driven by what SizeMap actually draws, and it stays correct even if someone sets `AutoScale = false` on the now-invisible series.

---

## 2. WHETHER HE SHOULD

Different question, and the answer is not "yes, obviously".

**For:**

1. **With `SetZOrder(-1)`, the candles paint *over* the heatmap.** During absorption the body sits exactly on the wall row being eaten. The money shot of the entire product is occluded by the one object we do not control. This is the strongest argument and it is structural, not aesthetic.
2. **The 240× mismatch is real and it is a lie about time.** At `BarDistance` 17 px on a 1-minute chart, one pixel column of heat is 3.53 s; the candle beside it is a 60 s summary drawn as one 17 px glyph. The wick says the bar touched a price; it does not say *when*, and the heat column right there does. Reading them together requires holding two clocks.
3. **The candles cost the ramp two hue arcs** — the constraint that disqualified the green-to-red variant outright (spec §03).

**Against:**

1. **He is discretionary and he already reads candles.** Wick rejection, engulfing, session-open structure — that is his entry language, and the heat does not speak it. Removing it removes context, not clutter.
2. **A candle-less chart cannot be shared, and he shares.** Screenshots for @javiertradess, journal review, comparing against anyone else's chart. A stepped white line over a blue field is unreadable to everyone who does not already run SizeMap — including future-him at 6 months.
3. **At his working zoom the trace re-becomes a candle, only worse.** 14 quanta fold into one 17 px-wide column; min/max renders a high/low envelope. That is an OHLC bar chart missing the open and the close. The trace only beats the candle once a column holds ~1 quantum — `BarDistance ≥ 240 px` on 1-min, `≥ 120 px` on 30-sec.

**Verdict: build the trace so it is right in both worlds, and make hiding the candles a recommendation, not a requirement.**

- **Ship with candles ON as the installed default** and the trace **OFF**. With candles on, the trace at z-order −1 renders *behind the bodies* and would appear chopped — that looks broken, and the candle is the price anyway. Redundant object, hide it.
- **Recommend candles OFF** (SizeMapNullStyle) in the README and the workspace template, where the trace turns ON and becomes the price.
- One bool, one palette, one code path. No second colour system, no mode-detection heuristics.

This also settles the sharing problem honestly: he keeps a normal chart for anything that leaves his screen, and runs the null style on the chart he actually trades from.

---

## 3. THE WINNER

**T1 ACHROMATIC wins.** With four grafts from T2, three from T3, and one result that neither of them got and that falls out of combining them.

**Why T1 and not T2 (red/green).** T2's own numbers kill it. Its cores fail (ΔL < 0.10) against ramp stops 5 and 6 — `#6187AE` and `#A79F73`, *the middle of the ramp*, which is where ordinary resting size lives and therefore the most common background on the screen. T1's neutral core fails only against stops 8 and 9 — the rare extremes. **Same casing, but the conventional palette leans on it most of the time and the neutral palette leans on it almost never.** On top of that: red/green needs a mode switch to survive candles-ON (T2 needs its `TraceMode` enum and a `ChartStyleType == 1770` sniff), it permanently spends the two arcs the ramp was built to avoid, it double-encodes against the ledger's rose/grey (Δhue 58.7° — the tightest pair in the whole system, by T2's own measurement), and T2 concedes in §1.6 that **hue was redundant coding all along**: ask is above bid, always, by construction.

**Why not T3 (the touch ribbon).** T3 has the best *idea* in the three documents — the map is structurally incapable of rendering the touch (1 lot → LUT 8, ΔL 0.006 against bg; 5 lots → ΔL 0.088, under its own floor), so a dedicated encoding there overwrites nothing. Four things disqualify it for v1:

1. **It cannot satisfy the brief.** ΔL 0.004 against NT8's green candles. T3 says so itself: requires candles OFF, absolutely. The design must work both ways.
2. **Its failure mode collides with the load-bearing honesty rule.** T3 §9 admits the fill candy-stripes on real tape. High-frequency chroma noise reads as *texture*, texture in this grammar means *dashed*, dashed means *remembered*. The ribbon would look like a memory of itself. The mitigation is an EWMA that T3 correctly calls a small lie and that delays the absorption read by ~1 s at exactly the break.
3. **On ES it delivers nothing all day** — spread locked at 1 tick. He trades ES/NQ.
4. It needs new machinery (per-quantum traded/cancelled attribution at the touch) for an encoding that degrades to 3 distinguishable states at the working zoom.

**Grafts taken:**

| From | What | Why |
|---|---|---|
| T2 | **Boundary placement** of the core (on the row edge, not the row centre) | 3× less intrusion into the quoted cell, and it reuses the groove pixel |
| T2 | **Carry-seeded min/max reducer** | the stepped connectors fall out of the fill; no connector pass exists at all |
| T2 | **Mutual clip** (`askMin ≥ bidMax + 1 tick`) | ask is always above bid at every fold, 4 lines |
| T2 | **Pre-REC ground = ink `#0E1013`, not bg** | free memset; removes the "bg = ramp stop 0 = zero resting size" lie |
| T2 | Conditional 8-neighbour casing when `h > 4` | fixes the naked flank T1 explicitly ponytailed away, 2 lines |
| T3 | **Print bubbles killed outright** | every print occurs at or inside the touch by definition, so every bubble lands on the trace |
| T3 | **No interpolation across silent historical columns** — draw a gap | no print, no observation |
| T3 | **HUD disclosure tokens** (`SPRD` / `EXT`, `TR ON · Q:REAL|Q:SYN`) | makes the resolution transition visible instead of silent |

**The result neither got.** Place the ask core one pixel *above* the ask row and its lower casing lands on `rowTop(ask)` — which, when the ask is parked on a confirmed wall, **is the wall's own 1 px ink groove**. Same colour, so the write is a no-op. The 3 px composite then costs the wall **zero heat pixels**. That kills T1's runner-up failure ("the trace occludes the exact wall it is eating"), removes T1's demand that the heat rasterizer be re-anchored, and gives T3's "yield the row to BEACON" rule for free with no special case.

### Every disagreement, resolved

| # | Disagreement | Ruling |
|---|---|---|
| 1 | Trace colour: neutral / red-green / rose-grey | **Neutral.** §3 above. |
| 2 | Row placement: centred (T1) vs boundary (T2) vs yield+seam (T3) | **Boundary.** Heat rasterizer unchanged. |
| 3 | Spread: ink ribbon fill (T1) / empty gap (T2) / colour fill (T3) | **Empty gap.** T1's ribbon is why T1's own §9 admits the trace out-shouts the heat: at a 1-tick spread it draws a 3-row ink block. And its justification is wrong — the void is *already drawn*: ramp stop 0 is "no resting size". Painting it ink re-encodes what the ramp says and spends the one dark value reserved for structure. Casing only. −60% writes. |
| 4 | Carry across silent columns | **Live: yes** (a depth event confirms the book is still there). **Historical: no** (no print = no observation). Principled split, not a compromise. |
| 5 | Envelope overlap at high fold | **Mutual clip** (T2), not T1's merged white block — ask must stay above bid at every zoom. Disclose with T3's `EXT` token. |
| 6 | Bubbles: before the trace (T1) / after at alpha (T2) / killed (T3) | **Killed.** If ever resurrected: before the trace. |
| 7 | History weight: dashed (T1) / full weight (T2) / NULL-grey (T3) | **Dashed, core only, no casing.** T2's "the data is real, don't dim it" loses to the existing grammar: quote-at-trade is a different observation class, and *solid = observed, dashed = reconstructed* is already the spec's load-bearing rule. |
| 8 | Glyph vs trace on a shared pixel | **Glyph wins** (drawn after). Reverse of T2. Losing 13 px of a 1600 px line is nothing; losing an outcome glyph is losing the outcome. |
| 9 | Last / VWAP | Not drawn. All three agree. |
| 10 | Diagonals | Never. All three agree. |
| 11 | SYN detector threshold | ≥99% exact-1-tick spreads over ≥2000 historical ticks **and** live spread variance > 0. |

---

## 4. THE FINAL SPEC — THE TOUCH TRACE

### 4.1 What is drawn

Two series: **best bid** and **best ask**. Nothing else. No last (every print is at or inside `[bid, ask]`, so a last-line lies on top of a line already drawn). No VWAP (bar-derived statistic on a different clock; NT8 ships one, one click away).

Stepped, strictly orthogonal. Horizontal runs at a constant tick, vertical risers at the column where the change is first observed. **No diagonals anywhere** — price lives on a 0.25 grid; a diagonal renders prices that never existed.

### 4.2 Colours — two tokens, both already in the palette

| Role | Hex | BGRA int | OKLab L | Chroma |
|---|---|---|---|---|
| **trace core** | `#E6EAEE` (= existing `text`) | `unchecked((int)0xFFE6EAEE)` = `-1643794` | 0.935 | **0.007** |
| **casing** | `#0E1013` (= existing `ink`) | `unchecked((int)0xFF0E1013)` = `-15855597` | 0.172 | 0.007 |

Zero new tokens, zero hue spent. Both fully opaque — alpha stays `0xFF` per spec §06, so premultiply never enters the trace path.

**Legibility is a luminance dipole, and it is complete.** The core fails only where `L_bg ∈ (0.835, 1.0]`; the casing only where `L_bg < 0.272`. Disjoint, with a 0.56 gap. Worst composite over the **entire 256-entry LUT**: **ΔL 0.382** at idx 107 `rgb(78,117,164)`, where core and casing are exactly balanced. Theoretical floor against *any* colour that can ever exist: **ΔL 0.381 at L 0.554 — 3.8× the failure line.** Core↔casing internal edge: ΔL 0.763, WCAG 15.76, so the 1 px core never dissolves into its own casing. **There is no background that defeats this object, and there is nothing to patch.**

Which is exactly why the casing is not optional and must never be dropped "for a cleaner look".

### 4.3 Geometry — px exact

```
tick  = Instrument.MasterInstrument.TickSize
rowTop(p) = (int)Math.Round(scale.GetYByValue(p + tick*0.5));      // top edge of price row p
rowBot(p) = (int)Math.Round(scale.GetYByValue(p - tick*0.5)) - 1;  // bottom edge
pxPerTick = rowBot(p) - rowTop(p) + 1;
```

**Ask composite** (3 px, y grows downward):

```
rowTop(askMax) - 2   casing   #0E1013
rowTop(askMax) - 1   CORE     #E6EAEE     <- one px ABOVE the quoted row
rowTop(askMax)       casing   #0E1013     <- the ask row's own upper groove
```

**Bid composite**, mirrored: core at `rowBot(bidMin) + 1`, casings at `+2` and at `rowBot(bidMin)`.

**Consequence, stated because it is the whole point:** when the ask is parked on a confirmed wall, the lower casing pixel is the wall's existing ink groove (spec §04: confirmed wall = 1 px ink groove above + below). Same colour → no-op write. **The composite costs the wall zero heat pixels.** The wall keeps every one of its `pxPerTick` rows through the entire absorption.

**Folded columns:** the core is a vertical run `rowTop(askMax)-1 … rowTop(askMin)-1`, casing 1 px above and below the run. When run height `h > 4`, add left/right casing (full 8-neighbour dilation) — steep risers are the only place a long vertical flank meets the heat, ~10% of columns.

**Zoom gates** (NQ, tick 0.25):

| px/tick | ticks visible on a 600 px pane | Behaviour |
|---|---|---|
| **≥ 4** | ≤ 150 | full grammar, 3 px composite both sides |
| 2 – 4 | 150 – 300 | **outer casing only** (2 px composite) — preserves the gap |
| **< 2** | > 300 | **collapse to one mid line**, 3 px composite at `round(GetYByValue((bid+ask)/2))`. HUD reads `MID`. |

Below 2 px/tick the two composites merge into a band; do not pretend to draw two.

### 4.4 Downsampling — carry-seeded min/max, then mutual clip

Per column, per side, four ints in tick space: `askMax askMin bidMax bidMin`.

```csharp
// pass 1 — carry-seeded envelope. carry = the quote IN FORCE at the column's left edge.
int cx = 0; int carry = quoteAtWindowStartTicks;
foreach (var q in quotesInWindow) {                     // time-ordered
    int x = ColumnOf(q.Time);
    while (cx < x) { lo[cx] = hi[cx] = carry; cx++; }   // silent column -> flat 1 px run
    if (!touched[x]) { lo[x] = hi[x] = carry; touched[x] = true; }   // <-- the seed
    if (q.Ticks < lo[x]) lo[x] = q.Ticks;
    if (q.Ticks > hi[x]) hi[x] = q.Ticks;
    carry = q.Ticks;
}
while (cx < w) { lo[cx] = hi[cx] = carry; cx++; }

// pass 2 — mutual clip. Compute BOTH from the originals; never chain.
int aMin = Math.Max(askMin[x], bidMax[x] + 1);
int bMax = Math.Min(bidMax[x], askMin[x] - 1);
```

The seed is the whole trick: column `x+1` always contains the last value of column `x`, so consecutive segments always touch and **the stepped line comes out connected with no connector pass**. It is also the correct reading of a step function — the quote held from before the column began. Neither clipped side is ever empty: at the sample where the bid peaked, ask ≥ bid + 1 tick.

**Never a mean.** A mean invents a price that never existed on a 0.25 grid — the same lie the 1-tick/250-ms quantum rule already forbids. Min/max keeps only values actually quoted, makes line thickness read as volatility for free, and is idempotent under further downsampling (`min(min(a),min(b))` is correct; mean-of-means is not once column widths differ). Same operator, same reason, as audio waveform rendering.

**Arithmetic** (`BarDistance` is VERIFIED as the pixel pitch — no need to derive it from two `GetXByBarIndex` calls):

| Chart | `BarDistance` | ms / px column | 250 ms quanta folded |
|---|---|---|---|
| 1-min | 5 px | 12 000 | 48 |
| 1-min | **17 px** | 3 529 | **14.1** ← the working zoom |
| 30-sec | 17 px | 1 765 | 7.1 |
| 1-min | 60 px | 1 000 | 4 |
| 30-sec | **120 px** | 250 | **1 — exact** |
| 1-min | **240 px** | 250 | **1 — exact** |
| RTH session on 1600 px | — | 14 625 | 58.5 |

**Thickness equals the spread exactly when a pixel column holds one depth snapshot.** Above that it is an envelope: extent, not spread. Disclosed in the HUD — `SPRD` at ≤1 quantum/column, `EXT` above 8. Cheap, honest, makes the transition visible instead of silent.

### 4.5 History — left of the REC rule

Depth still has no history (`OnMarketDepth` never fires historically — unchanged, the heat still starts at REC). **Price does**, with Tick Replay: `OnMarketData` fires on historical bars, and `MarketDataEventArgs` carries `.Bid` and `.Ask` — the inside quote at the moment of each trade.

**Route A (Tick Replay), not Route B.** One user checkbox, and a *single* event stream yields price, bid, ask, trade size and aggressor. Route B (`AddDataSeries(..., MarketDataType.Bid/.Ask)`) gives a strictly better quote trace — all quote events, not just quote-at-trade — but **no trades at all**, is mutually exclusive with TR, arrives only through `OnBarUpdate`, and costs two 1-tick series where quote updates outrun trades by 10–100×. Build one path.

Enable: **Tools > Options > Market Data > "Show Tick Replay"** (hidden by default), then tick **Tick Replay** on the Data Series. If he never does: `OnMarketData` simply never fires historically, the trace starts at REC alongside the heat, nothing crashes, nothing is drawn wrong.

**Rendering left of REC:**

- **Ground = ink `#0E1013`, not bg.** Free — the region is memset every frame anyway, just a different int. This removes a real lie: `#232424` is simultaneously "chart background" *and* "ramp stop 0 = zero resting size", so leaving pre-REC at bg tells the trader the book was empty.
- **Trace = 1 px `#E6EAEE` core, dashed 4-on/4-off, no casing.** Against ink ground the bare core is ΔL 0.763. Dropping the casing is not a compromise — there is no heat there for it to fight.
- **Columns with zero historical prints draw nothing.** A visible gap, never an interpolation. No print, no observation.
- No collision with remembered walls: the regions are disjoint (no wall glyph can exist left of REC), and the dash phases differ anyway (walls 8/8, trace 4/4).

**SYN detector — mandatory, ~8 lines.** VERIFIED footgun: *"If the data provided has no bid/ask data tied to the last tick data, NinjaTrader substitutes the bid/ask data (i.e. Bid = Last price, Ask = Bid + 1 tick)."* Rendered naively that is a flawless, perfectly-1-tick spread that is **pure fiction and looks like the best data in the file**.

```csharp
// ponytail: two counters. Ceiling: won't catch a provider that synthesises a
// *varying* fake spread. Upgrade path = compare the historical spread histogram
// to the live one, only if a real feed ever fools this.
if (histTicks > 2000 && histOneTick * 100 > histTicks * 99 && liveSpreadVaries)
    histMode = HistMode.SingleDashedMid;   // one line at the trade price, HUD "Q:SYN"
```

In `Q:SYN` the two dashed lines collapse to one at the trade price. Better one honest line than two beautiful lies.

### 4.6 Composition with the existing grammar

Draw order — one raster, later write wins:

```
1  ground        (bg live / ink pre-REC)
2  heat field
3  depth-vision envelope  #3A3F44
4  wall grooves / left caps / saturation marks
5  remembered hollow rules + 3 px side ticks
6  ★ TRACE casing → TRACE core ★
7  outcome glyphs  (● ▲ ✕ ◇)
8  ledgers
9  REC rule
10 HUD
11 legend
```

| Element | Collision | Resolution |
|---|---|---|
| **Wall groove** | the ask composite's lower casing lands on it constantly — that *is* the moment | Same colour, no-op write. The wall keeps all its heat rows. See §4.3. |
| **Remembered hollow rules** | also thin 1 px horizontals | Three independent separators: hollow-vs-solid form, the trace is always cased and they never are, and their dash duty encodes confidence while the live trace is never dashed. |
| **Outcome glyphs** | land where the trace tends to be | Glyphs drawn after (layer 7). They win. 13 px of a 1600 px line is nothing. |
| **Ledger** (rose/grey, in the right margin) | none | The trace is in the plot, the ledger is in the margin. Rose/grey stay unambiguously "traded/cancelled". |
| **Depth-vision envelope** | the trace must lie inside it | Free invariant — the touch cannot be outside depth vision. Worth one `Debug.Assert`. |
| **Print bubbles** | — | **Not built.** Every print is at or inside the touch, so every bubble would land on the trace. |

### 4.7 The two modes

| | **Candles OFF** (recommended) | **Candles ON** (installed default) |
|---|---|---|
| Chart style | `SizeMapNullStyle` (or 3 transparent pickers) | user's own |
| `Trace` property | `On` | **`Off`** — opt-in `Always` |
| Palette | identical | identical |
| Z-order | −1 | −1 |
| Note | the trace is the price | the candle is the price. At −1 the trace renders *behind* the bodies and appears chopped — that reads as a bug, not a feature. `Always` is for the trader who wants the sub-bar quote in the gaps, and he should expect it to be interrupted. |

`enum TracePaint { Off, On, Always }` — one property, one branch, no palette switch, no style sniffing.

### 4.8 Cost

Pane 1600 × 600, `E[h] ≈ 5.8` rows at 4 px/tick.

| Item | Writes / frame |
|---|---|
| core + vertical casing, both sides | ≈ 25 000 |
| side casing (`h > 4`, ~40% of column-lines) | ≈ 19 000 |
| REC rule + labels | ≈ 2 000 |
| **trace total** | **≈ 46 000** |
| heat field (the floor) | 960 000 |
| **share of frame** | **≈ 4.5%** |

At NT8's 250 ms repaint cap: ~184 k int writes/sec on a sequential `int[]` — **≈ 0.05 ms/frame.** Not measurable.

State: four `int[1600]` (`askLo askHi bidLo bidHi`) = **25.6 KB**, allocated once in `DataLoaded`/on resize, plus two carry ints. No per-frame allocation. Zero new Direct2D primitives, still **one `DrawBitmap` per frame**.

**Deliberately skipped:** the recency fade (solid at the right edge, progressively dashed leftward, so history recedes and the live edge shouts). One modulo in the write loop. *Add it when the first real screenshot shows the heat reading as background texture under the trace.*

---

## 5. Z-ORDER ARCHITECTURE

**One indicator. `SetZOrder(-1)`. Candles hidden.**

Bars sit at ZOrder 1; NinjaScript objects default to 10001; `-1` is behind bars, `int.MaxValue` topmost. Higher = on top (the docs' sentence *"Objects with a higher ZOrder are drawn first"* is internally inconsistent with its own recipes; the recipes are right). ZOrder is per chart object — one raster, one z-level, all or nothing. `IsSeparateZOrder` only splits *draw objects* off from their creator and SizeMap uses none; irrelevant here.

Staying at −1 with the bars gone means every remaining NT layer — price marker, Chart Trader lines, crosshair, drawing tools, other indicators — paints on top of the raster. That is exactly correct stacking for a background heatmap, and there is nothing left to occlude the trace.

**The second indicator, costed and rejected.** A "SizeMap Trace" at default z-order, so the trace paints over the candles while the heat stays behind them:

- +150–250 lines: a second `Indicator` class, its own `OnStateChange`/`OnRender`/raster/resize path, its own properties, and its own `OnMarketData` subscription.
- A **second depth/quote subscription to the same book** (INFERRED — subscriptions are per NinjaScript instance), doubling the hot-path event work for data we already have.
- A second raster: 1600 × 600 ints ≈ **3.8 MB** and a second `DrawBitmap` per frame.
- Cross-instance state sharing via a static registry keyed by instrument + chart — which is the part that breaks on workspace reload, on two charts of the same instrument, and on the user adding one indicator without the other.
- Two things to install, two to configure, two to keep in sync in the repo.

Bought: the ability to see a 1 px quote line on top of candles that already tell you the price. **Not worth it. One indicator, one file, one raster.** If he keeps candles on, the honest answer is that the trace is redundant, and the `Off` default says so.

---

## 6. DELTA AGAINST VISUAL SPEC v1

| § | Change |
|---|---|
| **01 The two frames** | Re-render both frames with the trace present and no candles. Add one line to frame A's caption: the trace is the price, the candles are gone, here is the click path. |
| **02 Why the built-in is unusable** | Unchanged. |
| **03 BEACON — the ramp** | **Unchanged. The forbidden arcs stay forbidden — see below.** |
| **04 The grammar** | Two rows added to the state table: **Touch — observed** (solid 1 px core `#E6EAEE`, 1 px ink casing above and below, no glyph) and **Touch — reconstructed** (dashed 4/4 core, *no* casing, left of REC only). The honesty rule gets a third application, stated once: *solid = observed, dashed = reconstructed* — for walls (hollow), for confidence (dash duty), and now for the price track. |
| **05 The ledger** | Unchanged. |
| **06 Deliberately not drawn** | Three edits, one addition. **"Bid/ask hue"** — stays, and gets stronger: position encodes side inside the window, the core is achromatic at chroma 0.007, and hue was never carrying information. **"Historical backfill"** — **amended**: *depth* backfill is still impossible and the REC rule still marks it; *price* backfill is now drawn under Tick Replay, dashed, on ink ground, with the SYN detector, and the two are visually distinct observation classes. **New entry — "Trade bubbles"**: every print occurs at or inside the touch, so every bubble lands on the trace; the trace already says "traded at the touch", continuously, with no density artefact. **New entry — "A spread fill"**: the rows between bid and ask contain no resting size, and ramp stop 0 already means exactly that. Painting them would re-encode what the ramp says and spend the one dark value reserved for structure. |
| **07 Only you can answer these** | Two questions added: (a) measure the touch-size distribution on his own NQ/ES feed — the workspace has this for **ES resting order size** only (77% are 1 lot, mean 1.52); (b) is Tick Replay's load time tolerable on his machine, given bars are rebuilt from 1-tick data. |
| **HUD** | Three new tokens: `TR ON · Q:REAL` / `TR ON · Q:SYN` / `TR OFF`; `PX/T n` with `MID` when < 2; `SPRD` at ≤1 quantum per column, `EXT` above 8. |
| **Legend** | +2 rows on the existing plate: a 3 px `TOUCH` composite (casing/core/casing) and a dashed `REPLAY` sample. ~14 px. |
| **Ships** | One new file: `SizeMapNullStyle.cs` → `bin\Custom\ChartStyles\`. NT's export utility has a dedicated `ExportType_ChartStyles` category (VERIFIED), so it packages cleanly alongside the indicator. |

**Should the forbidden hue arcs be relaxed now that the candles can go? No.** Three reasons, in descending order of force:

1. **Relaxing buys nothing measurable.** The ramp already delivers +0.084 OKLab L per step (≈8.5 JND) and 13.84:1 at the top stop. There is no unmet requirement that red or green would satisfy. Relaxing a constraint that costs nothing is not a gain, it is churn.
2. **The constraint had a second, independent justification.** Spec §02: the ramp is monotone under protanopia and deuteranopia both — *"which the candles themselves are not."* A red-green ramp fails that whether or not NT8's candles are on screen.
3. **Candles-OFF is a mode, not a guarantee.** Candles ON is the installed default (§2), he will flip back for screenshots and for sharing, and the collision returns the instant he does.

State it in §03 as a strengthened rule: the arcs are reserved because the ramp is a magnitude channel and hue is not a magnitude, and because SizeMap's own trace, the ledger's rose/grey and NT8's candles all live outside it.

---

## 7. THE ONE THING JAVIER MUST LOOK AT

**Trade one full session with the candles already invisible — using the three-click test that requires zero code — and write down one number before he starts.**

1. Open his normal NQ chart at the zoom he actually trades.
2. **Read the y-axis and count the ticks visible over the plot height.** Write it down. ≤150 ticks → 4+ px/tick, the full grammar works. 150–300 → two lines, thin casing. >300 → the trace is one mid line and this whole debate is decided for him.
3. Right-click → **Data Series…** → set **Candle up bars color**, **Candle down bars color** and **Doji color** to **Transparent**. OK. (Reversible in ten seconds; nothing is installed.)
4. Trade the session.

**The decision rule:** if he reaches for the candles — if he catches himself squinting for a wick, a body, a session open — then candles-ON is the default and the trace ships `Off`, exactly as specified in §4.7, and the null ChartStyle is a README footnote for the days he wants the clean view. If he does not miss them, ship `SizeMapNullStyle` and make candles-OFF the recommended template.

Nothing else settles this. Every number in this document is about whether the trace *can* be drawn legibly, and the answer to that is a proven yes (ΔL 0.381 floor against any colour that can exist). Whether he should trade without candles is a question about his eyes and his habits over six hours, and it costs three clicks and one session to answer definitively.

---

**Artefacts:** `…/scratchpad/NullStyle.cs` (`nt8c check` clean, ships as-is) · `…/scratchpad/{col.py,colors.py,final.py,t3.py}` (colorimetry) · palette source of truth `/home/javlo/Code Projects/main-project/projects/Trading/SizeMap/docs/design/preview.py` · spec v1 `…/docs/design/spec.html`.

**Skipped:** recency fade on the trace, per-column spread/extent separation via inner hairlines, print bubbles, the second indicator. *Add the fade when a real screenshot shows the heat reading as background texture; add the inner hairlines if fat columns at 1-min zoom read as spread blow-outs that were not; never add the other two.*