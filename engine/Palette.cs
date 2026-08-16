using System;

namespace SizeMap.Engine
{
    // The BEACON ramp and the six accents (visual-spec §2), plus the log size->index law.
    //
    // Two things here are non-obvious and both are deliberate:
    //
    // 1. The chart background is a PARAMETER, not a constant. Ramp stop 0 *is* "no resting size",
    //    so it has to be whatever NT8 already painted or the field reads as a grey wash over the
    //    user's own theme. Measured on the real machine: ~#1F1F1F, not the #232424 the probe
    //    hardcodes. Rebuild() is cheap enough to call every frame; it returns immediately when the
    //    background has not moved.
    // 2. Entries below FadeBelow are lerped INTO the background so "1 lot resting" is invisible
    //    rather than grey noise. Without it, an empty NQ book paints a full-panel dither.
    //
    // Monotonicity holds in two strengths, and the difference is worth stating because "strictly
    // monotone" is this ramp's headline claim. ABOVE FadeBelow the LUT is pure interpolation
    // between fixed stops — the background is not read at all — so it is strictly monotone and
    // background-independent. INSIDE the fade band it is not, by up to 0.0002 of relative
    // luminance: at a background like #1F1F1F the red channel (31) is higher than stop 1's (16),
    // so red falls while blue rises, and at 1-LSB granularity the weighted sum wobbles. That band
    // spans 0.021 of luminance in total and exists to be invisible, so the wobble is roughly one
    // fiftieth of a JND inside a region already below threshold. Tested both ways.
    //
    // Monotonicity precondition: the background must be DARKER than stop 1. If it is not, "nothing
    // resting" renders brighter than "8 lots resting" and the ramp lies about its own direction —
    // the honesty rule broken at the most basic level. The threshold lives here, next to the
    // precondition it enforces, because an indicator that guessed a different number is exactly how
    // this went wrong: it used 0.18, leaving every grey theme from ~#3A3A3A to ~#757575 unguarded
    // and silently inverted (27 inversions measured at #505050).
    public sealed class Palette
    {
        /// Relative luminance of stop 1 (#103772). A background at or above this inverts the ramp,
        /// so SizeMap refuses to draw rather than draw a lie. Callers ask BEFORE building a LUT.
        public const double MaxBackgroundLuminance = 0.0406;

        public static bool BackgroundIsUsable(uint bgra)
        {
            return RelativeLuminance(bgra) < MaxBackgroundLuminance;
        }

        /// sRGB relative luminance (WCAG), on the RGB of a 0xAARRGGBB value.
        public static double RelativeLuminance(uint bgra)
        {
            return 0.2126 * Srgb((bgra >> 16) & 0xFF)
                 + 0.7152 * Srgb((bgra >> 8) & 0xFF)
                 + 0.0722 * Srgb(bgra & 0xFF);
        }

