using System;
using SizeMap.Engine;
using Xunit;

namespace SizeMap.Tests
{
    public class RasterizerTests
    {
        const long BT = ColumnRing.BucketTicks;
        const long B0 = 1000 * BT;
        const uint Bg = 0xFF1F1F1F;

        static Palette Pal()
        {
            var p = new Palette();
            p.Rebuild(Bg);
            p.SetScale(8, 320);
            return p;
        }

        // One published column at B0, then the head opened at B0+BT so nothing else is visible.
        static ColumnRing OneColumn(params int[] rowSideSize)
        {
            var r = new ColumnRing(8, 8);
            for (int i = 0; i < rowSideSize.Length; i += 3)
                r.Accumulate(B0, rowSideSize[i], (Side)rowSideSize[i + 1], rowSideSize[i + 2]);
            r.Accumulate(B0 + BT, 0, Side.Ask, 1);
            return r;
        }

        static int At(int[] px, int w, int x, int y)
        {
            return px[y * w + x];
        }

        [Fact]
        public void AnEmptyRingIsExactlyTheBackground()
        {
            var p = Pal();
            var px = new int[10 * 10];
            Rasterizer.Rasterize(new ColumnRing(8, 8), px, 10, 10, new RasterView(100, 5f, 1f, B0, 10f, 4f), p);
            for (int i = 0; i < px.Length; i++)
                Assert.Equal(unchecked((int)Bg), px[i]);
        }

        [Fact]
        public void AKnownRingProducesKnownPixels()
        {
            var p = Pal();
            var ring = OneColumn(100, (int)Side.Ask, 50);
            var px = new int[10 * 10];

            // NowX 10 with 4 px per bucket: the column at back=0 owns x [6,10).
            // AnchorY 5 at row 100 with 1 px per tick: row 100 owns y [4,5), row 101 y [3,4).
            Rasterizer.Rasterize(ring, px, 10, 10, new RasterView(100, 5f, 1f, B0, 10f, 4f), p);

            int heat = unchecked((int)p.ColourOf(50));
            int env = unchecked((int)Palette.Envelope);
            int bg = unchecked((int)Bg);

            for (int x = 6; x < 10; x++)
            {
                Assert.Equal(heat, At(px, 10, x, 4));   // the level
                Assert.Equal(env, At(px, 10, x, 3));    // the top of what the feed could see
            }
            Assert.Equal(bg, At(px, 10, 5, 4));         // outside the column
            Assert.Equal(bg, At(px, 10, 6, 5));         // outside the tick
            Assert.NotEqual(bg, heat);
        }

        [Fact]
        public void HeatUsesTheLargerSideWhenARowIsBothBidAndAsk()
        {
            var p = Pal();
            var ring = OneColumn(100, (int)Side.Bid, 40, 100, (int)Side.Ask, 300);
            var px = new int[10 * 10];
            Rasterizer.Rasterize(ring, px, 10, 10, new RasterView(100, 5f, 1f, B0, 10f, 4f), p);
            Assert.Equal(unchecked((int)p.ColourOf(300)), At(px, 10, 7, 4));
        }

        [Fact]
        public void SeveralTicksFoldingIntoOneRowMergeByMax()
        {
            var p = Pal();
            var ring = OneColumn(100, (int)Side.Ask, 10, 101, (int)Side.Ask, 500);
            var px = new int[10 * 10];

            // 0.25 px per tick: rows 100 and 101 both land on y = 4.
            Rasterizer.Rasterize(ring, px, 10, 10, new RasterView(100, 5f, 0.25f, B0, 10f, 4f), p);

            Assert.Equal(unchecked((int)p.ColourOf(500)), At(px, 10, 7, 4));
            Assert.NotEqual(unchecked((int)p.ColourOf(10)), unchecked((int)p.ColourOf(500)));
        }

        [Fact]
        public void SeveralColumnsFoldingIntoOnePixelMergeByMax()
        {
            var p = Pal();
            var r = new ColumnRing(8, 8);
            r.Accumulate(B0, 100, Side.Ask, 500);            // older, big
            r.Accumulate(B0 + BT, 100, Side.Ask, 10);        // newer, small
            r.Accumulate(B0 + 2 * BT, 0, Side.Ask, 1);       // publish both

            var px = new int[10 * 10];
            // 0.4 px per bucket: both columns land on x = 9.
            Rasterizer.Rasterize(r, px, 10, 10, new RasterView(100, 5f, 1f, B0 + BT, 10f, 0.4f), p);

            Assert.Equal(unchecked((int)p.ColourOf(500)), At(px, 10, 9, 4));
        }

