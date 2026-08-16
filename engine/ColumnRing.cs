using System;

namespace SizeMap.Engine
{
    // 8 bytes, no padding. Bid and ask live in ONE cell rather than under a sign convention,
    // because inside a single 250 ms bucket the same price genuinely is bid at the start and ask
    // at the end whenever the market crosses it — and that is exactly the moment a trader cares
    // about. A signed int cannot express both, and it also destroys the difference between
    // "observed, size 0" and "not observed", which the ring is not allowed to invent.
    public struct DepthCell
    {
        public int Row;      // absolute price-tick index
        public ushort Bid;   // resting bid size in this bucket, clamped to 65535
        public ushort Ask;
    }

    public struct ColumnHeader
    {
        public long StartTicks;   // DateTime.Ticks of the bucket start, MARKET time. 0 = never written.
        public int BestBidRow;    // ColumnRing.NoRow = unknown
        public int BestAskRow;
        public int Count;         // cells used, 0..CellsPerColumn
    }

    // The time x price x size history: fixed 250 ms columns, sparse cells, anchored in TICK space.
    //
    // Nothing in here is in pixels or bar indices, so scroll, zoom, panel resize and bar-type
    // change are all projection changes handled once per frame in the rasterizer. There is no
    // cached projection, therefore no invalidation path to get wrong.
    //
    // Threading: one writer (the depth thread) calls Accumulate/SetInside/Reset; any number of
    // readers call PublishedIndex/Header/CellAt. The writer only ever mutates the open head
    // column; a published column is immutable, so there is nothing to tear and the reader never
    // takes the depth thread's lock. Blocking the depth thread is the failure that matters: a
    // heatmap that stutters is ugly, a heatmap with holes in the data is a lie.
    //
    // ponytail: reader-overrun ceiling is Capacity columns (30 min) of reader lag — the writer
    // would wrap onto a column a reader is still walking. A frame is 250 ms. Upgrade path: a
    // per-column generation counter checked after the cell scan, if a frame ever takes minutes.
    public sealed class ColumnRing
    {
        public const int BucketMs = 250;                                       // == NT8's 4 fps repaint cap
        public const long BucketTicks = BucketMs * TimeSpan.TicksPerMillisecond;
        public const int NoRow = int.MinValue;

        readonly ColumnHeader[] _cols;
        readonly DepthCell[] _cells;
        readonly int _capacity;
        readonly int _cellsPerColumn;

        int _head;                 // writer only
        long _headBucket = -1;     // writer only; -1 = no column open yet
        int _published = -1;       // the ONLY cross-thread field
        long _overflows;
        long _lateDrops;

        // 7200 columns x 48 cells ~= 2.9 MB, allocated once, never resized, never allocating per
        // event. 40-level feeds want cellsPerColumn 96 (5.7 MB); both are nothing.
        public ColumnRing(int capacity = 7200, int cellsPerColumn = 48)
        {
            if (capacity < 2) capacity = 2;
            if (cellsPerColumn < 1) cellsPerColumn = 1;
            _capacity = capacity;
            _cellsPerColumn = cellsPerColumn;
            _cols = new ColumnHeader[capacity];
            _cells = new DepthCell[capacity * cellsPerColumn];
        }

        public int Capacity { get { return _capacity; } }
        public int CellsPerColumn { get { return _cellsPerColumn; } }

        // Newest FINISHED column. -1 = nothing to draw (start of session, or just after a Reset).
        public int PublishedIndex { get { return System.Threading.Volatile.Read(ref _published); } }
        public long Overflows { get { return System.Threading.Volatile.Read(ref _overflows); } }
        public long LateDrops { get { return System.Threading.Volatile.Read(ref _lateDrops); } }

        // Absolute price-tick index. NQ 25000 -> 100000, ES 6000 -> 24000: int is never close to
        // overflowing, and there is no rebasing, ever. That is the whole point of tick space.
        public static int RowFor(double price, double tickSize)
        {
            return (int)Math.Round(price / tickSize);
        }

        public static long BucketOf(long ticks)
        {
            return ticks / BucketTicks;
        }

