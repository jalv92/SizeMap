## 1. Z-order — how to get a raster BEHIND the candles

**It is possible, and it is one line.** NinjaTrader's own `@SampleCustomRender.cs` does exactly this.

```csharp
protected override void OnStateChange()
{
    if (State == State.SetDefaults)
    {
        IsOverlay                = true;   // live on the price panel, share the bars' ChartScale
        IsChartOnly              = true;
        DrawOnPricePanel         = false;  // we use zero Draw.* objects — see caveat b) below
        IsSuspendedWhileInactive = true;
        ScaleJustification       = ScaleJustification.Right; // MUST match the bars' scale
        Calculate                = Calculate.OnEachTick;
    }
    else if (State == State.Historical)
        SetZOrder(-1);                     // BELOW ChartBars. Only in State.Historical.
}
```

**Semantics (the docs contradict themselves here — this is the resolved truth):**
- `chart_zorder.md` says *"Objects with a higher ZOrder are drawn first"*. That sentence is wrong. Notes 3 and 4 on the same page (`-1` = behind bars, `int.MaxValue` = topmost) and NT's own sample are authoritative: **higher ZOrder = painted later = in front.**
- Default bands, verified by decompiling `NinjaTrader.Gui.Chart.ChartPanel` (internal consts): `zOrderRange = 10000`, `zOrderMaxBars = 10000`, `zOrderMaxNinjaScript = 20000`, `zOrderMaxGlobalDrawingObjects = 30000`, `zOrderMax = 40000`. Documented starting indices: ChartBars `1`, NinjaScript `10001`, global draw objects `20001`, draw objects `30001`. Values above the current max clamp to the max.
- So an overlay indicator at default z-order has its `OnRender` invoked **after** `ChartBars.OnRender` — that is why every home-made indicator sits on top of the candles. `SetZOrder(-1)` moves the whole object into the band below ChartBars, so its `OnRender` runs **before** the bars are painted.

**All-or-nothing — this is a hard limit.** From `using_sharpdx_for_custom_chart_rendering.md`: *"it is not possible to sequence your chart object's RenderTarget to draw on two different ZOrders (e.g., one line above chart bars and another line below)."* RenderTarget command order only sequences you against yourself. If SizeMap wants raster-behind **and** glyphs/HUD-in-front, that is **two indicators**: `SizeMapRaster` (`SetZOrder(-1)`) and `SizeMapOverlay` (default z-order), sharing state through a `static Dictionary<(Instrument, ChartControl), SizeMapState>` registry.

**Known bugs to design around (NT forum, corroborated):**
- a) `SetZOrder(-1)` on an indicator in a **secondary panel** throws `Failed to call OnRender for '<x>': Sequence contains no elements`. SizeMap is price-panel-only — never support panel ≥ 1.
- b) `SetZOrder(-N)` **combined with `DrawOnPricePanel = true`** has been reported not to take effect; the community workaround is assigning `this.ZOrder = -1;` directly. Since SizeMap draws only through `RenderTarget` and uses no `Draw.*` objects, set `DrawOnPricePanel = false` and the conflict does not arise.
- c) On **first apply** the indicator can render on top until the chart is reloaded. Mitigate by calling `SetZOrder(-1)` again (idempotent) on the first `OnRender` after `State.Realtime`.

**Also mandatory:**
```csharp
protected override void OnRender(ChartControl cc, ChartScale cs)
{
    if (IsInHitTest) return;                        // full-panel raster => EVERY click "hits" SizeMap;
                                                    // without this, double-click opens our properties dialog.
    if (RenderTarget == null || RenderTarget.IsDisposed) return;
    // do NOT call base.OnRender() — SizeMap has no Plots, it just costs a pass
}
```

