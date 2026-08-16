using System;
using SizeMap.Engine;
using Xunit;

namespace SizeMap.Tests
{
    public class ColumnRingTests
    {
        const long BT = ColumnRing.BucketTicks;
        const long B0 = 1000 * BT;   // an arbitrary absolute bucket boundary

        static int Size(ColumnRing r, int col, int row, Side side)
        {
            ColumnHeader h = r.Header(col);
            for (int i = 0; i < h.Count; i++)
            {
                DepthCell c = r.CellAt(col, i);
                if (c.Row == row) return side == Side.Bid ? c.Bid : c.Ask;
            }
            return -1;   // no cell: NOT observed, which is not the same as observed at size 0
        }

        [Fact]
        public void NothingIsPublishedUntilTheBucketCloses()
        {
            var r = new ColumnRing(8, 8);
            r.Accumulate(B0, 100, Side.Ask, 50);
            r.Accumulate(B0 + BT - 1, 101, Side.Ask, 60);
            Assert.Equal(-1, r.PublishedIndex);

            r.Accumulate(B0 + BT, 102, Side.Ask, 70);        // crosses the boundary
            Assert.Equal(0, r.PublishedIndex);
            Assert.Equal(B0, r.Header(0).StartTicks);
            Assert.Equal(2, r.Header(0).Count);
        }

        [Fact]
        public void BucketBoundariesAreAbsoluteNotRelativeToTheFirstEvent()
        {
            var r = new ColumnRing(8, 8);
            r.Accumulate(B0 + BT - 1, 100, Side.Ask, 50);    // arrives at the very end of a bucket
            r.Accumulate(B0 + BT, 100, Side.Ask, 60);
            Assert.Equal(B0, r.Header(0).StartTicks);        // the grid, not first-event + 250 ms
        }

        [Fact]
        public void LastValueWins()
        {
            var r = new ColumnRing(8, 8);
            r.Accumulate(B0, 100, Side.Ask, 500);
            r.Accumulate(B0 + 10, 100, Side.Ask, 900);
            r.Accumulate(B0 + 20, 100, Side.Ask, 12);        // the wall pulled inside the bucket
            r.Accumulate(B0 + BT, 100, Side.Ask, 1);

            Assert.Equal(1, r.Header(0).Count);              // one cell, not three
            Assert.Equal(12, Size(r, 0, 100, Side.Ask));     // not 900 (max) and not 1412 (sum)
        }

        [Fact]
        public void BidAndAskCoexistAtOneRowInOneBucket()
        {
            // The market crossed this price inside 250 ms. A sign convention could not say both.
            var r = new ColumnRing(8, 8);
            r.Accumulate(B0, 100, Side.Bid, 40);
            r.Accumulate(B0 + 5, 100, Side.Ask, 70);
            r.Accumulate(B0 + BT, 100, Side.Ask, 1);

            Assert.Equal(1, r.Header(0).Count);
            Assert.Equal(40, Size(r, 0, 100, Side.Bid));
            Assert.Equal(70, Size(r, 0, 100, Side.Ask));
        }

        [Fact]
        public void ObservedAtSizeZeroIsNotTheSameAsNotObserved()
        {
            var r = new ColumnRing(8, 8);
            r.Accumulate(B0, 100, Side.Ask, 0);              // level went empty: observed
            r.Accumulate(B0 + BT, 100, Side.Ask, 1);
            Assert.Equal(0, Size(r, 0, 100, Side.Ask));      // a cell exists
            Assert.Equal(-1, Size(r, 0, 999, Side.Ask));     // this one does not
        }

        [Fact]
        public void RowsAreAbsoluteTickSpaceAndAreNeverRebased()
        {
            // Anchoring in tick space is what makes the ring survive scroll and zoom: the same
            // price is the same row in every column of the session, on any chart.
            Assert.Equal(100000, ColumnRing.RowFor(25000.0, 0.25));
            Assert.Equal(100001, ColumnRing.RowFor(25000.25, 0.25));
            Assert.Equal(24000, ColumnRing.RowFor(6000.0, 0.25));

            var r = new ColumnRing(8, 8);
            r.Accumulate(B0, ColumnRing.RowFor(25000.25, 0.25), Side.Ask, 50);
            r.Accumulate(B0 + BT, ColumnRing.RowFor(25000.25, 0.25), Side.Ask, 50);
            r.Accumulate(B0 + 2 * BT, ColumnRing.RowFor(25000.25, 0.25), Side.Ask, 50);
            Assert.Equal(100001, r.CellAt(0, 0).Row);
            Assert.Equal(100001, r.CellAt(1, 0).Row);
        }

        [Fact]
        public void TheRingWrapsAndKeepsPublishingTheNewestColumn()
        {
            var r = new ColumnRing(4, 8);
            for (int i = 0; i < 10; i++) r.Accumulate(B0 + i * BT, 100 + i, Side.Ask, 10 + i);

            Assert.Equal(4, r.Capacity);
            Assert.Equal(0, r.PublishedIndex);                           // 9 rolls over 4 slots
            Assert.Equal(B0 + 8 * BT, r.Header(r.PublishedIndex).StartTicks);
            Assert.Equal(18, Size(r, r.PublishedIndex, 108, Side.Ask));  // the wrapped slot was reused, not appended
        }

