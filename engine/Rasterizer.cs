using System;

namespace SizeMap.Engine
{
    // Everything the projection needs, rebuilt fresh on the UI thread every frame from
    // ChartScale/ChartControl. Nothing derived from a previous frame is retained, so scroll, zoom
    // and resize need no invalidation path.
    //
    // Y: one anchor per frame, never a per-row GetYByValue — that returns int and makes rows drift
    // 13,14,13,14 px against NT8's own gridlines, which is the exact defect that makes a heatmap
    // read as smeared slabs.
    // X: column time against NowX/PxPerBucket, never GetXByTime — that is slot-based, so every
    // timestamp inside a bar returns the same x and 250 ms resolution collapses to bar resolution.
    public readonly struct RasterView
    {
        public readonly int AnchorRow;      // absolute tick row whose cell centre sits at AnchorY
        public readonly float AnchorY;
        public readonly float PxPerTick;
        public readonly long NowTicks;      // market time at x == NowX
        public readonly float NowX;         // right edge of the newest 250 ms column
        public readonly float PxPerBucket;  // pixels per 250 ms of market time

        public RasterView(int anchorRow, float anchorY, float pxPerTick, long nowTicks, float nowX, float pxPerBucket)
        {
            AnchorRow = anchorRow;
            AnchorY = anchorY;
            PxPerTick = pxPerTick > 0 ? pxPerTick : 1f;
            NowTicks = nowTicks;
            NowX = nowX;
            PxPerBucket = pxPerBucket > 0 ? pxPerBucket : 0.0001f;
        }
    }

    // ColumnRing -> BGRA int[]. PURE: no SharpDX, no NinjaTrader, no allocation, no clock. That is
    // what lets the headless harness render the exact pixels NinjaTrader will, at dotnet run speed.
    //
    // Phase 1 draws two layers: the depth-vision envelope and the heat field. Wall grooves,
    // remembered rules, glyphs and ledgers are Phase 2 and are NOT stubbed here.
    //
    // The whole of OnRender, after the mandatory preamble:
    //
    //     _palette.Rebuild(SolidBgra(cc.Properties.ChartBackground));   // no-op unless it moved
    //     _palette.SetScale(baseline.P50, baseline.Cap);                // no-op unless it moved
    //     Rasterizer.Rasterize(_ring, _px, pw, ph, BuildView(cc, cs), _palette);
    //     _bitmap.CopyFromMemory(_px, pw * 4);                          // then one DrawBitmap
    public static class Rasterizer
    {
        // px carries ramp INDICES while the frame is being built and BGRA colours after Colourize.
        // Two states in one buffer, so the max-merge below can compare magnitudes without a second
        // 1.8 MB plane. Negative values are the non-ramp layers; heat always wins over them because
        // any heat index is > EnvelopeMark, which is the layer order the spec asks for.
        const int Empty = 0;
        const int EnvelopeMark = -1;

        public static void Rasterize(ColumnRing ring, int[] px, int w, int h, in RasterView v, Palette palette)
        {
            if (ring == null || px == null || palette == null) return;
            if (w <= 0 || h <= 0 || px.Length < w * h) return;

            int n = w * h;
            for (int i = 0; i < n; i++) px[i] = Empty;

            int p = ring.PublishedIndex;
            if (p >= 0) DrawColumns(ring, px, w, h, v, palette, p);

            uint[] lut = palette.Lut;
            for (int i = 0; i < n; i++)
            {
                int t = px[i];
                px[i] = t < 0 ? unchecked((int)Palette.Envelope) : unchecked((int)lut[t]);
            }
        }

