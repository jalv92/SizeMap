using System;

namespace SizeMap.Engine
{
    // Everything SizeMap says in words: the two-line HUD (visual-spec §5.1) and the legend plate
    // (§5.2). Both are written into the same int[] the heat field is, AFTER Rasterize has turned
    // ramp indices into colours — so they cost zero post-blit primitives and the count stays at
    // exactly one DrawBitmap per frame.
    //
    // It lives in engine/ rather than in the indicator for the same reason the rasterizer does:
    // the harness renders the real HUD to a PNG at `dotnet run` speed, so "is the text legible"
    // is answered by looking at pixels instead of by loading NinjaTrader.
    //
    // The ramp strip is sampled from the LIVE LUT, never from a copy of the stop table. A legend
    // that can disagree with the render is worse than no legend: it is a wrong scale presented
    // with authority.
    public static class Chrome
    {
        // What the HUD states. Every field is a number SizeMap already knows; nothing here is
        // computed from anything the caller has not measured.
        public struct HudInfo
        {
            public string Instrument;    // "NQ 09-26"
            public int DepthLevels;      // configured feed depth; 0 => omitted, see below
            public int Observed;         // max levels actually observed this session
            public int ColumnMs;         // what one rendered column really spans
            public int RowTicks;         // how many price ticks fold into one row
            public int S0, SCap;         // the live ramp anchors, in lots
            public bool WallsKnown;      // false => the tracker is not running; say OFF, do not print 0
            // Live / remembered / below the confidence floor. The spec's third token is D for dead,
            // which needs the EpisodeClassifier drain nothing calls yet; F is what SizeMap can
            // actually count today, and a census that names the wrong bucket is worse than one
            // letter off the spec.
            public int WallsLive, WallsRemembered, WallsFaint;
            public double FrameMs;       // EMA over OnRender, not the instantaneous frame
            public double Fps;
        }

        const int PlateW = 196, PlateH = 78, Margin = 8;
        const int StripW = 120, StripH = 8;
        const int LedgerW = 44;

        /// Two lines at the top-left of the plot rect. Coordinates are raster-local, because the
        /// raster is blitted at (panelX, panelY) and knows nothing about where the panel sits.
        public static void DrawHud(int[] px, int w, int h, HudInfo i, int scale)
        {
            if (px == null || w <= 0 || h <= 0) return;
            if (scale < 1) scale = 1;

            int x = 6 * scale, y = 6 * scale, c = x;

            c += BitmapFont.Draw(px, w, h, c, y, "SIZEMAP", Palette.Text, scale);
            if (!string.IsNullOrEmpty(i.Instrument))
                c += BitmapFont.Draw(px, w, h, c, y, "  " + i.Instrument, Palette.Text, scale);

            // The feed-truth badge. It escalates by CONTRAST, not by hue (5.7:1 -> 12.6:1), so the
            // rule that text wears text tokens survives an alarm state.
            // DEPTH is printed only when the caller actually knows the configured depth: NT8 does
            // not expose it, so the indicator passes 0 and the badge shrinks to OBS rather than
            // inventing a number in the one line whose whole job is feed honesty.
            string feed = "   " + (i.DepthLevels > 0 ? "DEPTH " + Num(i.DepthLevels) + "L  " : "")
                        + "OBS " + Num(i.Observed);
            c += BitmapFont.Draw(px, w, h, c, y, feed, i.Observed < 20 ? Palette.Text : Palette.NotTraded, scale);

            // The honesty rule made mechanical: on a 1-min chart a 250 ms bucket is 0.025 px, so
            // 239 of 240 buckets are invisible and one wins by rounding. Say what a column IS.
            c += BitmapFont.Draw(px, w, h, c, y,
                "   COL " + Dur(i.ColumnMs) + "  ROW " + Num(i.RowTicks) + "T", Palette.NotTraded, scale);

            y += BitmapFont.LineHeight * scale;
            c = x;
            c += BitmapFont.Draw(px, w, h, c, y,
                "LOG  S0 " + Num(i.S0) + "  CAP " + Num(i.SCap), Palette.NotTraded, scale);
            c += BitmapFont.Draw(px, w, h, c, y,
                "   WALLS " + (i.WallsKnown
                    ? Num(i.WallsLive) + "L " + Num(i.WallsRemembered) + "R " + Num(i.WallsFaint) + "F"
                    : "OFF"),
                Palette.NotTraded, scale);

            bool slow = i.FrameMs > 20;
            c += BitmapFont.Draw(px, w, h, c, y, "   ", Palette.NotTraded, scale);
            c += BitmapFont.Draw(px, w, h, c, y, (slow ? "!" : "") + Dec1(i.FrameMs) + "MS",
                slow ? Palette.Text : Palette.NotTraded, scale);
            BitmapFont.Draw(px, w, h, c, y, "  " + Dec1(i.Fps) + "FPS", Palette.NotTraded, scale);
        }