        [Fact]
        public void QuietMarketsDoNotFabricateEmptyColumns()
        {
            // A 5-second gap in depth events must leave a HOLE in time, not 20 invented columns.
            var r = new ColumnRing(8, 8);
            r.Accumulate(B0, 100, Side.Ask, 50);
            r.Accumulate(B0 + 20 * BT, 100, Side.Ask, 50);
            Assert.Equal(0, r.PublishedIndex);
            Assert.Equal(B0, r.Header(0).StartTicks);
            Assert.Equal(B0 + 20 * BT, r.Header(1).StartTicks);  // the head jumped the gap
            Assert.Equal(0, r.Header(2).StartTicks);             // 0 == never written
        }

        [Fact]
        public void AWriteAfterThePublishIndexIsInvisibleToTheReader()
        {
            var r = new ColumnRing(8, 8);
            r.Accumulate(B0, 100, Side.Ask, 50);
            r.Accumulate(B0 + BT, 100, Side.Ask, 999);       // publishes column 0, opens column 1

            int p = r.PublishedIndex;                        // what a reader would latch
            r.Accumulate(B0 + BT + 10, 101, Side.Ask, 777);  // writer keeps working on the head

            Assert.Equal(0, p);
            Assert.Equal(50, Size(r, p, 100, Side.Ask));     // the published column did not move
            Assert.Equal(1, r.Header(p).Count);
            Assert.Equal(-1, Size(r, p, 101, Side.Ask));     // the head's new cell is not in it
        }

        [Fact]
        public void AnEventForAnAlreadyPublishedBucketIsDroppedAndCounted()
        {
            var r = new ColumnRing(8, 8);
            r.Accumulate(B0 + BT, 100, Side.Ask, 50);
            r.Accumulate(B0 + 2 * BT, 100, Side.Ask, 60);    // publishes the B0+BT column
            r.Accumulate(B0, 100, Side.Ask, 999);            // out of order, belongs to a closed column

            Assert.Equal(1, r.LateDrops);
            Assert.Equal(50, Size(r, r.PublishedIndex, 100, Side.Ask));
        }

        [Fact]
        public void ResetPublishesNothingFirstThenWipes()
        {
            var r = new ColumnRing(8, 8);
            r.Accumulate(B0, 100, Side.Ask, 50);
            r.Accumulate(B0 + BT, 100, Side.Ask, 60);
            Assert.Equal(0, r.PublishedIndex);

            r.Reset(B0 + 5 * BT);                            // replay rewind
            Assert.Equal(-1, r.PublishedIndex);              // a reader draws nothing: the truth
            Assert.Equal(0, r.LateDrops);

            r.Accumulate(B0 + 5 * BT, 100, Side.Ask, 7);
            r.Accumulate(B0 + 6 * BT, 100, Side.Ask, 8);
            Assert.Equal(0, r.PublishedIndex);
            Assert.Equal(B0 + 5 * BT, r.Header(0).StartTicks);
            Assert.Equal(7, Size(r, 0, 100, Side.Ask));
        }

        [Fact]
        public void ResetLetsThePastArriveAgainWithoutBeingCalledLate()
        {
            // Rewinding to an EARLIER point is the normal Playback move; it must not be read as
            // out-of-order delivery.
            var r = new ColumnRing(8, 8);
            r.Accumulate(B0 + 10 * BT, 100, Side.Ask, 50);
            r.Accumulate(B0 + 11 * BT, 100, Side.Ask, 50);
            r.Reset(B0);
            r.Accumulate(B0, 100, Side.Ask, 5);
            r.Accumulate(B0 + BT, 100, Side.Ask, 6);
            Assert.Equal(0, r.LateDrops);
            Assert.Equal(5, Size(r, 0, 100, Side.Ask));
        }

        [Fact]
        public void AFullColumnEvictsTheSmallestLevelNeverTheArrivingOne()
        {
            var r = new ColumnRing(4, 3);
            r.Accumulate(B0, 100, Side.Ask, 500);
            r.Accumulate(B0, 101, Side.Ask, 7);              // the smallest
            r.Accumulate(B0, 102, Side.Ask, 300);
            r.Accumulate(B0, 103, Side.Ask, 900);           // a wall arriving late in a busy bucket
            r.Accumulate(B0 + BT, 100, Side.Ask, 1);

            Assert.Equal(3, r.Header(0).Count);
            Assert.Equal(1, r.Overflows);
            Assert.Equal(900, Size(r, 0, 103, Side.Ask));
            Assert.Equal(-1, Size(r, 0, 101, Side.Ask));
            Assert.Equal(500, Size(r, 0, 100, Side.Ask));
        }

        [Fact]
        public void OversizedLevelsAreClampedAndCounted()
        {
            var r = new ColumnRing(4, 8);
            r.Accumulate(B0, 100, Side.Ask, 90000);
            r.Accumulate(B0 + BT, 100, Side.Ask, 1);
            Assert.Equal(ushort.MaxValue, Size(r, 0, 100, Side.Ask));
            Assert.Equal(1, r.Overflows);
        }

        [Fact]
        public void InsideMarketIsStoredPerColumn()
        {
            var r = new ColumnRing(4, 8);
            r.SetInside(B0, 99, 101);
            r.Accumulate(B0, 100, Side.Ask, 50);
            r.Accumulate(B0 + BT, 100, Side.Ask, 50);
            Assert.Equal(99, r.Header(0).BestBidRow);
            Assert.Equal(101, r.Header(0).BestAskRow);
            Assert.Equal(ColumnRing.NoRow, r.Header(r.PublishedIndex + 1).BestBidRow);
        }
    }
}