        static void DrawColumns(ColumnRing ring, int[] px, int w, int h, in RasterView v, Palette palette, int published)
        {
            long nowBucket = ColumnRing.BucketOf(v.NowTicks);
            int cap = ring.Capacity;
            float pxPerTick = v.PxPerTick;

            int prevX = int.MinValue, prevAskY = 0, prevBidY = 0;
            bool havePrev = false;

            // cap - 1, not cap. At k == cap - 1 the index wraps back onto `published + 1`, which is
            // the OPEN head column the writer is still mutating — and the ring's whole lock-free
            // argument is that a reader never touches it. On a 1-minute chart PxPerBucket is ~0.025,
            // so 7200 columns span 180 px of a 1738 px panel and the `xr <= 0` break never fires:
            // the walk reaches the head every single frame. The damage today is one partial bucket,
            // but the proof is what matters — any future writer change turns it into a real tear.
            for (int k = 0; k < cap - 1; k++)
            {
                int ci = published - k;
                if (ci < 0) ci += cap;

                ColumnHeader hd = ring.Header(ci);
                if (hd.StartTicks == 0) break;               // never written: start of the session

                long back = nowBucket - ColumnRing.BucketOf(hd.StartTicks);
                if (back < 0) continue;                      // newer than this frame's "now"

                float xr = v.NowX - back * v.PxPerBucket;
                if (xr <= 0) break;                          // off the left edge; older is further left
                float xl = xr - v.PxPerBucket;

                int x0 = (int)Math.Floor(xl);
                int x1 = (int)Math.Ceiling(xr);
                if (x1 <= x0) x1 = x0 + 1;                   // several columns per pixel: they collide
                if (x0 < 0) x0 = 0;
                if (x1 > w) x1 = w;
                if (x1 <= x0) continue;

                int askTop = int.MinValue, bidBot = int.MaxValue;
                int count = hd.Count;
                for (int i = 0; i < count; i++)
                {
                    DepthCell c = ring.CellAt(ci, i);
                    if (c.Ask > 0 && c.Row > askTop) askTop = c.Row;
                    if (c.Bid > 0 && c.Row < bidBot) bidBot = c.Row;

                    // Heat is resting size, not side — side is never encoded as hue (spec §7), so a
                    // row that is both bid and ask inside one bucket shows the larger of the two.
                    int size = c.Bid > c.Ask ? c.Bid : c.Ask;
                    int idx = palette.IdxOf(size);
                    if (idx == 0) continue;

                    int y0, y1;
                    if (!RowSpan(c.Row, v, pxPerTick, h, out y0, out y1)) continue;

                    for (int y = y0; y < y1; y++)
                    {
                        int o = y * w + x0;
                        for (int x = x0; x < x1; x++, o++)
                            if (idx > px[o]) px[o] = idx;    // fold merges by MAX, never mean: a mean
                                                             // erases the transient walls this exists to find
                    }
                }

                // The envelope traces how far the feed could see, one tick OUTSIDE the outermost
                // observed level on each side, so it frames the ribbon instead of being buried under
                // it. Drawn only onto background, which keeps the heat field on top per spec §3.
                int askY = EdgeY(askTop == int.MinValue ? int.MinValue : askTop + 1, v, pxPerTick, h);
                int bidY = EdgeY(bidBot == int.MaxValue ? int.MinValue : bidBot - 1, v, pxPerTick, h);
                DrawEnvelope(px, w, h, x0, x1, askY, bidY, prevX, prevAskY, prevBidY, havePrev);

                if (askY >= 0 || bidY >= 0)
                {
                    prevX = x0; prevAskY = askY; prevBidY = bidY; havePrev = true;
                }
            }
        }

        // A tick occupies [y0, y1). Zoomed out (pxPerTick < 1) several ticks fold onto one row and
        // resolve by max; zoomed in the whole span gets the identical value, which is not smoothing
        // — the size genuinely is constant across the tick.
        static bool RowSpan(int row, in RasterView v, float pxPerTick, int h, out int y0, out int y1)
        {
            double top = v.AnchorY - (row - v.AnchorRow + 0.5) * pxPerTick;
            y0 = (int)Math.Floor(top);
            y1 = (int)Math.Floor(top + pxPerTick);
            if (y1 <= y0) y1 = y0 + 1;
            if (y0 < 0) y0 = 0;
            if (y1 > h) y1 = h;
            return y1 > y0;
        }

        static int EdgeY(int row, in RasterView v, float pxPerTick, int h)
        {
            if (row == int.MinValue) return -1;
            int y = (int)Math.Floor(v.AnchorY - (row - v.AnchorRow + 0.5) * pxPerTick);
            return y < 0 || y >= h ? -1 : y;
        }

        static void DrawEnvelope(int[] px, int w, int h, int x0, int x1, int askY, int bidY,
                                 int prevX, int prevAskY, int prevBidY, bool havePrev)
        {
            if (askY >= 0)
            {
                for (int x = x0; x < x1; x++) Mark(px, w, x, askY);
                if (havePrev && prevAskY >= 0) Connect(px, w, x1 - 1 < prevX ? x1 - 1 : prevX, askY, prevAskY);
            }
            if (bidY >= 0)
            {
                for (int x = x0; x < x1; x++) Mark(px, w, x, bidY);
                if (havePrev && prevBidY >= 0) Connect(px, w, x1 - 1 < prevX ? x1 - 1 : prevX, bidY, prevBidY);
            }
        }

        // The vertical riser between two adjacent columns' envelope samples: what turns a row of
        // dots into a polyline.
        static void Connect(int[] px, int w, int x, int ya, int yb)
        {
            if (x < 0 || x >= w) return;
            int lo = ya < yb ? ya : yb, hi = ya < yb ? yb : ya;
            for (int y = lo; y <= hi; y++) Mark(px, w, x, y);
        }

        static void Mark(int[] px, int w, int x, int y)
        {
            int o = y * w + x;
            if (o < 0 || o >= px.Length) return;
            if (px[o] == Empty) px[o] = EnvelopeMark;
        }
    }
}
