using System;

namespace SizeMap.Engine
{
    // A hand-authored 5x7 1-bit font, written straight into the BGRA int[].
    //
    // DirectWrite was rejected for this (visual-spec §0 #7): TextFormat/TextLayout lifetimes, DPI,
    // NoSnap and grayscale-AA are 1-1.5 days of the fiddliest bugs in the platform, and every
    // DrawText is a post-blit primitive — which is the one budget SizeMap keeps at exactly 1.
    //
    // The DPI story is the whole reason this is 1-bit: an integer-scaled 1-bit glyph is still
    // 1-bit, so x1 and x2 are both pixel-exact. There is no x1.5, and asking for one would put a
    // grey fringe on text drawn over a heat field whose colours ARE the data.
    //
    // The glyph table below is the deliverable. It is authored, not generated: a formula produces
    // a font where every letter is equally unreadable. Corrupt it and BitmapFontTests fails on
    // exact pixel patterns, not on "it drew something".
    public static class BitmapFont
    {
        public const int GlyphW = 5;
        public const int GlyphH = 7;
        public const int Advance = 6;      // 5 px glyph + 1 px letter spacing
        public const int LineHeight = 9;   // 7 px glyph + 2 px leading

        // Order fixes the glyph table's order. Index 0 is space, and every unknown char maps to it
        // — a caller that types a comma gets a hole, never an exception on the render thread.
        // U+00B7 is written as an escape so this file stays pure ASCII: NinjaTrader copies it into
        // Custom/ and csc reads a BOM-less file in the machine's ANSI codepage, where a literal
        // middle dot is whatever CP1252 says it is.
        const string Charset = " 0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ.\u00B7%!-:/=";

