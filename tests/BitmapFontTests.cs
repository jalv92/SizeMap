using System;
using System.Text;
using SizeMap.Engine;
using Xunit;

namespace SizeMap.Tests
{
    // The glyph table is hand-authored data, and hand-authored data rots silently: a row deleted
    // during an edit shifts every glyph after it by one row and the HUD still "renders text" — it
    // just renders the wrong letters, which nobody notices in a 5x7 screenshot.
    //
    // So these tests do two things: render the WHOLE charset and check the shape invariants every
    // glyph must satisfy, and compare three glyphs against art written out again here, by hand,
    // independently of the table.
    public class BitmapFontTests
    {
        const uint Ink = 0xFFFFFFFF;
        const string Charset = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ.\u00B7%!-:/=";

        // One character rendered into its own advance box (6 x 9 at scale 1), as '#'/'.' rows.
        // The box is deliberately bigger than the glyph: the empty column and the two empty rows
        // are what prove the letter spacing and the line height are real.
        static string[] Box(char c, int scale)
        {
            int w = BitmapFont.Advance * scale, h = BitmapFont.LineHeight * scale;
            int[] px = new int[w * h];
            BitmapFont.Draw(px, w, h, 0, 0, c.ToString(), Ink, scale);

            string[] rows = new string[h];
            for (int y = 0; y < h; y++)
            {
                StringBuilder sb = new StringBuilder(w);
                for (int x = 0; x < w; x++) sb.Append(px[y * w + x] == unchecked((int)Ink) ? '#' : '.');
                rows[y] = sb.ToString();
            }
            return rows;
        }

        static void AssertGlyph(char c, params string[] art)
        {
            string[] box = Box(c, 1);
            Assert.Equal(BitmapFont.GlyphH, art.Length);
            for (int r = 0; r < BitmapFont.GlyphH; r++)
                Assert.Equal(art[r] + ".", box[r]);              // + the letter-spacing column
        }

        [Fact]
        public void The_whole_charset_renders_and_stays_inside_its_box()
        {
            foreach (char c in Charset)
            {
                string[] box = Box(c, 1);

                int ink = 0;
                for (int r = 0; r < box.Length; r++)
                    for (int x = 0; x < box[r].Length; x++)
                        if (box[r][x] == '#')
                        {
                            ink++;
                            Assert.True(x < BitmapFont.GlyphW, c + ": ink in the letter-spacing column");
                            Assert.True(r < BitmapFont.GlyphH, c + ": ink below the glyph box");
                        }

                // 3 px is the floor for a mark a human can see at all; the space is the one glyph
                // allowed to be empty. A row-shifted table shows up here as a blank letter.
                if (c == ' ') Assert.Equal(0, ink);
                else Assert.True(ink >= 3, c + ": only " + ink + " ink pixels — glyph table corrupted?");
            }
        }

        [Fact]
        public void Three_glyphs_match_art_written_out_again_here()
        {
            AssertGlyph('0',
                ".###.",
                "#...#",
                "#..##",
                "#.#.#",
                "##..#",
                "#...#",
                ".###.");

            AssertGlyph('A',
                "..#..",
                ".#.#.",
                "#...#",
                "#...#",
                "#####",
                "#...#",
                "#...#");

            AssertGlyph(':',
                ".....",
                ".##..",
                ".##..",
                ".....",
                ".##..",
                ".##..",
                ".....");
        }

        // '0' vs 'O' is the pair that decides whether a size readout can be trusted at 5 px.
        [Fact]
        public void Zero_is_distinguishable_from_O()
        {
            Assert.NotEqual(string.Join("|", Box('0', 1)), string.Join("|", Box('O', 1)));
        }

        [Fact]
        public void An_unknown_character_is_blank_and_still_advances()
        {
            int[] px = new int[60 * 9];
            int adv = BitmapFont.Draw(px, 60, 9, 0, 0, "@", Ink, 1);
            Assert.Equal(BitmapFont.Advance, adv);
            for (int i = 0; i < px.Length; i++) Assert.Equal(0, px[i]);
        }

        [Fact]
        public void Lowercase_folds_onto_the_uppercase_glyph()
        {
            Assert.Equal(string.Join("|", Box('A', 1)), string.Join("|", Box('a', 1)));
        }

        [Fact]
        public void Measure_agrees_with_what_Draw_advances()
        {
            int[] px = new int[200 * 20];
            foreach (int scale in new[] { 1, 2 })
            {
                int adv = BitmapFont.Draw(px, 200, 20, 0, 0, "S0 8", Palette.Text, scale);
                Assert.Equal(BitmapFont.Measure("S0 8", scale), adv);
                Assert.Equal(4 * BitmapFont.Advance * scale, adv);
            }
            Assert.Equal(0, BitmapFont.Measure(null, 1));
            Assert.Equal(0, BitmapFont.Draw(px, 200, 20, 0, 0, null, Palette.Text, 1));
        }

        // Integer scaling of a 1-bit font is the entire DPI story: every x2 pixel must be a solid
        // 2x2 block of the x1 pixel, with no interpolation anywhere.
        [Fact]
        public void Scale_two_is_exactly_the_x1_glyph_with_2x2_blocks()
        {
            string[] one = Box('5', 1);
            string[] two = Box('5', 2);
            for (int r = 0; r < one.Length; r++)
                for (int x = 0; x < one[r].Length; x++)
                {
                    char e = one[r][x];
                    Assert.Equal(e, two[2 * r][2 * x]);
                    Assert.Equal(e, two[2 * r][2 * x + 1]);
                    Assert.Equal(e, two[2 * r + 1][2 * x]);
                    Assert.Equal(e, two[2 * r + 1][2 * x + 1]);
                }
        }

        // A HUD line is laid out from the left and can run off a narrow panel. Wrapping onto the
        // next row of the raster would draw garbage across the heat field, so it must clip.
        [Fact]
        public void Drawing_off_every_edge_clips_instead_of_wrapping_or_throwing()
        {
            const int W = 20, H = 12;
            int[] px = new int[W * H];

            BitmapFont.Draw(px, W, H, -100, 3, "88", Ink, 1);
            BitmapFont.Draw(px, W, H, 100, 3, "88", Ink, 1);
            BitmapFont.Draw(px, W, H, 3, -100, "88", Ink, 1);
            BitmapFont.Draw(px, W, H, 3, 100, "88", Ink, 1);
            for (int i = 0; i < px.Length; i++) Assert.Equal(0, px[i]);

            // Straddling the right edge: ink on screen, nothing on the row below.
            BitmapFont.Draw(px, W, H, W - 3, 0, "8", Ink, 1);
            bool any = false;
            for (int y = 0; y < BitmapFont.GlyphH; y++)
                for (int x = W - 3; x < W; x++) any |= px[y * W + x] != 0;
            Assert.True(any);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W - 3; x++) Assert.Equal(0, px[y * W + x]);
        }
    }
}