        private static double Srgb(uint channel)
        {
            double c = channel / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        // Accents. #97A1AC does double duty as data (cancelled) and chrome (secondary text):
        // achromatic *is* the visual class "this is not a trade".
        public const uint Traded = 0xFFF14BE9;      // rose - someone paid for it
        public const uint NotTraded = 0xFF97A1AC;   // the seller walked
        public const uint Ink = 0xFF0E1013;         // grooves, glyph dilation, borders
        public const uint Text = 0xFFE6EAEE;        // numbers, saturation mark
        public const uint Plate = 0xFF16191C;       // legend/HUD panel fill
        public const uint Envelope = 0xFF3A3F44;    // depth-vision polyline, REC rule

        // Stops 1..8 at LUT indices 32,64,96,128,160,192,224,255. Index 0 is the background.
        // Strictly monotone in OKLab L, +0.084 per step, and zero of the 256 sRGB-lerped entries
        // land in the red or green hue arcs where they could compete with a candle body.
        static readonly uint[] StopColours =
        {
            0x00000000,
            0xFF103772, 0xFF1F4F94, 0xFF446B9E, 0xFF6187AE,
            0xFFA79F73, 0xFFC9B97A, 0xFFEFD472, 0xFFFFF49D
        };

        public const int FadeBelow = 24;
        const int SizeIdxMax = 1024;   // sizes above this fall back to the real log law

        readonly uint[] _lut = new uint[256];
        readonly byte[] _sizeIdx = new byte[SizeIdxMax];
        uint _bg;
        double _s0 = 8, _sCap = 320;
        bool _lutBuilt, _scaleBuilt;

        public Palette()
        {
            Rebuild(0xFF1F1F1F);       // the measured NT8 dark default; the indicator overrides it
            SetScale(_s0, _sCap);
        }

        // 256 opaque BGRA entries, index 0 == the background exactly. Handed out by reference for
        // the rasterizer's per-pixel lookup; read-only by contract.
        public uint[] Lut { get { return _lut; } }
        public uint Background { get { return _bg; } }
        public double S0 { get { return _s0; } }
        public double SCap { get { return _sCap; } }

        public void Rebuild(uint background)
        {
            if (_lutBuilt && background == _bg) return;
            _bg = background;
            for (int i = 0; i < 256; i++)
            {
                int s = i >> 5, t = i & 31;
                uint a = s == 0 ? background : StopColours[s];
                uint b = StopColours[s + 1 > 8 ? 8 : s + 1];
                uint c = LerpRgb(a, b, t / 31.0);
                _lut[i] = i < FadeBelow ? LerpRgb(background, c, i / (double)FadeBelow) : c;
            }
            // Force opaque like every other entry. A chart-background brush with Opacity < 1 would
            // otherwise put non-premultiplied alpha into a premultiplied D2D bitmap — alpha leaking
            // into a data channel, in the one file whose thesis is that it never does.
            _lut[0] = background | 0xFF000000u;
            _lutBuilt = true;
        }

        // s0/sCap come from DepthBaseline percentiles and move every ~5 s. Filling the small-size
        // table here keeps Math.Log out of the per-cell render loop (~350k cells on a folded frame).
        public void SetScale(double s0, double sCap)
        {
            if (s0 < 1) s0 = 1;
            if (sCap <= s0) sCap = s0 * 2;
            if (_scaleBuilt && s0 == _s0 && sCap == _sCap) return;
            _s0 = s0; _sCap = sCap;
            for (int i = 0; i < SizeIdxMax; i++) _sizeIdx[i] = (byte)Idx(i, s0, sCap);
            _scaleBuilt = true;
        }

        public int IdxOf(long size)
        {
            if (size <= 0) return 0;
            return size < SizeIdxMax ? _sizeIdx[size] : Idx(size, _s0, _sCap);
        }

        public uint ColourOf(long size)
        {
            return _lut[IdxOf(size)];
        }

        // log(1 + size/s0) / log(1 + sCap/s0). One doubling of size is ~+42 indices anywhere on the
        // scale, which is the whole reason it is log and not linear: at a fixed linear cap ~92% of
        // NQ levels land in the first 20 indices.
        public static int Idx(double size, double s0, double sCap)
        {
            if (size <= 0) return 0;
            if (s0 < 1) s0 = 1;
            if (sCap <= s0) return 255;
            double u = Math.Log(1.0 + size / s0) / Math.Log(1.0 + sCap / s0);
            if (u <= 0) return 0;
            if (u >= 1) return 255;
            return (int)Math.Round(255.0 * u);
        }

        // Integer sRGB lerp instead of a true OKLab lerp: max error 0.0079 dE_ok, zero L-dips, zero
        // forbidden-arc entries (measured in the spec). No colour science in the render path.
        static uint LerpRgb(uint a, uint b, double t)
        {
            if (t <= 0) return a;
            if (t >= 1) return b;
            uint outv = 0;
            for (int sh = 0; sh < 32; sh += 8)
            {
                int x = (int)((a >> sh) & 0xFF);
                int y = (int)((b >> sh) & 0xFF);
                int z = x + (int)Math.Round((y - x) * t);
                outv |= (uint)z << sh;
            }
            return outv | 0xFF000000;   // the raster is blitted fully opaque; alpha is never data
        }
    }
}