        /// The legend plate, bottom-right. Returns false when it was suppressed — a legend that
        /// covers a quarter of a small panel is worse than none, and the caller may want to know.
        public static bool DrawLegend(int[] px, int w, int h, Palette pal, int scale)
        {
            if (px == null || pal == null || w <= 0 || h <= 0) return false;
            if (scale < 1) scale = 1;
            if (w < 500 || h < 300) return false;                 // spec §5.2 auto-hide

            int pw = PlateW * scale, ph = PlateH * scale;
            int x0 = w - pw - Margin * scale, y0 = h - ph - Margin * scale;
            if (x0 < 0 || y0 < 0) return false;

            Rect(px, w, h, x0, y0, pw, ph, Palette.Plate);
            Border(px, w, h, x0, y0, pw, ph, scale, Palette.Ink);

            uint[] lut = pal.Lut;
            int s0 = (int)Math.Round(pal.S0), cap = (int)Math.Round(pal.SCap);

            // ---- ramp strip, sampled from the live LUT ----
            for (int i = 0; i < StripW; i++)
                Rect(px, w, h, x0 + (Margin + i) * scale, y0 + 6 * scale, scale, StripH * scale,
                     lut[i * 255 / (StripW - 1)]);
            BitmapFont.Draw(px, w, h, x0 + 134 * scale, y0 + 6 * scale, "LOG", Palette.NotTraded, scale);

            // Three labels equally spaced in PIXELS and unequally spaced in lots — the log law made
            // visible without a sentence about it. The middle one is the size at the strip's own
            // centre pixel (LUT index 128), inverted from the same law the render uses, not the
            // geometric mean of the anchors: the label has to name the colour above it.
            int mid = (int)Math.Round(pal.S0 * (Math.Pow(1.0 + pal.SCap / pal.S0, 128 / 255.0) - 1.0));
            BitmapFont.Draw(px, w, h, x0 + Margin * scale, y0 + 18 * scale, Num(s0), Palette.NotTraded, scale);
            CentreAt(px, w, h, x0 + (Margin + StripW / 2) * scale, y0 + 18 * scale, Num(mid), Palette.NotTraded, scale);
            RightAt(px, w, h, x0 + (Margin + StripW) * scale, y0 + 18 * scale, Num(cap), Palette.NotTraded, scale);

            // What one doubling of size buys you, measured at the middle of the ramp with the live
            // anchors — the number moves when s0/sCap move, which is the point of printing it.
            int x2 = Palette.Idx(2.0 * mid, pal.S0, pal.SCap) - Palette.Idx(mid, pal.S0, pal.SCap);
            CentreAt(px, w, h, x0 + pw / 2, y0 + 28 * scale, "X2 = " + Num(x2) + " IDX", Palette.NotTraded, scale);

            // ---- form key: solid = live, hollow = remembered, dashed = fading ----
            int fy = y0 + 40 * scale;
            Rect(px, w, h, x0 + Margin * scale, fy, 14 * scale, 9 * scale, lut[255]);
            BitmapFont.Draw(px, w, h, x0 + 24 * scale, fy + scale, "NOW", Palette.Text, scale);
            Rules(px, w, h, x0 + 46 * scale, fy, 14, scale, lut[255], 8);      // 8 on / 0 off = solid
            BitmapFont.Draw(px, w, h, x0 + 62 * scale, fy + scale, "MEM", Palette.Text, scale);
            Rules(px, w, h, x0 + 84 * scale, fy, 14, scale, lut[255], 2);      // 2 on / 6 off = about to drop
            BitmapFont.Draw(px, w, h, x0 + 100 * scale, fy + scale, "FAINT", Palette.Text, scale);

            // NO GLYPH KEY AND NO MINI-LEDGER, and their absence is the point.
            //
            // The spec's legend advertises ● BOUGHT / ▲ HELD / ✕ PULLED / ◇ N-C and a three-segment
            // ledger. No layer draws any of them yet, and one of them cannot be drawn at all:
            // measured over 7,454 episodes of real ES tape, PULLED fired ONCE. Not a calibration
            // miss — a geometry one. Episodes only open on walls at the inside (D_approach = 1), so
            // for an ask wall "the level empties" and "best ask moves above it" are the same event;
            // ep.Crossed is therefore true whenever the wall dies, and Consumed is tested first.
            // Emptied-and-crossed was 100.0% of every emptied episode on all three tapes, while
            // 13-18% of episodes genuinely cancel more than they trade and get labelled Consumed.
            //
            // A legend is a promise about what the chart means. Printing a key to four marks the
            // chart does not draw — one of which the engine structurally cannot produce — is the
            // exact failure this instrument was built not to have.
            //
            // These come back the day the glyph and ledger layers ship, which is gated on Pulled
            // becoming reachable (test it before Consumed, or open episodes at D_approach >= 2).
            return true;
        }