        // 7 rows of 5 columns per glyph, '#' = ink. Same order as Charset.
        static readonly string[] Art =
        {
            // ' '
            ".....",
            ".....",
            ".....",
            ".....",
            ".....",
            ".....",
            ".....",
            // '0' — the diagonal is what separates it from 'O' at 5 px wide
            ".###.",
            "#...#",
            "#..##",
            "#.#.#",
            "##..#",
            "#...#",
            ".###.",
            // '1'
            "..#..",
            ".##..",
            "..#..",
            "..#..",
            "..#..",
            "..#..",
            ".###.",
            // '2'
            ".###.",
            "#...#",
            "....#",
            "...#.",
            "..#..",
            ".#...",
            "#####",
            // '3'
            "#####",
            "...#.",
            "..#..",
            "...#.",
            "....#",
            "#...#",
            ".###.",
            // '4'
            "...#.",
            "..##.",
            ".#.#.",
            "#..#.",
            "#####",
            "...#.",
            "...#.",
            // '5'
            "#####",
            "#....",
            "####.",
            "....#",
            "....#",
            "#...#",
            ".###.",
            // '6'
            "..##.",
            ".#...",
            "#....",
            "####.",
            "#...#",
            "#...#",
            ".###.",
            // '7'
            "#####",
            "....#",
            "...#.",
            "..#..",
            ".#...",
            ".#...",
            ".#...",
            // '8'
            ".###.",
            "#...#",
            "#...#",
            ".###.",
            "#...#",
            "#...#",
            ".###.",
            // '9'
            ".###.",
            "#...#",
            "#...#",
            ".####",
            "....#",
            "...#.",
            ".##..",
            // 'A'
            "..#..",
            ".#.#.",
            "#...#",
            "#...#",
            "#####",
            "#...#",
            "#...#",
            // 'B'
            "####.",
            "#...#",
            "#...#",
            "####.",
            "#...#",
            "#...#",
            "####.",
            // 'C'
            ".###.",
            "#...#",
            "#....",
            "#....",
            "#....",
            "#...#",
            ".###.",
            // 'D'
            "###..",
            "#..#.",
            "#...#",
            "#...#",
            "#...#",
            "#..#.",
            "###..",
            // 'E'
            "#####",
            "#....",
            "#....",
            "####.",
            "#....",
            "#....",
            "#####",
            // 'F'
            "#####",
            "#....",
            "#....",
            "####.",
            "#....",
            "#....",
            "#....",
            // 'G'
            ".###.",
            "#...#",
            "#....",
            "#.###",
            "#...#",
            "#...#",
            ".####",
            // 'H'
            "#...#",
            "#...#",
            "#...#",
            "#####",
            "#...#",
            "#...#",
            "#...#",
            // 'I'
            ".###.",
            "..#..",
            "..#..",
            "..#..",
            "..#..",
            "..#..",
            ".###.",
            // 'J'
            "..###",
            "...#.",
            "...#.",
            "...#.",
            "...#.",
            "#..#.",
            ".##..",
            // 'K'
            "#...#",
            "#..#.",
            "#.#..",
            "##...",
            "#.#..",
            "#..#.",
            "#...#",
            // 'L'
            "#....",
            "#....",
            "#....",
            "#....",
            "#....",
            "#....",
            "#####",
            // 'M'
            "#...#",
            "##.##",
            "#.#.#",
            "#.#.#",
            "#...#",
            "#...#",
            "#...#",
            // 'N'
            "#...#",
            "#...#",
            "##..#",
            "#.#.#",
            "#..##",
            "#...#",
            "#...#",
            // 'O'
            ".###.",
            "#...#",
            "#...#",
            "#...#",
            "#...#",
            "#...#",
            ".###.",
            // 'P'
            "####.",
            "#...#",
            "#...#",
            "####.",
            "#....",
            "#....",
            "#....",
            // 'Q'
            ".###.",
            "#...#",
            "#...#",
            "#...#",
            "#.#.#",
            "#..#.",
            ".##.#",
            // 'R'
            "####.",
            "#...#",
            "#...#",
            "####.",
            "#.#..",
            "#..#.",
            "#...#",
            // 'S'
            ".####",
            "#....",
            "#....",
            ".###.",
            "....#",
            "....#",
            "####.",
            // 'T'
            "#####",
            "..#..",
            "..#..",
            "..#..",
            "..#..",
            "..#..",
            "..#..",
            // 'U'
            "#...#",
            "#...#",
            "#...#",
            "#...#",
            "#...#",
            "#...#",
            ".###.",
            // 'V'
            "#...#",
            "#...#",
            "#...#",
            "#...#",
            "#...#",
            ".#.#.",
            "..#..",
            // 'W'
            "#...#",
            "#...#",
            "#...#",
            "#.#.#",
            "#.#.#",
            "##.##",
            "#...#",
            // 'X'
            "#...#",
            "#...#",
            ".#.#.",
            "..#..",
            ".#.#.",
            "#...#",
            "#...#",
            // 'Y'
            "#...#",
            "#...#",
            ".#.#.",
            "..#..",
            "..#..",
            "..#..",
            "..#..",
            // 'Z'
            "#####",
            "....#",
            "...#.",
            "..#..",
            ".#...",
            "#....",
            "#####",
            // '.'
            ".....",
            ".....",
            ".....",
            ".....",
            ".....",
            ".##..",
            ".##..",
            // U+00B7 middle dot — the HUD's token separator, so it sits on the optical centre
            ".....",
            ".....",
            ".....",
            ".##..",
            ".##..",
            ".....",
            ".....",
            // '%'
            "##..#",
            "##.#.",
            "...#.",
            "..#..",
            ".#...",
            "#..##",
            "...##",
            // '!'
            "..#..",
            "..#..",
            "..#..",
            "..#..",
            "..#..",
            ".....",
            "..#..",
            // '-'
            ".....",
            ".....",
            ".....",
            ".###.",
            ".....",
            ".....",
            ".....",
            // ':'
            ".....",
            ".##..",
            ".##..",
            ".....",
            ".##..",
            ".##..",
            ".....",
            // '/'
            "....#",
            "....#",
            "...#.",
            "..#..",
            ".#...",
            "#....",
            "#....",
            // '=' — not in the spec's charset list, but the spec's own legend says "X2 = 42 IDX".
            // The blank-on-unknown rule turned that into a silent hole in the first render.
            ".....",
            ".....",
            ".###.",
            ".....",
            ".###.",
            ".....",
            ".....",
        };

        // Packed at type-init: one byte per row, bit 4 = leftmost column. The draw loop then shifts
        // instead of indexing a string per pixel.
        static readonly byte[] Rows = Pack();

