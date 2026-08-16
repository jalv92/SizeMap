# SIZEMAP — FINAL VISUAL SPECIFICATION v1

Ramp: **BEACON**. Direction: **INSTRUMENT**, with the grafts both judges named. Everything below is a build instruction, not a proposal.

---

## 1. THE LOOK, IN THREE SENTENCES

You open the chart and your own candles are exactly where they were — untouched, full contrast, drawn on top of everything SizeMap does — sitting inside a narrow blue-to-yellow ribbon of resting size that tracks price, framed above and below by a thin chevron line that traces exactly how far your feed could see at every moment in history.

Away from that ribbon the chart is your own background, empty, except for a small number of **hollow** horizontal bands — walls the feed lost sight of, still drawn at the brightness they had when last seen, their outline dashing thinner as confidence decays — each ending in one of four hard 9 px glyphs that says what happened to it.

In the blank margin to the right of the last bar, three 44 px bars are stacked: for each of the biggest live walls, how much is still resting, how much was **bought** (rose), and how much was **cancelled** (grey) — which is the one sentence no competing product's screenshot contains.

---

## 0. DISAGREEMENTS RESOLVED

| # | Disagreement | Verdict | Why |
|---|---|---|---|
| 1 | All three directions: "cannot render behind the bars." Mechanics brief: `SetZOrder(-1)` works. | **Mechanics brief wins.** `SetZOrder(-1)` in `State.Historical`, price panel only, `DrawOnPricePanel = false`. **All knockout / punch-out / cutout passes are deleted.** | It is one verified line and NT ships `@SampleCustomRender.cs` doing it. D3's punch-out (High→Low + 2 px) would have deleted the live book on 1-min ES. Consequence accepted: z-order is all-or-nothing, so HUD/legend/glyphs are also behind the bars (see §9 Q4). |
| 2 | Remembered = 50 % stipple (D1, Judge 2 liked it) vs hollow rules (D3, Judge 1 grafts it). | **Hollow rules.** No fill; 1 px rule at band top and bottom in the **undimmed** `LUT[idxLastObserved]`; dash duty = confidence. | Judge 1's measurement decides it: an unresolved 50 % checker optically averages **ΔL −0.145 ≈ 1.9 ramp stops**, so stipple lies about the magnitude it claims to preserve. Hollow costs 2 px of ink per wall instead of a filled band, which also kills D1's own §12 "fog" risk at 10 levels. |
| 3 | Ledger at a fixed right gutter (D1) vs the future margin (D2, Judge 1 grafts it). | **Future margin**, left edge anchored at `x(now)`. | The fixed gutter covers the freshest ~12 s of every wall. The margin is empty, is where the eye already is, and aligns every ledger into one scannable column. |
| 4 | Ledger count: 6 (D1) vs 1 (Judge 2). | **3.** Top 3 by peak size. | Judge 2's cut was priced against Direct2D primitives; the ledger is written into the `int[]`, so 3 costs ~4,500 int writes and zero draw calls. 1 loses comparison; 6 is clutter. Knob `MaxLedgers`, default 3. |
| 5 | Traded/cancelled = solid `#E6EAEE` vs 33 % hatch (D1) — Judge 1 calls it the single worst element. | **Judge 1 wins: chromatic vs achromatic.** traded = rose `#F14BE9`, cancelled = flat `#97A1AC`, remaining = the wall's own LUT colour. Hatch deleted. | `#E6EAEE` (L 0.918) against `#FFF49D` (L 0.957) is ΔL 0.039 — invisible — and a 3 px hatch is one dot. Chroma-vs-no-chroma survives 1 px, zoom-out and peripheral vision. |
| 6 | Print bubbles: full spec (all three) vs cut entirely (Judge 2). | **Cut from v1**, fully specified in §4 for v2. | It is a second data pipeline (`OnMarketData` + aggressor inference + rolling P99) and D1's raster-drawn discs are aliased blobs at r ≤ 3. Rose stays in v1 via the ledger and the ● glyph, so the mnemonic is already taught when discs land in v2. |
| 7 | Text: DirectWrite (all three, mechanics brief documents it) vs hand-authored 5×7 bitmap font (Judge 2). | **Bitmap font.** 5×7 1-bit, charset `0-9 A-Z . · % ! -`, integer ×1 / ×2 scale. | Deletes the whole `TextFormat`/`TextLayout`/DPI/`NoSnap`/grayscale-AA bug class (Judge 2: 1–1.5 days of the fiddliest bugs) and makes the post-blit primitive count exactly **zero**. Consistent with the 1-bit glyph stencils already in the design. |
| 8 | Wall size labels: keep top-3 (D1) vs cut (Judge 2, "DirectWrite is the most expensive primitive"). | **Keep — one number per ledger, 3 total.** | Judge 2's cost argument evaporates once text is a bitmap blit into the raster (~20 chars × 35 px = 700 int writes). The peak-size number is what turns the ledger's proportions into lots. |
| 9 | Saturation mark for `size > sCap`: keep (D1) vs cut as redundant (Judge 2). | **Keep.** Two 1 px vertical rules, 1 px gap, `#E6EAEE`, at `x(now)−4 … x(now)`, full band height. | ~40 int writes. Without it the ramp silently reports a 2000-lot wall and a 340-lot wall as the same colour, which is exactly the honesty rule this document is built on. |
| 10 | `s0`/`sCap` rate limiter, ±12.5 %/min (D1). | **Cut.** Recompute every 5 s, no limiter. | Judge 2 is right that it is stability dressed as accuracy — and it becomes unnecessary once the store holds **lot counts** and the whole visible window is re-mapped through the current LUT every frame (§2). |
| 11 | Side encoding: hue (D3, 90° of budget) vs nothing (D1, "position encodes it" — false for remembered walls). | **Judge 2's third option:** a 1 px × 3 px tick on the band's left cap, up = was ask, down = was bid. | D3's insight is correct and its price is not. Zero hue, ~3 int writes. |
| 12 | Depth window shown as two straight rules (D1) vs a per-column envelope polyline (D3). | **Envelope polyline.** | Straight rules show the window *now*; the polyline shows it *through history*, which is what turns a 10-level feed from an embarrassment into a drawn statement. Zero primitives, one `int` per column per side. |