        // ---------------------------------------------------------------- text placement

        static void CentreAt(int[] px, int w, int h, int cx, int y, string s, uint colour, int scale)
        {
            BitmapFont.Draw(px, w, h, cx - (BitmapFont.Measure(s, scale) - scale) / 2, y, s, colour, scale);
        }

        // The last px of an advance is the letter gap, not ink, so the right edge is Measure - scale.
        static void RightAt(int[] px, int w, int h, int rx, int y, string s, uint colour, int scale)
        {
            BitmapFont.Draw(px, w, h, rx - (BitmapFont.Measure(s, scale) - scale), y, s, colour, scale);
        }

        // ---------------------------------------------------------------- shapes
        //
        // ponytail: the four glyphs are generated here, in the legend only. Ceiling — Phase 2's
        // wall glyphs need the same four with a 1 px ink dilation, and generating them twice is how
        // the legend and the chart drift apart. Upgrade path — when the wall glyphs land, hoist
        // Glyph9 into a shared 1-bit stencil table and have both call it.
        static void Glyph9(int[] px, int w, int h, int x, int y, int scale, uint colour, int kind)
        {
            for (int r = 0; r < 9; r++)
            {
                for (int c = 0; c < 9; c++)
                {
                    int dx = c - 4, dy = r - 4;
                    bool on;
                    switch (kind)
                    {
                        case 0: on = dx * dx + dy * dy <= 16; break;                 // consumed: filled disc
                        case 1: on = 2 * (dx < 0 ? -dx : dx) <= r; break;            // absorbed: filled triangle
                        case 2: on = (dx < 0 ? -dx : dx) == (dy < 0 ? -dy : dy); break;   // pulled: saltire
                        default: on = (dx < 0 ? -dx : dx) + (dy < 0 ? -dy : dy) == 4; break;  // unclassified
                    }
                    if (on) Rect(px, w, h, x + c * scale, y + r * scale, scale, scale, colour);
                }
            }
        }

        // A 14-px-wide hollow band: two 1 px rules at top and bottom, dash duty = confidence.
        static void Rules(int[] px, int w, int h, int x, int y, int width, int scale, uint colour, int on)
        {
            for (int i = 0; i < width; i++)
            {
                if (i % 8 >= on) continue;                       // 8 px period, `on` px of ink
                Rect(px, w, h, x + i * scale, y, scale, scale, colour);
                Rect(px, w, h, x + i * scale, y + 8 * scale, scale, scale, colour);
            }
        }

        static void Border(int[] px, int w, int h, int x, int y, int bw, int bh, int t, uint colour)
        {
            Rect(px, w, h, x, y, bw, t, colour);
            Rect(px, w, h, x, y + bh - t, bw, t, colour);
            Rect(px, w, h, x, y, t, bh, colour);
            Rect(px, w, h, x + bw - t, y, t, bh, colour);
        }

        static void Rect(int[] px, int w, int h, int x, int y, int rw, int rh, uint colour)
        {
            int x1 = x + rw, y1 = y + rh;
            if (x < 0) x = 0;
            if (y < 0) y = 0;
            if (x1 > w) x1 = w;
            if (y1 > h) y1 = h;
            int c = unchecked((int)colour);
            for (int yy = y; yy < y1; yy++)
            {
                int row = yy * w;
                for (int xx = x; xx < x1; xx++) px[row + xx] = c;
            }
        }

        // ---------------------------------------------------------------- numbers
        //
        // Formatted by hand, from integers. double.ToString() on a Spanish machine writes "1,4",
        // and ',' is not in the 5x7 charset — the frame-time readout would silently lose its
        // decimal point on exactly the machine this runs on. int.ToString() has no group separator
        // to lose, so plain digits are safe.

        static string Num(int v)
        {
            return v.ToString();
        }

        static string Dec1(double v)
        {
            if (v < 0 || double.IsNaN(v)) v = 0;
            if (v > 99999) v = 99999;
            long t = (long)(v * 10.0 + 0.5);
            return (t / 10).ToString() + "." + (t % 10).ToString();
        }

        static string Dur(int ms)
        {
            if (ms < 1000) return Num(ms) + "MS";
            return ms % 1000 == 0 ? Num(ms / 1000) + "S" : Dec1(ms / 1000.0) + "S";
        }
    }
}
