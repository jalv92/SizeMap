using System;
using SizeMap.Engine;
using Xunit;

namespace SizeMap.Tests
{
    public class PaletteTests
    {
        const uint DarkBg = 0xFF1F1F1F;   // measured on the real machine

        static double Chan(uint v)
        {
            double s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        // Relative luminance is computed here rather than imported, on purpose: the palette must be
        // provably monotone by an independent measure, not by its own arithmetic.
        static double Lum(uint c)
        {
            return 0.2126 * Chan((c >> 16) & 0xFF) + 0.7152 * Chan((c >> 8) & 0xFF) + 0.0722 * Chan(c & 0xFF);
        }

        [Fact]
        public void LutLuminanceNeverDecreases()
        {
            var p = new Palette();
            p.Rebuild(DarkBg);

            // Tolerance = one 8-bit LSB of the red channel near black (1.6e-4). It is needed for
            // exactly one entry: the user's grey background has MORE red than stop 1 (#103772), so
            // inside the fade the red channel steps down by 1 before green and blue have climbed
            // enough to pay for it. The continuous blend is monotone there (dL/dt = +20 at t=0);
            // what is left is quantisation the encoding cannot express away. The inversions this
            // test exists to catch are stop-level, ~0.02 and up — 100x this.
            const double lsb = 2e-4;
            for (int i = 1; i < 256; i++)
            {
                double a = Lum(p.Lut[i - 1]), b = Lum(p.Lut[i]);
                Assert.True(b >= a - lsb, "luminance inversion at index " + i + ": " + a + " -> " + b);
            }

            // And nothing structural hides inside the tolerance: every 8-index window climbs.
            for (int i = 0; i + 8 < 256; i++)
                Assert.True(Lum(p.Lut[i + 8]) > Lum(p.Lut[i]), "ramp is flat or falling across [" + i + "," + (i + 8) + "]");
        }

        [Fact]
        public void LutSpansFromBackgroundToTheBrightestStop()
        {
            var p = new Palette();
            p.Rebuild(DarkBg);
            Assert.Equal(0xFFFFF49Du, p.Lut[255]);
            Assert.True(Lum(p.Lut[255]) / Lum(p.Lut[0]) > 40.0);   // the ramp has to be the loudest thing on screen
        }

        [Theory]
        [InlineData(0xFF1F1F1Fu)]
        [InlineData(0xFF232424u)]
        [InlineData(0xFF000000u)]
        public void IndexZeroIsTheBackgroundExactly(uint bg)
        {
            var p = new Palette();
            p.Rebuild(bg);
            Assert.Equal(bg, p.Lut[0]);
            Assert.Equal(bg, p.Background);
        }

        [Fact]
        public void RebuildTracksABackgroundChange()
        {
            var p = new Palette();
            p.Rebuild(0xFF1F1F1F);
            uint before = p.Lut[4];
            p.Rebuild(0xFF000000);
            Assert.Equal(0xFF000000u, p.Lut[0]);
            Assert.NotEqual(before, p.Lut[4]);      // the faded foot follows the background
            Assert.Equal(0xFFFFF49Du, p.Lut[255]);  // the top of the ramp does not
        }

        [Fact]
        public void TheFootFadesIntoTheBackgroundSoOneLotIsInvisible()
        {
            var p = new Palette();
            p.Rebuild(DarkBg);
            double bg = Lum(DarkBg);
            Assert.True(Math.Abs(Lum(p.Lut[1]) - bg) < 0.0005);              // 1 lot resting: gone
            Assert.True(Lum(p.Lut[Palette.FadeBelow]) - bg > 0.005);         // past the fade: visible
        }

        [Fact]
        public void IdxIsMonotoneAndSaturates()
        {
            const double s0 = 8, sCap = 320;
            Assert.Equal(0, Palette.Idx(0, s0, sCap));
            Assert.Equal(0, Palette.Idx(-5, s0, sCap));

            int prev = -1;
            for (int size = 0; size <= 2000; size++)
            {
                int idx = Palette.Idx(size, s0, sCap);
                Assert.True(idx >= prev, "Idx went down at size " + size);
                Assert.InRange(idx, 0, 255);
                prev = idx;
            }
            Assert.Equal(255, Palette.Idx(sCap, s0, sCap));
            Assert.Equal(255, Palette.Idx(2000, s0, sCap));
        }

        [Fact]
        public void IdxIsTheDocumentedLogLawNotLinear()
        {
            // Worked NQ example from visual-spec §2.4: s0 8, sCap 320.
            Assert.Equal(48, Palette.Idx(8, 8, 320));
            Assert.Equal(123, Palette.Idx(40, 8, 320));
            Assert.Equal(209, Palette.Idx(160, 8, 320));
            // One doubling of size is ~+42 indices anywhere on the scale (spec: +37 near the floor,
            // +46 near the cap) — the property that makes a 20-lot level and a 400-lot wall both
            // readable on one ramp.
            Assert.InRange(Palette.Idx(40, 8, 320) - Palette.Idx(20, 8, 320), 35, 50);
            Assert.InRange(Palette.Idx(160, 8, 320) - Palette.Idx(80, 8, 320), 35, 50);
        }

        [Fact]
        public void IdxOfMatchesIdxAcrossTheFastTable()
        {
            var p = new Palette();
            p.SetScale(8, 320);
            for (int size = 0; size < 1024; size++)
                Assert.Equal(Palette.Idx(size, 8, 320), p.IdxOf(size));
            Assert.Equal(255, p.IdxOf(50000));
        }

        [Fact]
        public void SetScaleGuardsAgainstADegenerateBaseline()
        {
            var p = new Palette();
            p.SetScale(0, 0);              // DepthBaseline before warm-up
            Assert.True(p.S0 >= 1);
            Assert.True(p.SCap > p.S0);
            Assert.Equal(0, p.IdxOf(0));   // still honest about "nothing resting"
        }
    }
}