---

## 2. THE PALETTE — FINAL

### 2.1 The ramp — BEACON, 9 stops

`BGRA int` is the literal you write into the `int[]`; on little-endian x64 with `Format.B8G8R8A8_UNorm` the value `0xAARRGGBB` lands in memory as `BB,GG,RR,AA`, i.e. **`0xAARRGGBB` and BGRA are the same 32-bit value.** No byte swapping anywhere.

| i | LUT idx | hex | BGRA int | OKLab L | C | OKLCh H | CR vs `#232424` |
|---|---|---|---|---|---|---|---|
| 0 | 0 | *(the chart's own background)* | *composited, see §2.3* | 0.2593 | 0.001 | — | 1.00 |
| 1 | 32 | `#103772` | `0xFF103772` | 0.3470 | 0.111 | 259.0 | 1.34 |
| 2 | 64 | `#1F4F94` | `0xFF1F4F94` | 0.4341 | 0.125 | 258.0 | 1.93 |
| 3 | 96 | `#446B9E` | `0xFF446B9E` | 0.5221 | 0.093 | 255.6 | 2.85 |
| 4 | 128 | `#6187AE` | `0xFF6187AE` | 0.6107 | 0.073 | 249.7 | 4.14 |
| 5 | 160 | `#A79F73` | `0xFFA79F73` | 0.6982 | 0.061 | 99.1 | 5.82 |
| 6 | 192 | `#C9B97A` | `0xFFC9B97A` | 0.7847 | 0.084 | 95.6 | 7.93 |
| 7 | 224 | `#EFD472` | `0xFFEFD472` | 0.8727 | 0.122 | 94.3 | 10.62 |
| 8 | 255 | `#FFF49D` | `0xFFFFF49D` | 0.9568 | 0.108 | 102.0 | 13.84 |

Strictly monotone in OKLab L, steps **+0.0841 to +0.0886** (≈ 8.5 JND each). **Zero** of the 256 sRGB-lerped entries land in the RED arc `[349°,60°)` or the GREEN arc `[110°,177°)`. The 4→5 hop crosses hue 180° at interpolated **C ≈ 0.014**, three times under the C 0.045 threshold at which a colour can read as a candle at all. Total span ΔL 0.697, top stop **13.84:1** against NT8's own measured background — versus the built-in Order Flow Depth Map's loudest reading at **1.25:1**.

Dichromat check (Viénot 1999): protan min step **+0.0683**, deutan **+0.0838**, both monotone. The candles are not.

### 2.2 Accents — the whole rest of the palette

| role | hex | BGRA int | OKLab L | C | H | used for |
|---|---|---|---|---|---|---|
| **TRADED** (rose) | `#F14BE9` | `0xFFF14BE9` | 0.701 | 0.260 | 330.0 | ledger traded segment · ● consumed glyph · ▲ absorbed glyph · (v2) print discs |
| **NOT TRADED** (neutral) | `#97A1AC` | `0xFF97A1AC` | 0.687 | 0.020 | — | ledger cancelled segment · ✕ pulled glyph · ◇ unclassified glyph · secondary HUD text |
| ink / groove / halo | `#0E1013` | `0xFF0E1013` | 0.172 | 0.007 | — | wall groove, glyph dilation, ledger border/separators, panel border |
| text-primary | `#E6EAEE` | `0xFFE6EAEE` | 0.918 | 0.007 | — | numbers, escalated badges, saturation mark |
| legend/HUD plate | `#16191C` | `0xFF16191C` | 0.212 | 0.008 | — | opaque panel fill |
| envelope | `#3A3F44` | `0xFF3A3F44` | 0.365 | 0.011 | — | depth-vision polyline, REC rule |

**13 colours total: 9 encode magnitude, 1 encodes "a trade happened", 1 encodes "it didn't", 2 are chrome.** Rose sits **68° of hue** from the nearest ramp stop, dE_ok ≥ 0.267 from every ramp entry, 0.262 from both candle clusters, and 19° clear of the RED arc.

`#97A1AC` does double duty as data (cancelled) and chrome (secondary text). That is deliberate and it does not break the "text wears text tokens" rule: the token is *achromatic*, and achromatic is exactly the visual class "this is not a trade / this is not data". Rose is L 0.701, neutral is L 0.687 — **the two ledger segments differ only in chroma (0.260 vs 0.020)**, which is the cleanest categorical read available at 1 px and the whole point of Judge 1's fix.

**Deleted from the forensics inventory:** `#51CFBF`, `#EF4EE7`, the violet state trio (`#D1B5F6`/`#986FEB`/`#7B30BC`), `#4A5057`, and every "remembered = dimmed ramp colour" value. Terminal state is a **shape**, not a hue; remembered is **hollow**, not a colour.

### 2.3 Background handling

SizeMap draws **behind** the bars, so it composites against whatever NT8 already painted. Read it at runtime — never hardcode `#232424` (that is the *measured composite* the monotonicity math is verified against, not a constant):

```csharp
var P = chartControl.Properties;
uint bg = SolidBgra(P.ChartBackground, 0xFF141414);   // null/gradient -> fallback
double L = RelLum(bg);

// v1 supports dark charts only. Light theme -> paint our own opaque plate.
// Legal because we are BEHIND the bars: the candles still render on top at full contrast.
// ponytail: a light-theme ramp is a whole second palette. One rect fill until Javier asks.
if (L >= 0.18) { bg = 0xFF232424; FillRect(px, py, pw, ph, bg); themeOverridden = true; }
```

Because we composite on the CPU and blit **fully opaque (`α = 0xFF`)**, the premultiplied-alpha bug class disappears entirely and there is no blend pass. Ramp index 0 is not a colour, it is "write `bg`".

### 2.4 LUT construction and size → index

```csharp
// ---- built once per (background, theme) change. 256 entries, 1 KB. ----
static readonly uint[] Stops = {
    0x00000000,  // idx 0 is the background; filled in below
    0xFF103772, 0xFF1F4F94, 0xFF446B9E, 0xFF6187AE,
    0xFFA79F73, 0xFFC9B97A, 0xFFEFD472, 0xFFFFF49D };

void BuildLut(uint bg) {
    Stops[0] = bg;
    for (int i = 0; i < 256; i++) {
        int s = i >> 5, t = i & 31;                       // stops at 0,32,...,224,255
        uint c = LerpRgb(Stops[s], Stops[Math.Min(s + 1, 8)], t / 31f);
        // fade the foot INTO the background so "1 lot resting" is invisible, not grey noise
        lut[i] = (i < 24) ? LerpRgb(bg, c, i / 24f) : c;
    }
    lut[0] = bg;
}
// integer sRGB lerp vs true OKLab lerp: max error 0.0079 dE_ok, 0 L-dips, 0 forbidden-arc
// entries. No colour science in the render path.

// ---- normalisation: log, anchored to robust percentiles of the WHOLE book ----
// Recomputed every 5 s from a 30-min ring of non-zero level sizes. No rate limiter.
double s0   = Math.Max(1, P50);
double sCap = Math.Max(8 * s0, 8 * P85);

int Idx(double size) {
    if (size <= 0) return 0;
    double u = Math.Log(1 + size / s0) / Math.Log(1 + sCap / s0);
    return (int)Math.Round(255 * Math.Min(1, u));
}
```

**Store lot counts, never LUT indices.** The history ring holds `ushort` sizes; `Idx()` runs at render time over the whole visible window every frame. This is Judge 2's mandatory fix and it costs nothing (you re-rasterize anyway) — without it, a column painted at 14:10 at index 200 means a different lot count than one painted at 14:30, and the legend is only true for the rightmost pixel column.

**What the law produces:** one doubling of size ≈ **+42 LUT indices ≈ ΔL 0.115 ≈ 11 JND** (+37 near the floor, +46 near the cap). Worked NQ example, `s0 = 8`, `P85 = 40` → `sCap = 320`:

| size | 8 | 20 | 40 | 80 | 160 | 320 | 2000 |
|---|---|---|---|---|---|---|---|
| idx | 48 | 86 | 123 | 165 | 209 | 255 | **255 + saturation mark** |

**When a 2000-lot wall appears:**
- *Max-normalised:* `sCap` → 2000, every other level drops idx ~120 → ~30, the whole chart goes dark exactly when the interesting thing happens. **Rejected.**
- *Linear:* at a fixed cap, ~92 % of NQ levels land in idx 0–20 — NT8's failure with a nicer palette. **Rejected.**
- *This:* `sCap` is keyed to **P85 of the entire book**, so one freak level moves it by well under 1 %. The wall saturates at idx 255, the rest of the chart does not move a single index, and the excess is carried by **geometry** (the saturation mark) and by **number** (the ledger's peak label). Colour saturates; geometry carries the overflow. That sentence is the whole compression policy, and `LOG` is printed in the legend.

---

## 3. THE LAYER STACK

**One indicator.** `IsOverlay = true`, `IsChartOnly = true`, `DrawOnPricePanel = false`, `ScaleJustification = Right`, `Calculate = OnEachTick`, `SetZOrder(-1)` in `State.Historical` (re-issued once on the first `OnRender` after `State.Realtime`, for the first-apply bug). Price panel only — `SetZOrder(-1)` on a secondary panel throws.

Everything SizeMap draws lives in one `int[]`. Draw order inside the buffer:

| # | Layer | Contents | int writes, busy frame |
|---|---|---|---|
| 1 | Fill | `Array.Fill(pixels, bg)` — 1600×900 | 1,440,000 |
| 2 | Envelope | 2 polylines, top of observed ask / bottom of observed bid, 1 px `#3A3F44` | 3,200 |
| 3 | Heat field | observed levels, `FillCell(tick, slot, lut[Idx(size)])` | ≤ 480,000 |
| 4 | Wall grooves | 1 px `#0E1013` above + below each confirmed band, + left cap | ≤ 12 × 3,300 |
| 5 | Remembered rules | 2 × 1 px dashed, `lut[idxLastObserved]`, + side tick | ≤ 24 × 1,700 |
| 6 | Saturation marks | 2 × 1 px vertical, `#E6EAEE` | ≤ 12 × 40 |
| 7 | Death breaks | 1 px `bg` column at each re-promotion | ≤ 24 × 12 |
| 8 | Glyphs | ≤ 12 × 9×9 stencil + 1 px ink dilation = 11×11 | 1,452 |
| 9 | Ledgers | 3 × (44 × max(5, rowH)) + border + peak label | ~6,000 |
| 10 | REC rule | 1 px vertical `#3A3F44`, full height + label | 900 |
| 11 | HUD | 2 lines, 5×7 bitmap font | ~4,000 |
| 12 | Legend | 196×78 plate + LUT strip + swatches + labels | 15,288 |
| — | **`bitmap.CopyFromMemory(pixels, pw*4)`** | 5.76 MB | — |
| — | **`RenderTarget.DrawBitmap(...)`, `NearestNeighbor`, explicit dest rect** | | — |

**POST-BLIT PRIMITIVE COUNT ON A BUSY FRAME: 1.** One `DrawBitmap`. There are no `DrawText`, `DrawLine`, `FillRectangle`, `DrawEllipse`, `PushLayer` or `StrokeStyle` calls anywhere in `OnRender`.

Measured budget target: raster build ~3.0 ms + upload ~1.2 ms + blit ~0.2 ms = **≈ 4.4 ms of the ~60 ms available (7.3 %)**. That is an estimate; §5's frame-time readout exists so it stops being one.

Mandatory frame preamble:

```csharp
protected override void OnRender(ChartControl cc, ChartScale cs) {
    if (IsInHitTest) return;                        // full-panel raster => every click "hits" us
    if (RenderTarget == null || RenderTarget.IsDisposed) return;
    if (!ReferenceEquals(RenderTarget, rtSeen)) {   // identity guard: WindowRT vs WicRT
        ReleaseDeviceResources(); CreateDeviceResources(); rtSeen = RenderTarget;
    }
    int px = ChartPanel.X, py = ChartPanel.Y, pw = ChartPanel.W, ph = ChartPanel.H;
    if (pw <= 0 || ph <= 0) return;                 // snapshot: W/H can change mid-frame on drag-resize
    var oldAA = RenderTarget.AntialiasMode;
    try { RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased; /* ... */ }
    finally { RenderTarget.AntialiasMode = oldAA; } // restore what you READ, not a hardcoded default
}
```

`OnRender` never touches a live collection. `WallDetector` / `LiquidityMemory` / `ConsumptionTracker` run on the data thread inside `OnMarketDepth`; the render thread does one `Volatile.Read` of an immutable snapshot swapped by `Volatile.Write`. Never take a lock the depth thread holds — that stalls the whole platform, not just SizeMap.

**Geometry, and the honesty rule made structural.** The only function allowed to write heat is:

```csharp
void FillCell(int tickIdx, int slot, uint colour);  // fills the WHOLE cell rect or nothing
```

Rows come from one anchor per frame, never per-row `GetYByValue` (which returns `int` and makes rows drift 13,14,13,14 px against NT8's own gridlines — the exact defect that makes Screenshot_165 read as smeared slabs):

```csharp
float  pxTick   = chartScale.GetPixelsForDistance(Instrument.MasterInstrument.TickSize);
double anchorPx = Math.Round(chartScale.MinValue / tick) * tick;
int    yAnchor  = chartScale.GetYByValue(anchorPx);
float  RowTopF(int t) => yAnchor - (t + 0.5f) * pxTick;
int    rowsPerCell = Math.Max(1, (int)Math.Ceiling(1.0 / pxTick));   // sub-pixel rows -> aggregate
```
Columns come from pitch, not `GetXByTime` (slot-based; every timestamp inside a bar returns the same x):
```csharp
float pitch    = (x1 - x0) / (float)(i1 - i0);              // two GetXByBarIndex calls
float pxBucket = pitch / (barMs / 250);
int   fold     = Math.Max(1, (int)Math.Ceiling(1.0 / pxBucket));
int   columnMs = 250 * fold;                                 // PRINTED IN THE HUD
```
Merge across `fold` is **MAX, never MEAN** — mean erases exactly the transient walls the instrument exists to find. `rowsPerCell` and `fold` are both printed in the HUD as `COL 15S · ROW 3T`. Log-scaled Y axis → fall back to per-row `GetYByValue`. `BarSpacingType.TimeBased` → refused in v1 with a HUD line, because `GetSlotIndexByTime` throws.

---

## 4. THE VISUAL GRAMMAR

`rowH` = pixel height of one price tick's cell. **Full grammar at `rowH ≥ 3`.** Below that, grooves and rules collapse (column "≤ 2 px"), glyphs and ledgers keep fixed sizes anchored on the band centre.

| Object | Fill | Edge | Extra mark | Glyph (9×9 + 1 px ink dilation = 11×11) | Ledger | ≤ 2 px behaviour |
|---|---|---|---|---|---|---|
| **No data / below floor** | the chart's own background | — | — | — | — | — |
| **Live level** (present, unconfirmed) | solid, `lut[Idx(size)]`, full cell | none | — | — | — | unchanged |
| **Wall** — confirmed: `size ≥ K_mult × median` ∧ `≥ MinAbsSize` ∧ `≥ T_persist` ∧ `flicker ≤ F_flicker` | solid, `lut[Idx(size)]` | **1 px `#0E1013` groove immediately above and below the band**, from birth column to now, + 1 px groove at the birth column (left cap) | if `size > sCap`: **saturation mark** — two 1 px vertical `#E6EAEE` rules with a 1 px gap, at `x(now)−4 … x(now)`, full band height | none while alive | groove suppressed; band alone |
| **Remembered** (alive in `LiquidityMemory`, outside the depth window) | **NO FILL** | **two 1 px rules** at band top and bottom, colour `lut[Idx(sizeLastObserved)]` — **never dimmed, never decayed** | **side tick:** 1 px × 3 px vertical stub at the left cap, **up = was ask, down = was bid** | none | none | one 1 px rule at band centre, same dash duty |
| **Consumed** (`TBF ≥ 0.85`) | — | rules as remembered | — | **●** filled disc, **rose `#F14BE9`** | — | glyph fixed 9 px |
| **Absorbed** (`0.15 < TBF < 0.85`) | — | rules as remembered | — | **▲** filled triangle, **rose `#F14BE9`** | — | glyph fixed 9 px |
| **Pulled** (`TBF ≤ 0.15`) | — | rules as remembered | — | **✕** saltire, **`#97A1AC`** | — | glyph fixed 9 px |
| **Unclassified** (`EpisodeClassifier` refused) | — | rules as remembered | — | **◇** hollow diamond, **`#97A1AC`** | — | glyph fixed 9 px |
| **Death break** (a node re-promoted at a price where one already died) | — | **1 px column of background** at the death column, breaking the band | — | — | — | unchanged |
| **Depth-vision envelope** | — | 1 px `#3A3F44` polyline per column, top of observed ask and bottom of observed bid | — | — | — | unchanged |
| **REC start** | — | 1 px vertical `#3A3F44`, full plot height, label `REC 14:32:07` above it | — | — | — | unchanged |

**Confidence 0..1 → dash duty of the remembered rules.** 8 px period, `on = 2 × round(4 × conf)` px. Never alpha, never hue, never lightness.

| confidence | pattern | reads as |
|---|---|---|
| ≥ 0.875 | 8 on / 0 off | solid rule |
| 0.625 – 0.875 | 6 on / 2 off | long dash |
| 0.375 – 0.625 | 4 on / 4 off | even dash |
| 0.25 – 0.375 | 2 on / 6 off | dotted |
| < 0.25 | **not drawn** | gone (`ConfidenceFloor`, knob) |

**Two orthogonal, honest channels: the rules' COLOUR is the size at last observation and never decays; the rules' DASH is how sure we are it is still there.** Solid-band vs hollow-band is a *form* judgement — a stranger reads it from a screenshot with no legend, which is the requirement, and it is the only encoding of the four candidates that does not corrupt magnitude at any zoom.

**Caps.** Remembered objects: **24** on screen, ranked `confidence × peakSize`, hard cap not a fade. Grooved walls: **12** by peak. Glyphs: **12**, dropped by `confidence × peak`, **never nudged**. Ledgers: **3**. Only **confirmed walls** are ever remembered — plain levels are not, which is what keeps 10-level charts from turning into line soup.

### 4.1 The consumption ledger — the product

**Placement:** in the **future margin**, the blank strip right of the last bar. Left edge at `x(now)`, width **44 px**, height `max(5, rowH)`, vertically centred on the wall's band. 1 px `#0E1013` border, 1 px `#0E1013` separators. If the margin is narrower than 50 px, the ledger falls back to overwriting the band's rightmost 44 px.

Three segments, left → right, summing to exactly 44 px by largest-remainder, with a **1 px floor for any non-zero segment**:

| segment | width | fill | means |
|---|---|---|---|
| **R** remaining | `44 × current/peak` | the wall's own `lut[Idx(size)]` | still resting |
| **T** traded | `44 × (1 − current/peak) × TBF` | **rose `#F14BE9`** | someone paid for it |
| **C** cancelled | `44 × (1 − current/peak) × (1 − TBF)` | **`#97A1AC`** | the seller walked |

The **peak size in lots** is printed left of the ledger, right-aligned, 5×7 font, `#E6EAEE`.

Worked example — peak 400, current 160, Drop 240, Traded 210, TBF 0.875: **R = 18 px heat-coloured, T = 23 px rose, C = 3 px grey.** Same wall at TBF 0.10: R = 18, T = 3, C = 23. *How much of the eaten part is coloured?* is the whole read, and a 1 px segment means ">0 and <2.3 %", stated in the legend.

**TBF-bias disclosure (Judge 2, mandatory).** `ConsumptionTracker.Read` counts trades from `BookMirror`'s ring, which is pruned to `TradeRetention`. A wall older than that has had the trades that ate it pruned, so `Traded` under-counts and the ledger says "cancelled" for liquidity that was bought — a lie in the one number that is the entire product. **If `wallAge > BookMirror.TradeRetention`, omit the ledger's right border pixel**, leaving the track visibly open on the right. Open edge = "traded is a lower bound". Also raise `TradeRetention` to exceed the longest tracked wall lifetime and pay the memory.

### 4.2 Aggressive-print discs — **v2, specified now so it is a graft not a redesign**

Cut from v1 (§0 #6). When built:

- **Aggregate** signed volume into `(1 tick × 1 render column × aggressor side)`. Buy-initiated = traded at/above the prevailing ask; ties fall back to the tick rule.
- **Emit** only when `|v| ≥ Vmin = max(10, 4 × median(|cellVolume|, 30 min))` — NQ lands at 10–16, ES at 40–70. NQ tape is ~93 % one-lot with mean 1.09; per-print marks would be ~2.5 M identical dots per session.
- **Five discrete diameters** — 5, 7, 9, 11, 13 px — from pre-rendered 1-bit stencils with the ink ring baked in. `d = 5 + 2·floor(4 · ln(1+|v|/Vmin) / ln(1+vCap/Vmin))`, `vCap = P99` over 30 min. Discrete because a continuously-scaled disc at 4 fps and 1 px/tick is a lie about precision.
- **Fill: rose `#F14BE9`, always.** Rose already means "a trade happened" in the ledger and in ● — one hue, one concept, three places. **Rim: 1 px `#0E1013`.** Aggressor side is a **1 px notch on the ink ring, above = buy-initiated, below = sell-initiated.** Zero new colours.
- Cap **40 per frame**, keep the largest; HUD prints `+N` when clipped. Written into the raster, so still zero post-blit primitives.

---

## 5. THE HUD AND LEGEND

**Font:** hand-authored **5×7 1-bit bitmap**, charset `0-9 A-Z . · % ! - : / =` (`:` `/` for clock and file tokens; `=` because this section's own `X2 = 42 IDX` needs it — unknown characters render blank, so the first build of the legend had a hole where the `=` was), 1 px letter spacing (advance 6 px), 9 px line height. Scale is an **integer**: ×1 when `chartControl.M22ToDevice < 1.25`, ×2 at or above. Integer scaling of a 1-bit font stays pixel-crisp; that is the whole DPI story.

### 5.1 HUD — top-left of the plot rect, `(px+6, py+6)`, two lines

```
SIZEMAP  NQ 09-26   DEPTH 10L  OBS 10   COL 15S  ROW 1T
LOG  S0 8  CAP 320   WALLS 3L 11R 27D   1.4MS  4.0FPS
```

| token | colour | rule |
|---|---|---|
| `SIZEMAP  NQ 09-26` | `#E6EAEE` | — |
| **`DEPTH 10L  OBS 10`** — feed-truth badge | `#97A1AC` normally; **`#E6EAEE` (12.6:1 instead of 5.7:1) whenever `OBS < 20`** | `DEPTH` = configured levels, `OBS` = max levels **actually observed** this session. Escalation is a **contrast** change, not a hue change, so the text-token rule holds. |
| **`COL 15S  ROW 1T`** — time/space-truth badge | `#97A1AC` | Computed `columnMs` and `rowsPerCell`. This is the honesty rule made mechanical: on a 1-min chart a 250 ms bucket is 0.025 px, and 239 of 240 buckets are invisible. Say so. |
| `LOG  S0 8  CAP 320` | `#97A1AC` | The compression law and both live calibration numbers, always on screen. |
| **`WALLS 3L 11R 27D`** | `#97A1AC` | Live / remembered / dead census. `K_mult` is the entire design; this is its tuning instrument, not a settings dialog. Target band: **2–6 live, 10–25 remembered.** |
| `1.4MS  4.0FPS` | `#97A1AC`; **`!47.0MS` in `#E6EAEE` above 20 ms** | EMA (α = 0.1) over a `Stopwatch` around `OnRender`, so the number does not flicker at 4 fps. |

### 5.2 Legend — `196 × 78 px` at `(plotRight − 204, plotBottom − 86)`

Opaque plate `#16191C`, 1 px `#0E1013` border. Drawn into the raster. **Auto-hidden when panel height < 300 px or width < 500 px.** Knob `ShowLegend`, default on.

| y offset | content |
|---|---|
| +6 | **Ramp strip**, 120 × 8 px at x+8, painted by sampling the **live LUT** at 120 points — it can never drift from the render. At x+134: `LOG`. |
| +18 | Under the strip, `#97A1AC`: left `8` (= s0), centre `44` (= geometric mid), right `320` (= sCap). The three labels are equally spaced in pixels and unequally spaced in lots — that is the log law made visible without a sentence about it. |
| +28 | `X2 = 42 IDX` |
| +40 | **Form key:** a 14 × 9 solid band labelled `NOW`; a 14 × 9 hollow pair of rules labelled `MEM`; the same at 2-on/6-off labelled `FAINT`. |
| +54 | **Glyph key:** ● ▲ ✕ ◇ at real 9 px with real colours, captioned `BOUGHT · HELD · PULLED · N-C`. |
| +66 | **Mini-ledger:** a real 44 × 6 three-segment bar, captioned `LEFT · BOUGHT · CANCEL`. |

---

## 6. TWO ASCII MOCKUPS

### Key (both)

| char | means | rendered as |
|---|---|---|
| ` ` | no data | the chart's own background |
| `.` `:` `+` `*` `#` | heat, LUT idx ≈ 40 / 100 / 160 / 210 / 255 | `#1B4483` → `#FFF49D` |
| `-` | wall groove | 1 px `#0E1013` above and below a confirmed band |
| `'` | remembered rule; run length = dash duty = confidence | 1 px, `lut[idxLastObserved]`, undimmed |
| `^` `v` | depth-vision envelope: top of observed ask / bottom of observed bid | 1 px `#3A3F44` polyline |
| `!` | REC-start rule; nothing left of it is claimed | 1 px `#3A3F44` |
| `b` `d` `\|` | NT8 candle up body / down body / wick — **SizeMap is behind them, untouched** | NT8's own colours |
| `O` `A` `X` `?` | ● consumed · ▲ absorbed · ✕ pulled · ◇ unclassified | 9×9 stencil, rose / rose / grey / grey |
| `\|LLLTTC\|` | ledger: `L` remaining (heat colour) · `T` traded (rose) · `C` cancelled (grey) | 44 × max(5, rowH) px |
| `"` | saturation mark, `size > sCap` | two 1 px `#E6EAEE` verticals |
| digits | wall peak size, left of its ledger | 5×7 bitmap font, `#E6EAEE` |

---

### Mockup A — 10 levels, 6 minutes into the session, mostly empty chart

```
SIZEMAP  NQ 09-26   DEPTH 10L  OBS 10   COL 4S  ROW 1T
LOG  S0 8  CAP 320   WALLS 1L 3R 2D   1.3MS  4.0FPS

                             !
                             !                                          ^^^^
                             !                                     ^^^^^
                             !                                ^^^^^   :.:+*
                             !                           ^^^^^  .:.  :.+**#     |LLLLLTTTTTTTTTTTC|  620
                             !                      ^^^^^  .:  :.+*#############################
                             !                 ^^^^^  .:  :.+*#-----------------------------
                             !            ^^^^^  .:  :.+**. : .:.:  . :. .:  :.  . :  .
                             !       ^^^^^  .:  :.+*. : .  b :.  b  . :.  .:  :  . :.  .
                             !  ^^^^^  .:  :.+*. :  .:  . |b  |b  .:  .  :.  . :  .:.  :
                             !'''''''''''''''''''''''''''''''''''''''''''''''''''''''  O
                             !  .:  :.+*. : .  :. |b  d  d  |d  .  :.  . :  .:  . :.  .
                             !.:  :.+*. :  .:  . |d  |   |d  .:  . :.  .:  :.  . :  .:.
                             !  :.+*. : .  :. |d      |     .:  . :  .:.  :.  . :.  .:
                             !.+*. :  .:  . |d        vvvvv   vvvvvvvv   vvvvvvvvvvvvvv
                             !*. : .  :. |d     vvvvvv     vvv        vvv
                             !. :  .:  vvvv
                             !     vvvv
                             !
                             !''  ''  ''  ''  ''  ''  ''  ''  ''  ''  ''  ''  ''    X
                             !
                             !
                             !'   '   '   '   '   '   '   '   '   '   '   '   '     ?
                             !
                             !
                             !                                  +---------------------------+
                             !                                  | .:+*#   8   44   320  LOG |
                             !                                  |        X2 = 42 IDX        |
                             !                                  | #NOW  ''MEM  ' FAINT      |
                             !                                  | O BOUGHT A HELD X PULLED  |
                             !                                  | |LLTTC| LEFT BOUGHT CANCEL|
                             !                                  +---------------------------+
```

**Why it reads as intentional and not as a broken indicator.** The live band is a narrow diagonal ribbon (≈ 8 % of panel height at 10 levels) sweeping up with price, and it is **framed on both sides by the `^`/`v` envelope** — you can see the shape of what your feed could see, moment by moment. The emptiness is *labelled*: nothing is drawn left of the `!` rule, and the region above and below the ribbon is not empty at all — it holds three hollow remembered walls at full undimmed brightness, one solid (confidence ≥ 0.875, `''''''`), one at even dash (`''  ''`), one dotted (`'   '`, about to be dropped at the 0.25 floor), each ending in the glyph that says how it died. The one live wall carries its groove (`-----`), its heat is at the top of the ramp, and its ledger in the future margin reads `LLLLL TTTTTTTTTT C` — 620 peak, ~30 % still resting, and **almost all of the disappearance is rose**: it was bought.

---

### Mockup B — 40 levels, 3 hours into a recorded session

```
SIZEMAP  ES 09-26   DEPTH 40L  OBS 40   COL 15S  ROW 1T
LOG  S0 34  CAP 480   WALLS 4L 19R 61D   2.9MS  4.0FPS

^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^
.:.:. .:  .:.: .:. :.: .:.:. .:  .:.:  .:. :.:. .:  .: .:.:. .:  .:. :.:. .:.
:.:+*###########"-----------------------------------------------------------
+*################################################################"###########   |LLLTTTTTTTTTTTTC|  1240
:.:+*###########-------------------------------------------------------------
.:  :.+*.:.: .:  :. .:.:  .:. :.: .:.:  .: .:.  :.:. .:  .:.: .:.  :. .:.: .:
:.+*. :.:  .:. :.:. .:  :.:. .:.: .:  .:.:. .:  .: .:.:  .:. :.:.  .: .:.: .:
'''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''  O
.:  :.+*.: .:.:  .: .:.:. .:  .:.: .:.  :.:. .:  .:.:. .: .:.  :.:. .:  .:.:
:.+*.:  .:.:. .:.  :.:. .:  .:.: .:.:  .: .:.:. .:  .: .:.:. .:.  :.:. .:  .:
+*##########-----------------------------------------  b  .:  .: .:.:. .:.:.
*###########################################  b   |b  |b  |b  d  .:.: .:  .:    |LLLLLLLLLTTC|  740
+*##########-------------------------  b  |b  |b  |d  |d  |d  |d  .: .:.:. .:
:.+*.:  .:.: .:.: .:  b  d  |b  |b  |d  |d  |     |     |   .:.: .:.  :.:. .:
''  ''  ''  ''  ''  ''| ''| ''  '|  ''  ''| ''  ''  ''  ''  ''  ''  ''  ''  A
.:  :.+*.:.:. .:  .|.: |b |d |d  |d :.:. .:  .:.: .:.  :.:. .:  .:.:. .: .:.
:.+*. :.:  .:. :.:.  .:.  |  |    .:  .:.:. .:  .: .:.:. .:.  :.:. .:  .:.:.
+*###########---------------------------------------------  :.:. .:  .: .:.:
*############################################"##############   .:.: .:  .:.:    |LLLLTTTTTTTTC|  980
+*###########-----------------------------------------------  .:  .: .:.:. .:
:.+*.:  .:.: .:.: .:.  :.:. .:  .:.:. .: .:.  :.:. .:  .:.:. .:.  :.:. .:  .:
'   '   '   '   '   '   '   '   '   '   '   '   '   '   '   '   '   '   '   ?
.:  :.+*.: .:.:  .: .:.:. .:  .:.: .:.  :.:. .:  .:.:. .: .:.  :.:. .:  .:.:
''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''''  X
:.+*. :.:  .:. :.:. .:  :.:. .:.: .:  .:.:. .:  .: .:.:  .:. :.:.  .: .:.: .:
.:  :.+*.:.: .:  :. .:.:  .:. :.: .:.:  .: .:.  :.:. .:  .:.: .:.  :. .:.: .:
vvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvvv
                                    +---------------------------+
                                    | .:+*#  34  128  480   LOG |
                                    |        X2 = 42 IDX        |
                                    | #NOW  ''MEM  ' FAINT      |
                                    | O BOUGHT A HELD X PULLED  |
                                    | |LLTTC| LEFT BOUGHT CANCEL|
                                    +---------------------------+
```

**Why it reads as intentional at density.** Nothing about the grammar changed — same LUT, same threshold, same marks. The envelope has widened to a corridor covering ~33 % of the panel, three grooved walls carry their ledgers in one scannable column in the margin (`1240 / 740 / 980`), two of them carry the `"` saturation mark meaning "off the top of the scale", and the rest of the field stays at C 0.061–0.093 — which is exactly why BEACON was chosen over ULTRAVIOLET's C 0.195: at 33 % coverage a saturated field would vibrate against a 4 fps aliased grid. The four remembered bands stay legible because they are **hollow**: 19 remembered objects cost 38 px of ink, not 19 filled bands. The chart is dense and still has one loudest thing on it.

---

## 7. WHAT WE ARE NOT DRAWING

| omitted | reason |
|---|---|
| Candle knockout / punch-out / cutout | `SetZOrder(-1)` puts the raster behind the bars; the workaround was for a problem that does not exist, and D3's version deleted the live book. |
| Bid/ask hue | Position encodes it inside the depth window; outside it, a 1 px side tick on the left cap encodes it for 3 int writes instead of 90° of hue budget. |
| Any second colormap or user-selectable palette | A palette dropdown is a promise to validate every entry in it. One audited ramp. |
| Blur, glow, feathered bands, gradients on chrome | The data is 1 tick × 250 ms; anything smoother is a claim about resolution the feed cannot support, and `AntialiasMode.Aliased` renders it as artefacts anyway. |
| Animation, easing, fade-over-time, glow pulse | 4 fps hard cap — a pulse is a stutter. Confidence decay is four quantized dash duties, not a fade. |
| Alpha as a data channel | Alpha carries `0xFF` and nothing else; the whole raster is composited opaque. Confidence rides dash, magnitude rides LUT index. |
| Bookmap's second hot branch | Measured **ΔL −0.13 drop** at the seam: a big orange wall renders darker than a medium white one. One ramp, monotone. |
| Stipple/checker for remembered liquidity | Optically averages **ΔL −0.145 ≈ 1.9 stops** — it corrupts the magnitude it claims to preserve. |
| Callout boxes with leader lines, side profile panels, summary panel | ~28 % of the plot rect plus a collision solver, to restate what the heat and the ledger already say. |
| Session shading, RTH/ETH brackets, value-area lines | Not liquidity, and re-tinting the raster perturbs the ramp's lightness monotonicity by up to ΔL 0.04 — half a stop. |
| Imbalance dots on a −3…+3 legend | Six categorical levels of a signed variable on a chart with one clean accent lane. Claims more separable colours than exist. |
| An in-chart DOM ladder | NT8 ships SuperDOM; duplicating it costs 20 % of plot width for data one keystroke away. |
| Per-print trade dots | ~2.5 M marks per NQ session at mean 1.09 lots. |
| Aggressive-print discs (v1) | Second data pipeline; the mnemonic is already carried by rose in the ledger and ●. v2. |
| Historical backfill, or interpolating across the gap | `OnMarketDepth` never fires on historical bars. The `REC` rule discloses it instead — faking it would make every other honesty rule worthless. |
| DirectWrite | 5×7 bitmap font deletes the lifetime/DPI/AA/`NoSnap` bug class and takes the post-blit primitive count to zero. |
| A light-theme ramp | One opaque plate instead. Ship the ramp Javier actually looks at. |
| `TimeBased` bar spacing support | `GetSlotIndexByTime` throws on it; refuse in the HUD rather than draw wrong columns. |

---

## 8. THE FIRST SCREENSHOT

**Instrument and window.** NQ 09-26, **30-second bars**, ~90 bars visible so `pitch ≈ 17 px` and `fold = 8` (`COL 2S` — real temporal resolution, not a 1-min chart pretending). Vertical range **80 ticks over 900 px, `rowH = 11 px`** — the grammar's full-detail zoom, and the number to insist on: every groove, every dash duty and every glyph is unambiguous at 11 px/tick and nothing is at 40 % of a pixel. Panel **1600 × 900** at 100 % DPI. Feed at **40 levels** if Rithmic delivers it; if not, 10 levels is *still the right shot* — see below.

**The moment.** Take it 90 minutes into a recorded session, at the frame where a **620-lot ask wall is 60 % consumed with TBF 0.87**. What must be in frame:

1. **The ledger, mid-eat.** `18 px heat-coloured · 23 px rose · 3 px grey`, with `620` printed beside it, in the empty future margin at the right. This is the shot. Everything else is context for it.
2. **The envelope, sweeping.** Both `^` and `v` polylines visible across the full width with real curvature. This is the element nobody ships and it is what makes a 10-level feed look deliberate.
3. **At least four remembered bands at different dash duties** — one solid, one 6/2, one 4/4, one 2/6 — with all four glyph types on screen: ● ▲ ✕ **and ◇**. The `◇` is non-negotiable in the marketing shot: a product that admits when it does not know outranks one that always has an answer.
4. **The HUD, unretouched:** `DEPTH 40L OBS 40 · COL 2S · ROW 1T` and `LOG S0 8 CAP 320 · WALLS 4L 17R 52D · 1.9MS 4.0FPS`.
5. **Candles, obviously on top**, at full NT8 contrast (7.43:1 / 3.91:1), with heat visibly continuing behind them.

**What has to be true for it to beat Bookmap's marketing shot.** Bookmap's shot wins on density and loses on three measurable things, and the screenshot has to make all three visible without a caption:

- **Bookmap's ramp has a lightness inversion.** Ours does not: 9 stops, +0.084 per step, 13.84:1 at the top. Composition requirement: **exactly one band on screen at LUT ≥ 248**, so the eye lands on the biggest wall first and there is no second candidate.
- **Bookmap cannot say what happened to a wall.** Ours can, in three ways at once in the same frame — the ledger's rose/grey split, the glyph on the dead wall above it, and the `WALLS 4L 17R 52D` census.
- **Bookmap's memory is a blur.** Ours is a set of hollow objects with a dash that means something. Composition requirement: the hollow bands must not touch the live ribbon — leave ≥ 40 px of clean background between them so solid-vs-hollow is unmissable at thumbnail scale.

**Failure conditions for the shot** — retake if any is true: `WALLS` shows fewer than 2 live or more than 25 remembered (`K_mult` is wrong, and the picture will show it); two or more bands are at LUT 255; the ledger's traded segment is under 8 px (nothing to see); `rowH < 6`; any glyph overlaps another; the frame-time readout is above 20 ms.

---

## 9. OPEN VISUAL QUESTIONS — only Javier can answer these, on his own monitor

1. **Stop 5, `#A79F73`.** It is a khaki and it is on screen constantly. Does it read as warm, or as dirty/washed on your panel? It is 9° inside the legal lane and cannot rotate further from green. **If it bothers you the fix is chroma, not hue:** raise it toward `#AC9F62` (C 0.061 → ~0.080). Answer after one full session, not in the first ten minutes.
2. **Hollow remembered bands at 10 levels.** With 15–24 of them on screen, does it read as a liquidity map or as scaffolding/line-soup? This is the single biggest open risk in the design and no spec can settle it. If it reads as soup, the first knob is the cap (24 → 12), the second is `ConfidenceFloor` (0.25 → 0.40), and the last resort is restricting the memory band to walls above `2 × K_mult`.
3. **The ink groove.** At your monitor's gamma, is a 1 px `#0E1013` line above and below a bright band enough to say "this is a confirmed object", or does the wall just look like a bright band? If not: double it to 2 px before adding any colour.
4. **HUD, legend and glyphs are behind the bars** — that is the price of the raster being behind them, and it is all-or-nothing. Over a real session, does a candle ever land on the HUD (top-left) or the legend (bottom-right)? If it happens often enough to annoy you, the fix is a second indicator `SizeMapHud` at default z-order, ~150 lines, half a day, purely additive.
5. **Future-margin width.** Your NT8 right margin has to be ≥ 50 px for the ledger to live there without covering history. Check it at your normal zoom; if it is tight, raise NT8's right-margin setting rather than shrinking the ledger below 44 px.
6. **Dash duty.** At your usual `rowH`, can you actually distinguish 6-on/2-off from 4-on/4-off across the width of the chart? If not, drop to three confidence steps (8/0, 4/4, 2/6) — coarser is more honest anyway, since confidence is a heuristic.
7. **The envelope.** Does the `^`/`v` polyline read as a deliberate frame, or as noise chasing price? If noise, the fallback is D1's two straight horizon rules (window *now* only) — less information, more calm.
8. **Rose vs grey in the ledger at 5 px tall.** They are the same lightness (0.701 / 0.687) and differ only in chroma (0.260 / 0.020). That is the deliberate design. Confirm it survives at 5 px on your screen — if it does not, the ledger height floor goes 5 → 7 px, not the colours.
9. **Which side of the chart do you want the ledger stack anchored on** if you ever run Chart Trader open? The future margin is shared real estate.

---

**Build order, and what is deliberately not in v1:** baseline harness + coordinate mapping + data plumbing (8–11 days, paid by any design) → this spec (+3–4 days) = **11–15 honest days**, shipping ~7 mark types and **one draw call per frame**.

*skipped in v1: aggressive-print discs (§4.2 — second data pipeline), light-theme ramp, `TimeBased` bar spacing, DirectWrite text. **add when:** the base raster is proven on real ES/NQ tape and `K_mult` sits inside the 2–6 live / 10–25 remembered band.*