**Fallback if z-order ever fails** (do not ship this — it tints the candles and destroys the ramp's zero point): stay on top and blit translucent. Numbers that actually work against NT default candles on a `#141414`-class background: hottest cells `α = 0x99` (60 %), mid `α = 0x66` (40 %). Below `0x4D` (30 %) the heatmap disappears; above `0xB3` (70 %) 1-px candle wicks vanish.

---

## 2. Coordinate mapping

### Units — the trap
Verified by reflection against the shipping `NinjaTrader.Gui.dll` (8.1.8.2):

| Member | Type | Space |
|---|---|---|
| `ChartPanel.X / Y / W / H` | `Int32` | **device pixels** ✅ |
| `ChartControl.CanvasLeft / CanvasRight` | `Int32` | **device pixels** (help guide says `double` — wrong) |
| `ChartControl.GetXByBarIndex(ChartBars, int)` | `Int32` | **device pixels**, **bar CENTER** |
| `ChartControl.GetXByTime(DateTime)` | `Int32` | device pixels, **slot-based** |
| `ChartControl.GetBarPaintWidth(ChartBars)` | `Int32` | device pixels |
| `ChartScale.GetYByValue(double)` | `Int32` | **device pixels** |
| `ChartScale.GetPixelsForDistance(double)` | `Single` | **device pixels**, fractional ✅ |
| `ChartScale.GetYByValueWpf / GetValueByYWpf` | `Double` | **WPF/DIP** |
| `ChartScale.Height / Width` | `Double` | **WPF/DIP** — decompiled: `Height => ChartPanel.ActualHeight`. The help guide claims "device pixels". **It is lying.** Never use these for the blit. |
| `ChartControl.AxisXHeight / AxisYLeftWidth / AxisYRightWidth` | `Double` | WPF/DIP |
| `ChartControl.BarWidth` | `Double` | **unitless style value, NOT pixels** (docs are explicit) |
| `ChartControl.BarMarginLeft` | — | hard-coded **8 px** |
| `ChartControl.Properties.BarDistance` | `Single` | center-to-center |
| `ChartControl.MouseDownPoint` | `Point` | **WPF/DIP** |

NT's own sample states it: *"Always use ChartPanel X, Y, W, H — as chartScale and chartControl properties WPF units, so they can be drastically different depending on DPI set."*

### Rows: (tick) → y
Do **not** call `GetYByValue()` per row. It returns `int`, so consecutive tick rows come out 13, 14, 13, 14 px and the banding drifts against NT's gridlines. One anchor per frame, float arithmetic:

```csharp
double tick     = Instrument.MasterInstrument.TickSize;              // 0.25 for ES/NQ
float  pxTick   = chartScale.GetPixelsForDistance(tick);             // float, device px per tick
double anchorPx = Math.Round(chartScale.MinValue / tick) * tick;     // a REAL tick, recompute each frame
int    yAnchor  = chartScale.GetYByValue(anchorPx);                  // one int call per frame

// row index 0 = the anchor tick; increases upward in price, downward in y
float RowTopF(int tickIdx) => yAnchor - (tickIdx + 0.5f) * pxTick;
int   RowTop (int tickIdx) => (int)Math.Floor(RowTopF(tickIdx) + 0.5f);
int   RowH   (int tickIdx) => RowTop(tickIdx - 1) - RowTop(tickIdx);   // consistent, sums exactly
```
Guards:
- `if (chartScale.Properties.YAxisScalingType == YAxisScalingType.Logarithmic)` → the linear shortcut is invalid; fall back to per-row `GetYByValue`.
- `if (pxTick < 1f)` → more than one tick per pixel. Aggregate `ceil(1/pxTick)` ticks per row and **say so in the HUD**. Never draw sub-pixel rows.
- Visible rows: `nRows = (int)(panelH / Math.Max(1f, pxTick)) + 2`.

Verification test: turn horizontal gridlines on. Every gridline must land exactly on a row boundary. If it doesn't, the formula is wrong.

### Columns: (250 ms bucket) → x
```csharp
int i0 = ChartBars.FromIndex, i1 = ChartBars.ToIndex;
int x0 = chartControl.GetXByBarIndex(ChartBars, i0);
int x1 = chartControl.GetXByBarIndex(ChartBars, i1);
float pitch = (i1 > i0) ? (x1 - x0) / (float)(i1 - i0)
                        : chartControl.GetBarPaintWidth(ChartBars);   // device px, center-to-center
```
`GetXByBarIndex` returns the **center** — column left edge = `center - pitch/2`.

Intrabar placement (time-based bars only):
```csharp
DateTime tClose = ChartBars.Bars.GetTime(i);          // NT8 GetTime() = bar CLOSE time
TimeSpan span   = barDuration;                        // e.g. 60 s
double   f      = (t - (tClose - span)).TotalMilliseconds / span.TotalMilliseconds;  // 0..1
float    x      = chartControl.GetXByBarIndex(ChartBars, i) - pitch * 0.5f + pitch * (float)f;
```

**The exact caveat you asked about:** `GetXByTime()` resolves `time → slot index → x`. Every timestamp inside bar *i* returns **the same x**. There is no intrabar interpolation available from the API — you must synthesise it from `GetXByBarIndex` + a known bar duration. Consequences:
- On **tick / volume / range / Renko** bars there is no intrabar time model at all. The only honest column is *one bar = one column*; SizeMap must aggregate its 250 ms buckets into that bar's column and label it as such.
- `chartControl.BarSpacingType` ∈ {`EquidistantFirstSeries`, `EquidistantMulti`, `EquidistantSingle`, `TimeBased`}. On the three Equidistant modes the formula above holds. On `TimeBased`, x is linear in wall-clock time and `GetSlotIndexByTime()` **throws** — use `x = CanvasLeft + (t − FirstTimePainted)/TimePainted × (CanvasRight − CanvasLeft)`. `GetSlotIndexByX()` returns `-1` on TimeBased. Handle both or refuse TimeBased in v1.

**The resolution lie you must not tell.** On a 1-minute chart with `pitch = 6 px`, a 250 ms bucket is `6/240 = 0.025 px`. 239 of 240 buckets are invisible and one wins by rounding. Rule:
```csharp
int   bucketsPerBar = (int)(span.TotalMilliseconds / 250.0);
float pxPerBucket   = pitch / bucketsPerBar;
int   fold          = Math.Max(1, (int)Math.Ceiling(1.0 / pxPerBucket)); // buckets merged per column
int   columnMs      = 250 * fold;                                        // PRINT THIS IN THE HUD
```

### Scroll / zoom / resize mid-session
- Cache **nothing** in pixel space. The store is `(bucketIndex, tickIndex) → value`; pixels are recomputed from `ChartBars.FromIndex/ToIndex` every pass. At 4 fps that is free.
- Scroll changes `FromIndex/ToIndex`; zoom changes `pitch` and `pxTick`; a resize **destroys and recreates the RenderTarget** → `OnRenderTargetChanged` → drop the Bitmap.
- `ChartPanel.W/H` can change **between two reads inside one frame** during a live drag-resize. Snapshot into locals at the top of `OnRender` and use only the locals:
```csharp
int px = ChartPanel.X, py = ChartPanel.Y, pw = ChartPanel.W, ph = ChartPanel.H;
if (pw <= 0 || ph <= 0) return;
```

---

## 3. DPI

The chart target is a `SharpDX.Direct2D1.WindowRenderTarget` (HWND-based; the second is a `WicRenderTarget` for hit-test / taskbar thumbnail / resize snapshot). NT creates it such that **1 DIP == 1 device pixel** — this is why `ChartPanel.X/Y/W/H` (device px) can be passed straight to `RenderTarget`. Assert it rather than trust it:

```csharp
// once, on the first render pass
System.Diagnostics.Debug.Assert(Math.Abs(RenderTarget.DotsPerInch.Width  - 96f) < 0.5f);
System.Diagnostics.Debug.Assert(Math.Abs(RenderTarget.DotsPerInch.Height - 96f) < 0.5f);
System.Diagnostics.Debug.Assert(RenderTarget.PixelSize.Width  == (int)Math.Round(RenderTarget.Size.Width));
System.Diagnostics.Debug.Assert(RenderTarget.PixelSize.Height == (int)Math.Round(RenderTarget.Size.Height));
```
`RenderTarget.PixelSize` is `Size2` (device px), `RenderTarget.Size` is `Size2F` (DIPs). They are equal iff DPI == 96.

**Correct blit on 4K @ 150 %** — nothing DPI-specific is needed, because the bitmap is authored at exactly the panel's device-pixel size and the destination rect is in device pixels:
```csharp
// raster is EXACTLY pw x ph device pixels => 1:1, NearestNeighbor is a no-op resample
RenderTarget.DrawBitmap(raster,
    new SharpDX.RectangleF(px, py, pw, ph), 1f,
    SharpDX.Direct2D1.BitmapInterpolationMode.NearestNeighbor);
```
The bitmap's own DPI is irrelevant **as long as you always pass an explicit destination rectangle** — which you must (the `DrawBitmap(bitmap, opacity, mode)` overload uses the bitmap's DIP size and WILL be wrong).

