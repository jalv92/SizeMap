using System;
using System.Collections.Generic;

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
        // The ring is already in tick rows, but RadarNode is in PRICE — it is an engine contract, not
        // a projection one. Price -> row is projection, so the tick lives here with the rest of it
        // rather than being guessed (every instrument SizeMap runs on today is 0.25, which is exactly
        // how a default would survive until the first one that is not).
        public readonly double TickSize;

        public RasterView(int anchorRow, float anchorY, float pxPerTick, long nowTicks, float nowX, float pxPerBucket, double tickSize)
        {
            AnchorRow = anchorRow;
            AnchorY = anchorY;
            PxPerTick = pxPerTick > 0 ? pxPerTick : 1f;
            NowTicks = nowTicks;
            NowX = nowX;
            PxPerBucket = pxPerBucket > 0 ? pxPerBucket : 0.0001f;
            TickSize = tickSize > 0 ? tickSize : 0.25;
        }
    }

    // ColumnRing -> BGRA int[]. PURE: no SharpDX, no NinjaTrader, no allocation, no clock. That is
    // what lets the headless harness render the exact pixels NinjaTrader will, at dotnet run speed.
    //
    // Layers, in the order visual-spec §3 lists them: the depth-vision envelope, the heat field,
    // then the wall bands (grooves, remembered rules, saturation marks). Outcome glyphs, the
    // consumption ledger, the HUD and the legend are NOT stubbed here — they are gated on whether
    // the classifier's labels carry information, and drawing a label nobody has validated is the
    // one failure this instrument cannot survive.
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

        // Returns the number of ring columns VISITED. Nothing on the hot path reads it — it is what
        // lets a test pin the walk's bound without timing anything, and a timing test is flaky.
        public static int Rasterize(ColumnRing ring, int[] px, int w, int h, in RasterView v, Palette palette,
                                    IReadOnlyList<RadarNode> walls = null)
        {
            if (ring == null || px == null || palette == null) return 0;
            if (w <= 0 || h <= 0 || px.Length < w * h) return 0;

            int n = w * h;
            for (int i = 0; i < n; i++) px[i] = Empty;

            int p = ring.PublishedIndex;
            int visited = p >= 0 ? DrawColumns(ring, px, w, h, v, palette, p) : 0;

            uint[] lut = palette.Lut;
            for (int i = 0; i < n; i++)
            {
                int t = px[i];
                px[i] = t < 0 ? unchecked((int)Palette.Envelope) : unchecked((int)lut[t]);
            }

            // The wall layer runs AFTER the colourize pass and writes BGRA straight in. It could have
            // been folded into the index plane with more negative sentinels, but a groove is ink and a
            // remembered rule is lut[Idx(sizeAtLastObservation)] — one is not a ramp value at all, and
            // the other must NOT max-merge against whatever heat happens to sit under it. Painting
            // last is the layer order spec §3 asks for, expressed where it cannot be got wrong.
            if (walls != null && walls.Count > 0) DrawWalls(walls, px, w, h, v, palette);
            return visited;
        }

        // How many columns back from the head can still land inside the panel. The k-th column back
        // in the ring is at least k buckets old — the writer advances the head by exactly one slot
        // per bucket and only ever forwards — so `back >= k`, and a column is off the left edge once
        // back >= NowX/PxPerBucket. Bounding k by that is therefore not a heuristic: it can only
        // drop columns the `xr <= 0` break was going to drop anyway.
        //
        // MEASURED, and say it plainly: this is a no-op today. The `xr <= 0` break below already
        // stops the walk at the same column and for the same reason, so a mutant that returns
        // cap - 1 here renders identical pixels in identical time on every case the harness can
        // build. It is kept because it STATES the bound instead of leaving it to emerge from an
        // undocumented monotonicity invariant — capacity is a memory dial by construction now,
        // not by an argument a future writer or a sparser ring could quietly invalidate.
        //
        // What frame time really tracks is columns ON the panel, which is a zoom question, not a
        // capacity one. Measured on the harness at 1600x900, 26 levels/column: 0.45 us per on-panel
        // column, so a whole RTH session zoomed out to fit (93,600 columns) is ~42 ms of a ~60 ms
        // budget and every 250 ms of it is genuinely drawn. Numbers per zoom live on
        // SizeMapHeat.MinutesOfHistory.
        static int VisibleColumnLimit(in RasterView v, int cap)
        {
            double span = Math.Ceiling(v.NowX / (double)v.PxPerBucket) + 1;   // PxPerBucket > 0 by ctor
            int max = cap - 1;                                                // never the open head
            if (span >= max) return max;
            return span <= 0 ? 0 : (int)span;
        }

        static int DrawColumns(ColumnRing ring, int[] px, int w, int h, in RasterView v, Palette palette, int published)
        {
            long nowBucket = ColumnRing.BucketOf(v.NowTicks);
            int cap = ring.Capacity;
            float pxPerTick = v.PxPerTick;

            int prevX = int.MinValue, prevAskY = 0, prevBidY = 0;
            bool havePrev = false;

            // The limit is <= cap - 1, never cap. At k == cap - 1 the index wraps back onto
            // `published + 1`, which is the OPEN head column the writer is still mutating — and the
            // ring's whole lock-free argument is that a reader never touches it. Two tests in
            // RegressionTests pin that edge; the panel bound above only ever tightens it.
            int limit = VisibleColumnLimit(v, cap);
            int visited = 0;
            for (int k = 0; k < limit; k++)
            {
                int ci = published - k;
                if (ci < 0) ci += cap;
                visited++;

                ColumnHeader hd = ring.Header(ci);
                if (hd.StartTicks == 0) break;               // never written: start of the session

                long back = nowBucket - ColumnRing.BucketOf(hd.StartTicks);
                if (back < 0)
                {
                    // Newer than this frame's "now" — so it broke the `back >= k` invariant the
                    // limit rests on. Buy the slot back rather than reason about whether a clock
                    // behind the ring is reachable.
                    if (limit < cap - 1) limit++;
                    continue;
                }

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

            return visited;
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

        // ---------------------------------------------------------------- the wall bands
        //
        // THE load-bearing rule of this layer, and the only one worth memorising: OBSERVED IS SOLID,
        // REMEMBERED IS HOLLOW. It is a change of FORM, not of alpha and not of hue. A 50% stipple was
        // measured and rejected because it optically averages dL -0.145 — nearly two ramp stops — so a
        // stippled band lies about the very magnitude it claims to be preserving. A stranger has to be
        // able to tell the two apart in a screenshot with no legend, and form is the only channel that
        // survives that test at every zoom.
        //
        // The two channels are orthogonal and both honest: a remembered rule's COLOUR is the size at
        // last observation and never decays; its DASH DUTY is how sure we still are it is there.

        const int MaxGrooved = 12;              // spec §4 caps: hard, never a fade
        const int MaxRemembered = 24;
        const double ConfidenceFloor = 0.25;    // below this the band is not drawn at all
        const int DashPeriod = 8;               // px
        const float MinPxPerTickForGroove = 3f; // below it two ink rules would erase the band they frame

        // Cached so the per-frame Sort allocates no closure. Both scores are spec §4's.
        static readonly Comparison<RadarNode> ByPeak =
            delegate (RadarNode a, RadarNode b) { return b.PeakSize.CompareTo(a.PeakSize); };
        static readonly Comparison<RadarNode> ByConfidenceTimesPeak =
            delegate (RadarNode a, RadarNode b)
            {
                return (b.Confidence * b.PeakSize).CompareTo(a.Confidence * a.PeakSize);
            };

        // ponytail: two small Lists and two sorts per frame — the only allocation left in the render
        // path. Ceiling — node count is bounded by LiquidityMemory.Evict, so this is tens of items at
        // 4 fps. Upgrade path — a fixed-size top-N insertion if a profile ever shows it.
        static void DrawWalls(IReadOnlyList<RadarNode> nodes, int[] px, int w, int h, in RasterView v, Palette palette)
        {
            List<RadarNode> grooved = new List<RadarNode>();
            List<RadarNode> remembered = new List<RadarNode>();
            for (int i = 0; i < nodes.Count; i++)
            {
                RadarNode n = nodes[i];
                // InWindow is the WHOLE distinction, and the extra `State == Wall` test that used to
                // sit inside it was a hole that swallowed 87% of tracked nodes on real ES tape.
                //
                // LiquidityMemory only ever tracks CONFIRMED walls — a plain level is never promoted
                // — so every node here was a wall at some point. WallTracker demotes State to Live
                // the instant a promoted wall's current size stops clearing K_mult x baseline, which
                // is precisely what happens WHILE IT IS BEING EATEN. So the old chain dropped the
                // groove at the exact moment the object became worth watching: the wall vanished
                // mid-life and reappeared if it refilled. Neither branch caught it, so it was not
                // drawn, not counted live, not counted remembered, not counted faint.
                //
                // The groove means "this is a tracked object", not "this currently exceeds a
                // threshold". Its size falling is already said by the heat colour underneath it.
                // These three buckets are now exhaustive — RasterizerWallBucketTests pins that.
                if (n.InWindow) grooved.Add(n);
                else if (n.Confidence >= ConfidenceFloor) remembered.Add(n);
            }
            grooved.Sort(ByPeak);
            remembered.Sort(ByConfidenceTimesPeak);

            int g = grooved.Count < MaxGrooved ? grooved.Count : MaxGrooved;
            for (int i = 0; i < g; i++) DrawConfirmed(grooved[i], px, w, h, v, palette);

            int r = remembered.Count < MaxRemembered ? remembered.Count : MaxRemembered;
            for (int i = 0; i < r; i++) DrawRemembered(remembered[i], px, w, h, v, palette);
        }

        // Left edge of the 250 ms column that owns `ticks`, laid out exactly as DrawColumns lays out
        // the ring: back*PxPerBucket from NowX, minus one bucket for the column's own width. NOT
        // clamped to the panel — the caller needs the true value as the dash's phase origin, or a
        // band scrolling off the left edge would visibly re-phase every frame.
        static int BirthX(long ticks, in RasterView v)
        {
            long back = ColumnRing.BucketOf(v.NowTicks) - ColumnRing.BucketOf(ticks);
            if (back < 0) back = 0;                       // born after this frame's now: clamp, don't wrap
            double x = v.NowX - back * (double)v.PxPerBucket - v.PxPerBucket;
            if (x < -1e6) x = -1e6;                       // a session-old wall would overflow the cast
            return (int)Math.Floor(x);
        }

        static int RowOf(double price, in RasterView v)
        {
            return (int)Math.Round(price / v.TickSize);
        }

        static void DrawConfirmed(RadarNode n, int[] px, int w, int h, in RasterView v, Palette palette)
        {
            int y0, y1;
            if (!RowSpan(RowOf(n.Price, v), v, v.PxPerTick, h, out y0, out y1)) return;

            int xb = BirthX(n.FirstSeenTicks, v);
            int xn = (int)Math.Ceiling(v.NowX);
            if (xn > w) xn = w;
            if (xn <= xb) return;

            // The fill is already there: it is the heat field, and the wall IS an observed level.
            // All this layer adds is the frame that says "this one is confirmed".
            if (v.PxPerTick >= MinPxPerTickForGroove)
            {
                int ink = unchecked((int)Palette.Ink);
                HLine(px, w, h, xb, xn, y0 - 1, ink);
                HLine(px, w, h, xb, xn, y1, ink);
                VLine(px, w, h, xb, y0 - 1, y1 + 1, ink);   // left cap, closing the two rules at birth
            }
            if (n.LastKnownSize > palette.SCap) SaturationMark(px, w, h, xn, y0, y1);
        }

        static void DrawRemembered(RadarNode n, int[] px, int w, int h, in RasterView v, Palette palette)
        {
            int y0, y1;
            if (!RowSpan(RowOf(n.Price, v), v, v.PxPerTick, h, out y0, out y1)) return;

            int xb = BirthX(n.FirstSeenTicks, v);
            int xn = (int)Math.Ceiling(v.NowX);
            if (xn > w) xn = w;
            if (xn <= xb) return;

            // NO FILL, and the colour is the size at LAST OBSERVATION — never dimmed, never decayed.
            int c = unchecked((int)palette.ColourOf(n.LastKnownSize));
            int on = DashOn(n.Confidence);
            if (on <= 0) return;

            if (y1 - y0 >= 2)
            {
                Dash(px, w, h, xb, xn, y0, c, on, xb);
                Dash(px, w, h, xb, xn, y1 - 1, c, on, xb);
            }
            else Dash(px, w, h, xb, xn, y0, c, on, xb);   // <= 2 px: one rule at the band, same duty

            // Side tick at the left cap: 1 px x 3 px, UP = was ask, DOWN = was bid. The only place in
            // SizeMap where side is drawn at all, and it is geometry — never hue (spec §7).
            int dir = n.Side == Side.Ask ? -1 : 1;
            int y = n.Side == Side.Ask ? y0 : y1 - 1;
            for (int k = 1; k <= 3; k++) Put(px, w, h, xb, y + dir * k, c);

            // Also marked on a remembered band, though spec §4 tabulates it only for a live wall: the
            // rules carry lut[Idx(size)] too, so they saturate for exactly the same reason and would
            // report a 2000-lot memory and a 340-lot memory in the same colour.
            if (n.LastKnownSize > palette.SCap) SaturationMark(px, w, h, xn, y0, y1);
        }

        // 8 px period, on = 2 * round(4 * confidence). AwayFromZero, not .NET's default ToEven: the
        // spec's bands are lower-inclusive (0.625 -> 6 on), and ToEven rounds 2.5 down to 2 and hands
        // back 4 — one duty step too faint at every band edge.
        public static int DashOn(double confidence)
        {
            if (confidence < ConfidenceFloor) return 0;
            int steps = (int)Math.Round(4.0 * confidence, MidpointRounding.AwayFromZero);
            if (steps > 4) steps = 4;
            return 2 * steps;
        }

        static void Dash(int[] px, int w, int h, int xFrom, int xTo, int y, int colour, int on, int phase0)
        {
            if (y < 0 || y >= h || on <= 0) return;
            int a = xFrom < 0 ? 0 : xFrom;
            int b = xTo > w ? w : xTo;
            for (int x = a; x < b; x++)
            {
                int m = (x - phase0) % DashPeriod;
                if (m < 0) m += DashPeriod;
                if (m < on) px[y * w + x] = colour;
            }
        }

        // Two 1 px verticals with a 1 px gap at the band's right end. Without it the ramp reports a
        // 2000-lot wall and a 340-lot wall in the same colour, silently — the one thing a magnitude
        // channel is not allowed to do.
        static void SaturationMark(int[] px, int w, int h, int xRight, int y0, int y1)
        {
            int c = unchecked((int)Palette.Text);
            VLine(px, w, h, xRight - 4, y0, y1, c);
            VLine(px, w, h, xRight - 2, y0, y1, c);
        }

        static void HLine(int[] px, int w, int h, int xFrom, int xTo, int y, int colour)
        {
            if (y < 0 || y >= h) return;
            int a = xFrom < 0 ? 0 : xFrom;
            int b = xTo > w ? w : xTo;
            for (int x = a; x < b; x++) px[y * w + x] = colour;
        }

        static void VLine(int[] px, int w, int h, int x, int yFrom, int yTo, int colour)
        {
            if (x < 0 || x >= w) return;
            int a = yFrom < 0 ? 0 : yFrom;
            int b = yTo > h ? h : yTo;
            for (int y = a; y < b; y++) px[y * w + x] = colour;
        }

        static void Put(int[] px, int w, int h, int x, int y, int colour)
        {
            if (x < 0 || x >= w || y < 0 || y >= h) return;
            px[y * w + x] = colour;
        }
    }
}