        [Fact]
        public void AMeanWouldHideTheWallAndMaxDoesNot()
        {
            // The regression this project exists to prevent: a 500-lot wall visible for one bucket
            // inside a folded pixel column must survive the fold at full brightness.
            var p = Pal();
            var r = new ColumnRing(16, 8);
            for (int i = 0; i < 10; i++) r.Accumulate(B0 + i * BT, 100, Side.Ask, i == 4 ? 500 : 5);
            r.Accumulate(B0 + 10 * BT, 0, Side.Ask, 1);

            var px = new int[20 * 10];
            Rasterizer.Rasterize(r, px, 20, 10, new RasterView(100, 5f, 1f, B0 + 9 * BT, 20f, 0.5f), p);

            bool sawTheWall = false;
            for (int x = 0; x < 20; x++) if (At(px, 20, x, 4) == unchecked((int)p.ColourOf(500))) sawTheWall = true;
            Assert.True(sawTheWall, "the transient wall was averaged away by the column fold");
        }

        [Fact]
        public void OutOfRangeTicksDoNotWriteOutsideTheBuffer()
        {
            var p = Pal();
            var ring = OneColumn(
                100000, (int)Side.Ask, 400,      // far above the view
                -100000, (int)Side.Bid, 400,     // far below
                100, (int)Side.Ask, 50);         // and one that is inside

            const int w = 10, h = 10, guard = 8;
            var px = new int[w * h + guard];
            for (int i = w * h; i < px.Length; i++) px[i] = 0x5A5A5A5A;

            Rasterizer.Rasterize(ring, px, w, h, new RasterView(100, 5f, 1f, B0, 10f, 4f), p);

            for (int i = w * h; i < px.Length; i++)
                Assert.Equal(0x5A5A5A5A, px[i]);
            Assert.Equal(unchecked((int)p.ColourOf(50)), At(px, w, 7, 4));
        }

        [Fact]
        public void ColumnsOffTheLeftEdgeAreClippedNotWrapped()
        {
            var p = Pal();
            var r = new ColumnRing(64, 8);
            for (int i = 0; i < 40; i++) r.Accumulate(B0 + i * BT, 100, Side.Ask, 300);
            r.Accumulate(B0 + 40 * BT, 0, Side.Ask, 1);

            var px = new int[10 * 10];
            // 4 px per bucket over a 10 px panel: only the newest 3 columns can be on screen.
            Rasterizer.Rasterize(r, px, 10, 10, new RasterView(100, 5f, 1f, B0 + 39 * BT, 10f, 4f), p);

            int heat = unchecked((int)p.ColourOf(300));
            for (int x = 0; x < 10; x++) Assert.Equal(heat, At(px, 10, x, 4));
            for (int x = 0; x < 10; x++) Assert.NotEqual(heat, At(px, 10, x, 6));
        }

        [Fact]
        public void TheEnvelopeFramesTheObservedWindowAndNeverOverwritesHeat()
        {
            var p = Pal();
            var ring = OneColumn(
                102, (int)Side.Ask, 60,
                101, (int)Side.Ask, 60,
                100, (int)Side.Bid, 60,
                99, (int)Side.Bid, 60);

            var px = new int[12 * 12];
            Rasterizer.Rasterize(ring, px, 12, 12, new RasterView(100, 8f, 1f, B0, 12f, 4f), p);

            int env = unchecked((int)Palette.Envelope);
            int heat = unchecked((int)p.ColourOf(60));
            // rows 102..99 occupy y 5..8; the envelope sits one tick outside on each side.
            Assert.Equal(env, At(px, 12, 9, 4));    // above the top observed ask (row 103)
            Assert.Equal(env, At(px, 12, 9, 9));    // below the bottom observed bid (row 98)
            for (int y = 5; y <= 8; y++) Assert.Equal(heat, At(px, 12, 9, y));
        }

        [Fact]
        public void ARingThatWasResetDrawsNothing()
        {
            var p = Pal();
            var r = OneColumn(100, (int)Side.Ask, 400);
            r.Reset(B0);

            var px = new int[10 * 10];
            Rasterizer.Rasterize(r, px, 10, 10, new RasterView(100, 5f, 1f, B0, 10f, 4f), p);
            for (int i = 0; i < px.Length; i++) Assert.Equal(unchecked((int)Bg), px[i]);
        }

        [Fact]
        public void RasterizeRefusesABufferItWouldOverrun()
        {
            var p = Pal();
            var px = new int[10];
            Rasterizer.Rasterize(OneColumn(100, (int)Side.Ask, 50), px, 10, 10, new RasterView(100, 5f, 1f, B0, 10f, 4f), p);
            for (int i = 0; i < px.Length; i++) Assert.Equal(0, px[i]);   // untouched, not half-painted
        }
    }
}