        // char -> glyph index. 256 entries covers the whole range a char below U+0100 can take,
        // which is every character in Charset; anything above falls through to space.
        static readonly byte[] Map = BuildMap();

        static byte[] Pack()
        {
            if (Art.Length != Charset.Length * GlyphH)
                throw new InvalidOperationException("BitmapFont: " + Art.Length + " art rows for "
                    + Charset.Length + " glyphs; expected " + (Charset.Length * GlyphH));

            byte[] rows = new byte[Art.Length];
            for (int i = 0; i < Art.Length; i++)
            {
                string r = Art[i];
                if (r.Length != GlyphW)
                    throw new InvalidOperationException("BitmapFont: art row " + i + " is "
                        + r.Length + " wide, expected " + GlyphW);
                int bits = 0;
                for (int c = 0; c < GlyphW; c++)
                {
                    char ch = r[c];
                    if (ch == '#') bits |= 1 << (GlyphW - 1 - c);
                    else if (ch != '.')
                        throw new InvalidOperationException("BitmapFont: art row " + i
                            + " has '" + ch + "'; only '#' and '.' are legal");
                }
                rows[i] = (byte)bits;
            }
            return rows;
        }

        static byte[] BuildMap()
        {
            byte[] m = new byte[256];
            for (int i = 0; i < Charset.Length; i++)
            {
                char c = Charset[i];
                m[c] = (byte)i;
                // Fold lowercase onto the same glyph. The charset is uppercase-only by design, but
                // a caller that formats "ms" instead of "MS" should get text, not two holes it
                // only notices in a screenshot.
                if (c >= 'A' && c <= 'Z') m[c - 'A' + 'a'] = (byte)i;
            }
            return m;
        }

        /// Draws s at (x, y) — y is the TOP of the glyph box — and returns the advance in px,
        /// i.e. the x offset of the next character, including the 1 px gap after the last glyph.
        /// Clips to the buffer; never allocates; never throws on an unknown character.
        public static int Draw(int[] px, int w, int h, int x, int y, string s, uint colour, int scale)
        {
            if (s == null) return 0;
            if (scale < 1) scale = 1;
            int step = Advance * scale;
            int ink = unchecked((int)colour);

            for (int i = 0; i < s.Length; i++)
            {
                int gx = x + i * scale * Advance;
                int gi = Index(s[i]);
                if (gi == 0) continue;                                   // space, or unknown
                if (px == null || gx >= w || gx + GlyphW * scale <= 0) continue;
                if (y >= h || y + GlyphH * scale <= 0) continue;

                int b = gi * GlyphH;
                for (int r = 0; r < GlyphH; r++)
                {
                    int bits = Rows[b + r];
                    if (bits == 0) continue;
                    for (int c = 0; c < GlyphW; c++)
                    {
                        if (((bits >> (GlyphW - 1 - c)) & 1) == 0) continue;
                        Block(px, w, h, gx + c * scale, y + r * scale, scale, ink);
                    }
                }
            }
            return s.Length * step;
        }

        /// Width of s in px, on the same definition as Draw's return value. Right-aligning to X
        /// means drawing at X - Measure(s, scale) + scale, because the last px of the advance is
        /// the letter gap, not ink.
        public static int Measure(string s, int scale)
        {
            if (s == null) return 0;
            if (scale < 1) scale = 1;
            return s.Length * Advance * scale;
        }

        static int Index(char c)
        {
            return c < 256 ? Map[c] : 0;
        }

        // One glyph pixel = a scale x scale block. At scale 1 this is a single store, and the
        // bounds test is per-pixel because a HUD line that runs off a narrow panel must clip, not
        // wrap onto the next row of the raster.
        static void Block(int[] px, int w, int h, int x, int y, int scale, int colour)
        {
            for (int dy = 0; dy < scale; dy++)
            {
                int yy = y + dy;
                if (yy < 0 || yy >= h) continue;
                int row = yy * w;
                for (int dx = 0; dx < scale; dx++)
                {
                    int xx = x + dx;
                    if (xx < 0 || xx >= w) continue;
                    px[row + xx] = colour;
                }
            }
        }
    }
}
