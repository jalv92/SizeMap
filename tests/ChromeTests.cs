using System;
using SizeMap.Engine;
using Xunit;

namespace SizeMap.Tests
{
    // The HUD and the legend are the two places where SizeMap makes a CLAIM in words. Each test
    // here pins one claim that would otherwise be able to go quietly wrong on a live chart.
    public class ChromeTests
    {
        const int W = 1600, H = 900;

        static Palette Pal(double s0, double cap)
        {
            Palette p = new Palette();
            p.Rebuild(0xFF1F1F1F);
            p.SetScale(s0, cap);
            return p;
        }

        static Chrome.HudInfo Info()
        {
            Chrome.HudInfo i = new Chrome.HudInfo();
            i.Instrument = "NQ 09-26";
            i.DepthLevels = 10;
            i.Observed = 10;
            i.ColumnMs = 15000;
            i.RowTicks = 1;
            i.S0 = 8;
            i.SCap = 320;
            i.WallsKnown = false;
            i.FrameMs = 1.4;
            i.Fps = 4.0;
            return i;
        }

        static int Count(int[] px, uint colour)
        {
            int c = unchecked((int)colour), n = 0;
            for (int i = 0; i < px.Length; i++) if (px[i] == c) n++;
            return n;
        }

        // The one thing a legend must never do is disagree with the render. Sampling the live LUT
        // is what makes that structurally impossible, so this test moves the ramp anchors and the
        // background underneath it and demands the strip follow both.
        [Fact]
        public void The_ramp_strip_is_sampled_from_the_live_LUT()
        {
            foreach (uint bg in new uint[] { 0xFF1F1F1F, 0xFF000000 })
            {
                Palette p = Pal(34, 480);
                p.Rebuild(bg);
                int[] px = new int[W * H];
                Assert.True(Chrome.DrawLegend(px, W, H, p, 1));

                // Plate at (W-204, H-86); strip 120 px wide at +8, 8 px tall at +6.
                int x0 = W - 204, y0 = H - 86, y = y0 + 6 + 3;
                for (int i = 0; i < 120; i++)
                    Assert.Equal(unchecked((int)p.Lut[i * 255 / 119]), px[y * W + x0 + 8 + i]);

                // The ends are the ends: background at the foot, the top stop at the cap.
                Assert.Equal(unchecked((int)p.Lut[0]), px[y * W + x0 + 8]);
                Assert.Equal(unchecked((int)p.Lut[255]), px[y * W + x0 + 8 + 119]);
            }
        }

        [Fact]
        public void The_legend_hides_itself_on_a_panel_too_small_to_carry_it()
        {
            int[] px = new int[499 * 400];
            Assert.False(Chrome.DrawLegend(px, 499, 400, Pal(8, 320), 1));
            Assert.False(Chrome.DrawLegend(px, 600, 299, Pal(8, 320), 1));
            for (int i = 0; i < px.Length; i++) Assert.Equal(0, px[i]);   // and it drew NOTHING
        }

        // The frame-time readout escalates by contrast when the frame blows the budget. Line 2 of
        // the HUD is otherwise entirely #97A1AC, so a single text-token pixel on those rows is the
        // alarm and nothing else can produce one.
        [Fact]
        public void A_slow_frame_escalates_the_readout_to_full_contrast()
        {
            const int Y0 = 6 + BitmapFont.LineHeight, Y1 = Y0 + BitmapFont.GlyphH;

            Chrome.HudInfo fast = Info();
            int[] a = new int[W * H];
            Chrome.DrawHud(a, W, H, fast, 1);
            Assert.Equal(0, CountRows(a, Y0, Y1, Palette.Text));

            Chrome.HudInfo slow = Info();
            slow.FrameMs = 47.0;
            int[] b = new int[W * H];
            Chrome.DrawHud(b, W, H, slow, 1);
            Assert.True(CountRows(b, Y0, Y1, Palette.Text) > 20);
        }

        static int CountRows(int[] px, int y0, int y1, uint colour)
        {
            int c = unchecked((int)colour), n = 0;
            for (int y = y0; y < y1; y++)
                for (int x = 0; x < W; x++) if (px[y * W + x] == c) n++;
            return n;
        }

        // Two lines, top-left, and nothing anywhere else: the HUD must not be able to scribble over
        // the heat field it sits on.
        [Fact]
        public void The_hud_stays_inside_two_lines_at_the_top_left()
        {
            int[] px = new int[W * H];
            Chrome.DrawHud(px, W, H, Info(), 1);

            Assert.True(Count(px, Palette.Text) > 50);          // it drew real text
            Assert.True(Count(px, Palette.NotTraded) > 50);

            int bottom = 6 + BitmapFont.LineHeight + BitmapFont.GlyphH;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (y < 6 || y >= bottom || x < 6)
                        Assert.Equal(0, px[y * W + x]);
        }

        [Fact]
        public void Nothing_in_the_chrome_throws_on_a_degenerate_panel()
        {
            int[] px = new int[16];
            Chrome.DrawHud(px, 4, 4, Info(), 1);
            Chrome.DrawHud(null, 4, 4, Info(), 1);
            Chrome.DrawHud(px, 0, 0, Info(), 1);
            Assert.False(Chrome.DrawLegend(px, 4, 4, Pal(8, 320), 1));
            Assert.False(Chrome.DrawLegend(px, 4, 4, null, 1));
        }
    }
}