**Anything you author in logical units must be scaled.** Two sanctioned APIs, both verified present:
```csharp
// NinjaTrader.Gui.Chart.ChartingExtensions
int  ConvertToHorizontalPixels(double x, ChartControl cc);      // also (double, PresentationSource)
int  ConvertToVerticalPixels  (double y, ChartControl cc);
double ConvertFromHorizontalPixels(int x, ChartControl cc);
double ConvertFromVerticalPixels  (int y, ChartControl cc);

int pad8 = ChartingExtensions.ConvertToVerticalPixels(8, chartControl);   // 8 -> 12 at 150%
```
Raw factors (WPF `TransformToDevice`): `chartControl.M11ToDevice`, `M22ToDevice` (and `M11FromDevice`, `M22FromDevice`) — `double`, e.g. `1.5` at 150 %.

Fonts: build from `chartControl.Properties.LabelFont` (a `NinjaTrader.Gui.Tools.SimpleFont`) and call `.ToDirectWriteTextFormat()` — DPI handling stays NT's problem. If you need a second size, clone a `new Gui.Tools.SimpleFont("Segoe UI", 11)` and convert it, rather than hand-scaling a `TextFormat.FontSize`.

---

## 4. MaximumBitmapSize

`RenderTarget.MaximumBitmapSize` → `Int32` (present in the shipping SharpDX). NT8 ships `SharpDX.Direct3D10.dll`, so expect **8192** (D3D10 feature level 10_0); 16384 on FL11. Read it at runtime, don't hard-code.

Panel size is never the problem — 4K @ 150 % is 3840 device px. The two lines of insurance:
```csharp
int maxDim = RenderTarget.MaximumBitmapSize;
if (pw > maxDim || ph > maxDim) { /* tile: ceil(pw/maxDim) side-by-side blits */ }
```

**The real trap is history.** 6.5 h at 250 ms = **93,600 columns** — 11× over the cap. Never hold session history in a bitmap. History lives in a CPU-side ring buffer of columns (`ushort[nTicks]` or a packed `byte[]` per column); only the **visible window** is rasterized each frame. Budget: 93,600 columns × 200 tick rows × 2 bytes ≈ 37 MB per side — trim to the ± N ticks you actually paint (± 100 ticks → 37 MB total, acceptable; ± 400 ticks → 150 MB, not).

---

## 5. Text with DirectWrite