        public ColumnHeader Header(int idx)
        {
            return _cols[idx];
        }

        public DepthCell CellAt(int col, int i)
        {
            return _cells[col * _cellsPerColumn + i];
        }

        // ---- depth thread only ----------------------------------------------------------------

        // Last value wins per (row, side) inside the bucket. Summing would turn a resting-liquidity
        // map into a volume map — a different, wrong product — and this store is the input to a
        // colour ramp that means "how much is resting here right now".
        public void Accumulate(long eventTicks, int row, Side side, long size)
        {
            long bucket = eventTicks / BucketTicks;
            if (_headBucket < 0) OpenHead(bucket);
            else if (bucket > _headBucket) Roll(bucket);
            else if (bucket < _headBucket)
            {
                // The column that would own this event is already published, and published columns
                // are immutable. Counted rather than hidden: a rising LateDrops means the feed is
                // delivering out of order and every other number here is suspect.
                _lateDrops++;
                return;
            }

            ushort v;
            if (size <= 0) v = 0;
            else if (size >= ushort.MaxValue) { v = ushort.MaxValue; _overflows++; }
            else v = (ushort)size;

            int b = _head * _cellsPerColumn;
            int n = _cols[_head].Count;
            for (int i = 0; i < n; i++)
            {
                if (_cells[b + i].Row != row) continue;
                if (side == Side.Bid) _cells[b + i].Bid = v; else _cells[b + i].Ask = v;
                return;
            }

            if (n < _cellsPerColumn)
            {
                _cells[b + n] = new DepthCell { Row = row, Bid = side == Side.Bid ? v : (ushort)0, Ask = side == Side.Ask ? v : (ushort)0 };
                _cols[_head].Count = n + 1;
                return;
            }

            // Full column: evict the smallest level, never the arriving one. A wall that appears
            // late in a busy bucket is precisely the value worth keeping.
            int worst = 0, worstSize = int.MaxValue;
            for (int i = 0; i < n; i++)
            {
                int m = _cells[b + i].Bid > _cells[b + i].Ask ? _cells[b + i].Bid : _cells[b + i].Ask;
                if (m >= worstSize) continue;
                worstSize = m; worst = i;
            }
            _cells[b + worst] = new DepthCell { Row = row, Bid = side == Side.Bid ? v : (ushort)0, Ask = side == Side.Ask ? v : (ushort)0 };
            _overflows++;
        }

        // Stored per column so the rasterizer can draw the inside market without re-deriving it,
        // and so a replayed recording is self-contained.
        public void SetInside(long eventTicks, int bestBidRow, int bestAskRow)
        {
            long bucket = eventTicks / BucketTicks;
            if (_headBucket < 0) OpenHead(bucket);
            else if (bucket > _headBucket) Roll(bucket);
            else if (bucket < _headBucket) { _lateDrops++; return; }
            _cols[_head].BestBidRow = bestBidRow;
            _cols[_head].BestAskRow = bestAskRow;
        }

        // Replay rewind / instrument switch. Publishes -1 FIRST, so a reader that lands mid-wipe
        // draws nothing — which is the truth after a rewind — and only then wipes.
        public void Reset(long nowTicks)
        {
            System.Threading.Volatile.Write(ref _published, -1);
            Array.Clear(_cols, 0, _cols.Length);
            Array.Clear(_cells, 0, _cells.Length);
            _head = 0;
            _headBucket = -1;
            _overflows = 0;
            _lateDrops = 0;
            if (nowTicks > 0) OpenHead(nowTicks / BucketTicks);
        }

        void OpenHead(long bucket)
        {
            _headBucket = bucket;
            _cols[_head].StartTicks = bucket * BucketTicks;
            _cols[_head].BestBidRow = NoRow;
            _cols[_head].BestAskRow = NoRow;
            _cols[_head].Count = 0;
        }

        // The one place _published moves. Volatile.Write is a release fence: every cell and header
        // write above it is visible to any thread that afterwards reads _published.
        void Roll(long newBucket)
        {
            System.Threading.Volatile.Write(ref _published, _head);
            _head = (_head + 1) % _capacity;
            OpenHead(newBucket);
        }
    }
}