**Lifetime (from NT's own device-dependency tables):**
- **Device-INDEPENDENT** — create once in `State.Configure`, dispose in `State.Terminated`, survives every `OnRenderTargetChanged`: `TextFormat`, `TextLayout`, `StrokeStyle`, `PathGeometry`.
- Both need `NinjaTrader.Core.Globals.DirectWriteFactory` (verified present). `StrokeStyle`/`PathGeometry` need `NinjaTrader.Core.Globals.D2DFactory`.

**Pattern for ~30 labels/frame with zero per-frame allocation:**
```csharp
private SharpDX.DirectWrite.TextFormat fmtLabel;                       // created ONCE
private readonly Dictionary<string, SharpDX.DirectWrite.TextLayout> layoutCache
        = new Dictionary<string, SharpDX.DirectWrite.TextLayout>(512);

// State.Configure  (or first OnRender — it is device-independent, so either is safe)
fmtLabel = (chartControl.Properties.LabelFont ?? new Gui.Tools.SimpleFont("Segoe UI", 11))
           .ToDirectWriteTextFormat();

SharpDX.DirectWrite.TextLayout Layout(string s)
{
    SharpDX.DirectWrite.TextLayout l;
    if (layoutCache.TryGetValue(s, out l)) return l;
    if (layoutCache.Count >= 512) { foreach (var kv in layoutCache) kv.Value.Dispose(); layoutCache.Clear(); }
    l = new SharpDX.DirectWrite.TextLayout(Core.Globals.DirectWriteFactory, s, fmtLabel,
                                           240f, fmtLabel.FontSize * 1.6f);   // maxW, maxH
    layoutCache[s] = l;
    return l;
}

// in OnRender
RenderTarget.DrawTextLayout(new SharpDX.Vector2((float)Math.Round(x), (float)Math.Round(y)),
                            Layout(txt), textBrushDx,
                            SharpDX.Direct2D1.DrawTextOptions.None);   // None, NOT NoSnap
```
Labels are quantized numbers from a small vocabulary (`"400"`, `"1.2k"`, `"−62 %"`), so the cache hit rate is ~100 % after 30 s.

**Cost.** `DrawTextLayout` ≈ one glyph run per call; 30 labels ≈ 30 glyph runs, well under 1 ms of the ~60 ms budget. Layout **construction** is the expensive part (tens of µs each) and it allocates unmanaged memory — that is what the cache kills. Per NT: *"When drawing the same text repeatedly, using DrawTextLayout() is more efficient than DrawText() because the text doesn't need to be formatted and the layout processed with each call."*

**Four crispness rules:**
1. `DrawTextOptions.None` — **not** `NoSnap`. NT's own sample uses `NoSnap`, which disables baseline pixel-snapping; that is precisely why NT's own chart labels look soft. At 9–11 px, snapping is the difference between legible and mush.
2. `RenderTarget.TextAntialiasMode = TextAntialiasMode.Grayscale` before your labels, restore afterwards. ClearType subpixel rendering on a near-black chart produces coloured fringes that read as chromatic aberration. (Enum verified: `Default=0, Cleartype=1, Grayscale=2, Aliased=3`.)
3. Round the origin to integers. A fractional origin defeats snapping.
4. `TextLayout` maxWidth/maxHeight must be **> 0**; passing `0` yields an empty layout that silently draws nothing. NT's sample passes `textFormat.FontSize` as maxHeight, which clips descenders — use `FontSize * 1.6f`.

**Never call `ToDirectWriteTextFormat()` inside `OnRender`** — it allocates an unmanaged `TextFormat` every pass. At 4 fps × 6.5 h that is 93,600 leaks if you miss a `Dispose`.

---

## 6. Primitives, relative cost, and HATCHING

Ranked per call on a ~60 ms budget (`AntialiasMode.Aliased` unless stated):

| # | Primitive | Relative cost | Ceiling / frame |
|---|---|---|---|
| 1 | `FillRectangle` (axis-aligned) | 1× — batches into one geometry pass | many hundreds |
| 2 | `DrawLine` 1 px, no StrokeStyle | ~1× | hundreds |
| 3 | `DrawRectangle` | ~2× (4 edges) | hundreds |
| 4 | `FillEllipse` / `DrawEllipse` | ~5–10× (tessellated; needs `PerPrimitive` AA or it looks awful) | **< 60** |
| 5 | `DrawLine` **with a dashed StrokeStyle** | 10–50× — dashing splits the segment on the CPU, re-tessellated every frame | **< 20**, and keep them short |
| 6 | `DrawGeometry` / `FillGeometry` | draw is cheap; **building the sink** is the cost | build once, reuse (device-independent) |
| 7 | `DrawTextLayout` | ~2–4× a rect | ~30 |
| 8 | `PushAxisAlignedClip` / `PopAxisAlignedClip` (`Aliased`) | ≈ free | use it; **must be balanced** |
| 9 | `PushLayer` / `PopLayer` + `Layer` | 100×+ — offscreen surface + composite | **never** |

`RenderTarget.SaveDrawingState(DrawingStateBlock)` / `RestoreDrawingState` exist (`DrawingStateBlock(Factory)`, device-independent) if you want an atomic state snapshot instead of hand-rolled save/restore.

### Hatching — there is NO hatch brush in Direct2D

`HatchBrush` is GDI+. Direct2D has `SolidColorBrush`, `LinearGradientBrush`, `RadialGradientBrush`, `BitmapBrush`. Three real options:

**(a) Bake it into the raster — this is the answer for the heatmap body.** SizeMap already owns an `int[]`. A 45° hatch for remembered / cancel-backed cells is:
```csharp
// 8 px period, 2 px stroke, 45°  -> zero draw calls, zero resources, automatically pixel-aligned
if (((x + y) & 7) < 2) px = hatchColor; else px = baseColor;
```
Zero cost, zero lifetime management, and it can never drift relative to the cells because it is computed in the same pixel space.

**(b) `BitmapBrush` tile — for post-blit shapes** (callout boxes, legend swatches). Verified present in the shipping SharpDX 2.6.3:
```csharp
var tp = new SharpDX.Direct2D1.BitmapProperties(RenderTarget.PixelFormat);
tp.PixelFormat.AlphaMode = SharpDX.Direct2D1.AlphaMode.Premultiplied;     // legal: struct field of a local
tile = new SharpDX.Direct2D1.Bitmap(RenderTarget, new SharpDX.Size2(8, 8), tp);
tile.CopyFromMemory(tilePixels, 8 * 4);                                   // int[64], pitch = w*4

hatch = new SharpDX.Direct2D1.BitmapBrush(RenderTarget, tile,
    new SharpDX.Direct2D1.BitmapBrushProperties {
        ExtendModeX       = SharpDX.Direct2D1.ExtendMode.Wrap,
        ExtendModeY       = SharpDX.Direct2D1.ExtendMode.Wrap,
        InterpolationMode = SharpDX.Direct2D1.BitmapInterpolationMode.NearestNeighbor },
    new SharpDX.Direct2D1.BrushProperties { Opacity = 1f });

hatch.Transform = SharpDX.Matrix3x2.Translation(px % 8, py % 8);  // anchor to the PANEL, not the RT origin
RenderTarget.FillRectangle(rect, hatch);
```
Gotchas: `Bitmap` and `BitmapBrush` are **device-dependent** (rebuild in `OnRenderTargetChanged`). Without `ExtendMode.Wrap` you get one tile plus a clamp-smear. Without `Transform` the tile is anchored to the render-target origin, so the hatch **swims** under the shape whenever the panel moves. With `Linear` interpolation a 1-px diagonal becomes grey mush.

**(c) Manual `DrawLine` per hatch stroke** — a 200 px band at 8 px pitch is 25+ lines; × 30 bands = 750 dashed-adjacent lines per frame. Don't.

### The reference gotcha that decides (b) is even possible
`NinjaTrader.Custom.dll` references **only `SharpDX` and `SharpDX.Direct2D1`** — verified by dumping its assembly reference table. **`SharpDX.DXGI` is NOT referenced.** Therefore `SharpDX.DXGI.Format.B8G8R8A8_UNorm` **cannot be named in NinjaScript** without adding `C:\Program Files\NinjaTrader 8\bin\SharpDX.DXGI.dll` to `Documents\NinjaTrader 8\bin\Custom\AdditionalReferences.txt` — a per-machine step every end user would have to perform. **Do not require it.** The workaround above is exact and reference-free: `BitmapProperties.PixelFormat` is a public **field** of type `Direct2D1.PixelFormat`, whose `AlphaMode` field is a `Direct2D1` type. So
```csharp
var props = new SharpDX.Direct2D1.BitmapProperties(RenderTarget.PixelFormat);
props.PixelFormat.AlphaMode = SharpDX.Direct2D1.AlphaMode.Premultiplied;
```
compiles clean, and inherits the render target's own DXGI format (`B8G8R8A8_UNorm`) without ever naming it.

### Pixel packing — the answer to "hex AND 0xAARRGGBB/BGRA"
`B8G8R8A8_UNorm` stores bytes B,G,R,A. On x64 (little-endian) a 32-bit int written as `0xAARRGGBB` lands in memory as `BB,GG,RR,AA` — **exactly** that layout. So **`0xAARRGGBB` and BGRA are the same 32-bit value here.** Write `int px = (a<<24)|(r<<16)|(g<<8)|b;` into your `int[]` and the bitmap reads it correctly. No byte swapping anywhere.

### The whole raster path, per frame
```csharp
// once per (pw, ph, RenderTarget) triple:
raster = new SharpDX.Direct2D1.Bitmap(RenderTarget, new SharpDX.Size2(pw, ph), props);
// every frame:
raster.CopyFromMemory(pixels, pw * 4);                              // int[pw*ph], generic overload
RenderTarget.DrawBitmap(raster, new SharpDX.RectangleF(px, py, pw, ph), 1f,
                        SharpDX.Direct2D1.BitmapInterpolationMode.NearestNeighbor);
```
One memcpy + one draw call. A 1600×900 panel is 1.44 M pixels; a straight C# fill loop runs 2–4 ms. Fits.

---

## 7. Resource lifetime, the identity guard, and crash signatures

**Device-DEPENDENT** (die with the RenderTarget): `Brush`, `SolidColorBrush`, `LinearGradientBrush`, `RadialGradientBrush`, `GradientStopCollection`, **`Bitmap`**, **`BitmapBrush`**, **`Layer`**.
> NT's published list omits `Bitmap`/`BitmapBrush`/`Layer`. They are device-dependent all the same — every one derives from `Direct2D1.Resource` and is constructed from a `RenderTarget`. Treating them as durable is the #1 way to crash this indicator.

**Device-INDEPENDENT** (create once, dispose in `State.Terminated`): `TextFormat`, `TextLayout`, `StrokeStyle`, `PathGeometry`, `DrawingStateBlock`.

**There are TWO render targets.** `WindowRenderTarget` paints the chart; `WicRenderTarget` is used for (1) hit testing when the user clicks, (2) the Windows taskbar thumbnail, (3) the snapshot during a resize. Your `OnRender` is called against **both**. Hence:

```csharp
private SharpDX.Direct2D1.RenderTarget rtSeen;

public override void OnRenderTargetChanged()
{
    ReleaseDeviceResources();          // RenderTarget can be NULL here (destroy notification)
    rtSeen = null;
}

protected override void OnRender(ChartControl cc, ChartScale cs)
{
    if (IsInHitTest) return;
    if (RenderTarget == null || RenderTarget.IsDisposed) return;

    if (!ReferenceEquals(RenderTarget, rtSeen))    // <- the identity guard: one ref compare per frame
    {
        ReleaseDeviceResources();
        CreateDeviceResources();                    // brushes + raster Bitmap, from THIS RenderTarget
        rtSeen = RenderTarget;
    }
    ...
}
```
Both are required: `OnRenderTargetChanged` fires on create **and** destroy, and on destroy `RenderTarget` is already null, so creation cannot live there unconditionally. The identity compare is the belt.

**Exact crash signatures:**

| Signature | Cause | Fix |
|---|---|---|
| `HRESULT: [0x88990015], Module: [SharpDX.Direct2D1], ApiCode: [D2DERR_WRONG_RESOURCE_DOMAIN/WrongResourceDomain], Message: The resource was realized on the wrong render target.` | A brush/bitmap created on target A used on target B — i.e. you cached a device resource across a resize, a click (hit-test WIC target), or a taskbar hover. | identity guard above |
| `HRESULT: [0x88990006]` — `D2DERR_WRONG_STATE` | You called `BeginDraw()`/`EndDraw()` yourself, or drew after `Dispose()`. **NT owns the BeginDraw/EndDraw pair around OnRender.** | never call them |
| `Error on calling 'OnRender' method on bar 0: Attempted to read or write protected memory. This is often an indication that other memory is corrupt.` | Use-after-`Dispose`. This is an access violation; it usually kills the chart and can kill the process. | check `.IsDisposed` before use; null the field on dispose |
| Memory climbing hundreds of MB over a session, then chart freeze | An undisposed unmanaged resource per pass (`ToDxBrush`, `TextFormat`, `TextLayout`, `PathGeometry`, `Bitmap`). At 4 fps × 6.5 h = 93,600 leaks. | `using` blocks or a single owned field |
| `Failed to call OnRender for '<x>': Sequence contains no elements` | `SetZOrder(-1)` on a **secondary panel** indicator | price panel only |

Also: **`Dispose()` in `State.Terminated` only releases the last reference.** NT's docs are explicit that this is *not* sufficient for anything created per-pass.

Also: `OnRender` runs on the **UI thread**. Never take a lock in it that the `OnMarketDepth` thread also holds — that stalls the whole platform (crosshair, scroll, Chart Trader), not just SizeMap. Use a lock-free double buffer: the depth thread builds an immutable snapshot and swaps it with `Volatile.Write`; `OnRender` does one `Volatile.Read`.

---

## 8. Antialiasing / interpolation — save & restore

`RenderTarget.AntialiasMode` (`PerPrimitive` | `Aliased`) and `RenderTarget.TextAntialiasMode` (`Default | Cleartype | Grayscale | Aliased`) are **shared with every chart object drawn after you**. So is `Transform`, `StrokeWidth`, and the clip stack.

```csharp
var oldAA   = RenderTarget.AntialiasMode;        // read it — do NOT assume the default
var oldText = RenderTarget.TextAntialiasMode;
try
{
    RenderTarget.AntialiasMode     = SharpDX.Direct2D1.AntialiasMode.Aliased;      // raster, rects, rules
    RenderTarget.TextAntialiasMode = SharpDX.Direct2D1.TextAntialiasMode.Grayscale;

    RenderTarget.PushAxisAlignedClip(new SharpDX.RectangleF(px, py, pw, ph),
                                     SharpDX.Direct2D1.AntialiasMode.Aliased);
    try
    {
        /* DrawBitmap + FillRectangle + labels */
        RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;  // ONLY for dots/curves
        /* the < 60 ellipses */
    }
    finally { RenderTarget.PopAxisAlignedClip(); }   // MUST be balanced
}
finally
{
    RenderTarget.AntialiasMode     = oldAA;
    RenderTarget.TextAntialiasMode = oldText;
}
```
- **The D2D default is `PerPrimitive`, not `Aliased`.** Restoring to a hard-coded `Aliased` is the same bug as not restoring at all.
- An unbalanced `PushAxisAlignedClip` (because an exception escaped between push and pop) clips NT's own axis and every later indicator for the rest of the frame — and it looks like *their* bug.
- Do not touch `RenderTarget.Transform`. If you must, save and restore; do not force `Identity` without restoring.
- Wrap the whole `OnRender` body: an exception escaping `OnRender` disables the indicator and shows a red log entry.
- Interpolation: `BitmapInterpolationMode.NearestNeighbor` (=0) is the only honest choice — `Linear` (=1) invents resolution between your 250 ms columns and your 1-tick rows, which violates the honesty rule. It is also faster. With a 1:1 dest rect it is a pure copy.

---

## 9. Reading the user's actual theme at runtime

All on `chartControl.Properties` (type `ChartControlProperties`, verified by reflection):

```csharp
var P = chartControl.Properties;

uint bg      = SolidBgra(P.ChartBackground,      0xFF141414);  // ramp ZERO POINT
uint gridCol = SolidBgra(P.GridLineHPen.Brush,   0xFF2A2A2A);
bool gridOn  = P.AreHGridLinesVisible && P.GridLineHPen.IsVisible;
float gridW  = P.GridLineHPen.Width;                            // Stroke.Width, float
uint textCol = SolidBgra(P.ChartText,            0xFFE6E6E6);   // TEXT TOKEN — never the data colour
uint axisCol = SolidBgra(P.AxisPen.Brush,        0xFF3A3A3A);
var  font    = P.LabelFont ?? new Gui.Tools.SimpleFont("Segoe UI", 11);

static uint SolidBgra(System.Windows.Media.Brush b, uint fallback)
{
    var s = b as System.Windows.Media.SolidColorBrush;
    if (s == null) return fallback;                 // gradient / image background -> fail honestly
    var c = s.Color;
    byte a = (byte)Math.Round(c.A * s.Opacity);     // brush-level Opacity is separate from Color.A
    return ((uint)a << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;   // == 0xAARRGGBB == BGRA
}
```
Other relevant members: `P.GridLineVPen`, `P.AreVGridLinesVisible`, `P.CrosshairStroke`, `P.SelectedMarkerBrush`, `P.InactivePriceMarkersBackground/Foreground`, `P.ChartTraderVisibility`, and — critically — **`P.LoadBackgroundImage` / `P.BackgroundImagePath`**: if a background image is set, background luminance is unknowable and you must paint your own opaque plate behind the raster.

Pick the ramp direction from measured luminance, do not assume dark:
```csharp
double L = 0.2126*Lin(r) + 0.7152*Lin(g) + 0.0722*Lin(b);   // Lin() = sRGB -> linear
bool darkTheme = L < 0.18;                                   // dark: ramp runs dark -> bright
```
NT's default "dark" background is **not** `#000000` (it sits around `#141414`–`#1C1C1C` depending on skin), and a real fraction of users run light skins. A ramp whose zero point is hard-coded black will float above a `#1C1C1C` background as a visible grey slab across the entire panel — the single most common way a heatmap indicator announces itself as amateur.

Also read `chartScale.Properties.HorizontalGridlinesInterval` / `HorizontalGridlinesIntervalType` (`Ticks|Points|Pips`) so your row-boundary emphasis can coincide with the user's gridlines instead of fighting them.

---

## 10. The eight things that will make this look amateur

**1. Premultiplied vs straight alpha.**
`AlphaMode.Premultiplied` is the only mode that blends, and it expects every channel already multiplied by α. Writing plain `0x80FF0000` gives a washed, haloed red with bright fringes at cell edges.
*Fix:* since SizeMap sits **behind** the bars, over a solid background you can read — composite the ramp against the theme background on the CPU and blit **fully opaque** (`α = 0xFF`). Kills the whole bug class and removes a blend pass. Where you genuinely need α, premultiply on write: `r = (r*a)/255`.

**2. Half-pixel stroke straddle.**
With `AntialiasMode.Aliased`, `DrawRectangle(rect, brush, 1f)` centres the 1-px stroke on the path, so it straddles two pixel columns and D2D snaps it to an arbitrary side. Outlines land 1 px off, and differently on the left vs right edge of the same box.
*Fix:* strokes at `x + 0.5f`, fills at integers. Or draw outlines as four `FillRectangle`s of exactly 1 px — cheaper and exact.

**3. Rows not aligned to the price axis.**
Per-row `GetYByValue()` returns `int`, so tick rows come out 13, 14, 13, 14 px and the banding drifts against NT's own gridlines. This is exactly what makes NT's built-in Order Flow Market Depth Map (Screenshot_165) read as smeared grey slabs of inconsistent height instead of a ladder.
*Fix:* the single-anchor float formula in §2. *Verification:* enable horizontal gridlines — every one must land on a row boundary.

**4. Columns not aligned to bar centres.**
`GetXByTime()` is slot-based, so anything intrabar built on it collapses onto the bar centre; and `GetXByBarIndex()` returns the **centre**, not the left edge, so a naive `FillRectangle(x, …, pitch, …)` is offset by half a bar across the whole chart.
*Fix:* pitch from two `GetXByBarIndex` calls; column left = `centre − pitch/2`. *Verification:* the right edge of the last column must touch the right edge of the last bar.

**5. Claiming 250 ms resolution you do not have.**
On a 1-min chart with 6 px bars, a 250 ms bucket is 0.025 px. 239 of 240 buckets are invisible and one wins by rounding.
*Fix:* fold buckets so every column is ≥ 1 device px (`fold = ceil(1/pxPerBucket)`), and print the effective column duration in the HUD ("column = 15 s"). This is the honesty rule made mechanical.

**6. Banding in the ramp.**
An 8-bit sRGB ramp across a 200-px region with a smooth magnitude field shows Mach bands at every quantization step, and the bands read as data.
*Fix:* either (a) quantize magnitude into **7–9 explicit steps** so the steps read as intentional — which is also the honest choice, because with ~10 depth levels you do not have continuous data — or (b) if continuous, add ±1 LSB ordered dither from a 4×4 Bayer matrix at write time: three lines inside the pixel loop, invisible, kills the banding.

**7. Leaving the render target dirty.**
`AntialiasMode`, `TextAntialiasMode`, `Transform`, `StrokeWidth` and the clip stack are shared with every object drawn after you. An unbalanced `PushAxisAlignedClip` clips NT's own axis and every later indicator for the rest of the frame.
*Fix:* try/finally around every state change; restore the value you **read**, never a hard-coded default; never let an exception escape `OnRender`.

**8. Rebuilding the `Bitmap` (and the brushes) every frame.**
`new Bitmap(...)` per pass is an unmanaged allocation plus a GPU upload. At 4 fps it will not stutter in steady state, but resize and zoom fire render passes in a burst and it hitches visibly — and one missed `Dispose` at 93,600 passes per session is hundreds of MB.
*Fix:* one allocation per `(width, height, RenderTarget)` triple; refresh with `CopyFromMemory(pixels, pw*4)`; release only in `OnRenderTargetChanged` or on a size change. One memcpy + one `DrawBitmap` per frame is the whole hot path.

---

### Verification provenance
Local docs mirror: `.claude/skills/nt8-common/reference/` (375 pages), `nt8-indicator/reference/`, `nt8-sharpdx/reference/` (102 pages), `nt8-educational/reference/using_sharpdx_for_custom_chart_rendering.md` and `working_with_pixel_coordinates.md`.
API surface verified by reflection (`MetadataLoadContext`) against the **installed** binaries at `C:\Program Files\NinjaTrader 8\bin\` — `NinjaTrader.Gui.dll` / `NinjaTrader.Core.dll` **8.1.8.2**, `SharpDX*.dll` **2.6.3.0** (not 4.x). Assembly-reference table of `C:\Users\javlo\Documents\NinjaTrader 8\bin\Custom\NinjaTrader.Custom.dll` confirms only `SharpDX` + `SharpDX.Direct2D1` are referenced. Z-order band constants decompiled from `NinjaTrader.Gui.Chart.ChartPanel` (ilspycmd 11.0). Live pattern reference: `C:\Users\javlo\Documents\NinjaTrader 8\bin\Custom\Indicators\@SampleCustomRender.cs`.
Reusable dumper left at `/tmp/claude-1000/-home-javlo-Code-Projects-main-project/20642f5c-a876-46bf-90e0-fc4e0235f90c/scratchpad/dxdump/` (`dotnet run -- <FullTypeName>` / `--find <kw>` / `--refs <dll>` / `--member <name>`).

Sources: [SetZOrder](https://ninjatrader.com/support/helpguides/nt8/setzorder.htm) · [Using SharpDX for Custom Chart Rendering](https://ninjatrader.com/support/helpguides/nt8/using_sharpdx_for_custom_chart_rendering.htm) · [RenderTarget](https://ninjatrader.com/support/helpguides/nt8/rendertarget.htm) · [ZOrder issue NT 8.0.1.0](https://forum.ninjatrader.com/forum/ninjatrader-8/platform-technical-support-aa/92541-zorder-issue-nt-8-0-1-0) · [Drawing certain objects behind bars and others in front](https://forum.ninjatrader.com/forum/ninjatrader-8/indicator-development/1035798-drawing-certain-objects-behind-bars-and-others-in-front) · [SharpDX BitmapBrush in OnRender](https://forum.ninjatrader.com/forum/ninjatrader-8/indicator-development/1166642-sharpdx-bitmapbrush-in-onrender) · [DX Brushes for drawing heatmap best practice](https://forum.ninjatrader.com/forum/ninjatrader-8/strategy-development/105264-dx-brushes-for-drawing-heatmap-best-practice) · [D2DERR_WRONG_RESOURCE_DOMAIN](https://forum.ninjatrader.com/forum/historical-beta-archive/version-8-beta/90647-module-sharpdx-direct2d1-d2derr_wrong_resource_domain-wrongresourcedomain) · [HRESULT 0x88990006](https://forum.ninjatrader.com/forum/ninjatrader-8/platform-technical-support-aa/99207-hresult-0x88990006-module-sharpdx-direct2d1)