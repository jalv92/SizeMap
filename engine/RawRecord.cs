using System;

// The raw depth/tape corpus — the format, the codec and the producer-side queue.
//
// Depth has no history. OnMarketDepth never fires on historical bars, so every threshold
// in this project (K_mult, MinAbsSize, T_persist, F_flicker, rampMax) can only ever be
// calibrated against tape that was recorded while it happened. A day the writer did not
// run is a day of corpus that will never exist.
//
// SIZE, from the measured rate. ES at 10 levels runs ~200 depth events/s; NQ peaks around
// 400. At 16 B/record that is 3.2-6.4 KB/s = 11-23 MB/h, so a 6.5 h RTH day is 75-150 MB and
// a 23 h globex day is 265-530 MB. Hence MaxBytesPerDay = 512 MB: one full day of the worst
// realistic tape fits, and a runaway feed cannot silently eat the disk.
//
// Fixed-width and packed on purpose: numpy reads the body with
//     np.fromfile(f, dtype=np.dtype([('dtMs','<i4'),('row','<i4'),('size','<i4'),
//                                    ('kind','u1'),('pos','u1'),('op','u1'),('seq','u1')]),
//                 offset=32)
// with no parser on either side.
//
// ===========================  FILE LAYOUT — sm-raw-<INSTRUMENT>-<yyyyMMdd>[-partN].smr
//
// Header, 32 bytes, little-endian:
//   off  size  type    field
//     0     4  ascii   magic "SMR1"
//     4     2  ushort  version = 1
//     6     2  ushort  headerBytes = 32
//     8     8  long    t0Ticks     — DateTime.Ticks of this file's time origin (MARKET time)
//    16     8  double  tickSize
//    24     4  int     recordBytes = 16
//    28     4  int     flags — bit0 Market Replay, bit1 this file is a continuation part
//
// Record, 16 bytes, little-endian:
//   off  size  type    field
//     0     4  int     dtMs — ms since the CURRENT epoch's t0 (see EpochBreak). Signed:
//                             a sub-2s out-of-order feed tick is legitimately negative.
//     4     4  int     row  — absolute price-tick index, round(price / tickSize)
//     8     4  int     size — resting size (depth) or print size (trade)
//    12     1  byte    kind — RawKind
//    13     1  byte    pos  — ladder position 0..254, 255 = n/a
//    14     1  byte    op   — 0 Add, 1 Update, 2 Remove, 255 = n/a
//    15     1  byte    seq  — low 8 bits of a monotonic counter. Stamped BEFORE the
//                             queue-full test, so a dropped event leaves a seq gap in the
//                             file: drops are detectable from the corpus alone.
//
// An EpochBreak record ends an epoch and carries the NEXT epoch's t0Ticks in its
// row/size fields (low 32 / high 32). Everything after it is relative to that new t0.
// The file is therefore self-describing even if the part-file split is ever removed.

namespace SizeMap.Engine
{
    public enum RawKind : byte
    {
        DepthBid = 0, DepthAsk = 1, TradeBuy = 2, TradeSell = 3,
        FeedReset = 4, EpochBreak = 5, Heartbeat = 6
    }

    public struct RawRecord
    {
        public int DtMs;
        public int Row;
        public int Size;
        public RawKind Kind;
        public byte Pos;
        public byte Op;
        public byte Seq;
    }

    public struct RawHeader
    {
        public int Version;
        public int HeaderBytes;
        public int RecordBytes;
        public int Flags;
        public long T0Ticks;
        public double TickSize;

        public bool IsReplay { get { return (Flags & RawFile.FlagReplay) != 0; } }
        public bool IsContinuation { get { return (Flags & RawFile.FlagContinuation) != 0; } }
    }

    // Pure codec. No System.IO: the caller owns the bytes, so the same code serves the
    // NinjaTrader writer, the headless harness and any future analysis tool.
    public static class RawFile
    {
        public const int HeaderBytes = 32;
        public const int RecordBytes = 16;
        public const int Version = 1;

        public const byte NA = 255;              // pos / op: not applicable to this kind
        public const int FlagReplay = 1;
        public const int FlagContinuation = 2;

        public const string Extension = ".smr";

        // "SMR1"
        private const byte M0 = (byte)'S', M1 = (byte)'M', M2 = (byte)'R', M3 = (byte)'1';

        public static void WriteHeader(byte[] dst, int off, long t0Ticks, double tickSize, int flags)
        {
            if (dst == null) throw new ArgumentNullException("dst");
            if (off < 0 || off + HeaderBytes > dst.Length) throw new ArgumentOutOfRangeException("off");
            dst[off] = M0; dst[off + 1] = M1; dst[off + 2] = M2; dst[off + 3] = M3;
            U16(dst, off + 4, Version);
            U16(dst, off + 6, HeaderBytes);
            I64(dst, off + 8, t0Ticks);
            I64(dst, off + 16, BitConverter.DoubleToInt64Bits(tickSize));
            I32(dst, off + 24, RecordBytes);
            I32(dst, off + 28, flags);
        }

        public static bool TryReadHeader(byte[] src, int off, int len, out RawHeader h)
        {
            h = new RawHeader();
            if (src == null || off < 0 || len < HeaderBytes || off + len > src.Length) return false;
            if (src[off] != M0 || src[off + 1] != M1 || src[off + 2] != M2 || src[off + 3] != M3) return false;
            h.Version = RdU16(src, off + 4);
            h.HeaderBytes = RdU16(src, off + 6);
            h.T0Ticks = RdI64(src, off + 8);
            h.TickSize = BitConverter.Int64BitsToDouble(RdI64(src, off + 16));
            h.RecordBytes = RdI32(src, off + 24);
            h.Flags = RdI32(src, off + 28);
            // A future version may extend the header; it may never move a field or change the
            // record width, because that is what makes the body readable without a parser.
            return h.Version == Version && h.HeaderBytes >= HeaderBytes && h.RecordBytes == RecordBytes;
        }

        public static void Write(byte[] dst, int off, RawRecord r)
        {
            I32(dst, off, r.DtMs);
            I32(dst, off + 4, r.Row);
            I32(dst, off + 8, r.Size);
            dst[off + 12] = (byte)r.Kind;
            dst[off + 13] = r.Pos;
            dst[off + 14] = r.Op;
            dst[off + 15] = r.Seq;
        }

        public static RawRecord Read(byte[] src, int off)
        {
            RawRecord r = new RawRecord();
            r.DtMs = RdI32(src, off);
            r.Row = RdI32(src, off + 4);
            r.Size = RdI32(src, off + 8);
            r.Kind = (RawKind)src[off + 12];
            r.Pos = src[off + 13];
            r.Op = src[off + 14];
            r.Seq = src[off + 15];
            return r;
        }

        // Decodes whole records only. A process killed mid-write leaves a partial tail;
        // it is silently ignored rather than throwing, because the 4 999 records before it
        // are still a valid corpus. Returns how many were decoded.
        public static int ReadRecords(byte[] src, int off, int len, RawRecord[] into, int intoOff)
        {
            if (src == null || into == null || len <= 0) return 0;
            int n = len / RecordBytes;
            if (n > into.Length - intoOff) n = into.Length - intoOff;
            for (int i = 0; i < n; i++) into[intoOff + i] = Read(src, off + i * RecordBytes);
            return n;
        }

        // Convenience for whole-file consumers: header + body in one call. `records` may be
        // null on a header-only probe. Returns the record count, -1 if the header is not ours.
        public static int ReadAll(byte[] file, out RawHeader h, RawRecord[] records)
        {
            if (!TryReadHeader(file, 0, file == null ? 0 : file.Length, out h)) return -1;
            if (records == null) return (file.Length - h.HeaderBytes) / RecordBytes;
            return ReadRecords(file, h.HeaderBytes, file.Length - h.HeaderBytes, records, 0);
        }

        public static RawRecord MakeEpochBreak(int dtMs, long nextT0Ticks)
        {
            RawRecord r = new RawRecord();
            r.DtMs = dtMs;
            r.Kind = RawKind.EpochBreak;
            r.Row = (int)(nextT0Ticks & 0xFFFFFFFFL);
            r.Size = (int)(nextT0Ticks >> 32);
            r.Pos = NA;
            r.Op = NA;
            return r;
        }

        // Offset of the first EpochBreak record in [off,end), or -1. The writer splits its file
        // here, so an off-by-one on the stride silently welds two tape epochs together — which
        // is the exact failure the epoch record exists to prevent. Lives in the engine, and is
        // tested, for that reason.
        public static int FindEpochBreak(byte[] buf, int off, int end)
        {
            for (int p = off; p + RecordBytes <= end; p += RecordBytes)
                if (buf[p + 12] == (byte)RawKind.EpochBreak) return p;
            return -1;
        }

        public static long EpochT0Of(RawRecord r)
        {
            return ((long)r.Size << 32) | (uint)r.Row;
        }

        public static DateTime TimeOf(long t0Ticks, int dtMs)
        {
            return new DateTime(t0Ticks + dtMs * TimeSpan.TicksPerMillisecond);
        }

        public static int MsSince(long t0Ticks, DateTime t)
        {
            return (int)((t.Ticks - t0Ticks) / TimeSpan.TicksPerMillisecond);
        }

        private static void U16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        private static void I32(byte[] b, int o, int v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }
        private static void I64(byte[] b, int o, long v)
        {
            I32(b, o, (int)v); I32(b, o + 4, (int)(v >> 32));
        }
        private static int RdU16(byte[] b, int o) { return b[o] | (b[o + 1] << 8); }
        private static int RdI32(byte[] b, int o)
        {
            return b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
        }
        private static long RdI64(byte[] b, int o)
        {
            return (uint)RdI32(b, o) | ((long)RdI32(b, o + 4) << 32);
        }
    }

    // Single producer (NT8's per-instrument depth thread), single consumer (the drain task).
    // Records are packed on the producer side, so the consumer's whole job is one
    // Buffer.BlockCopy and one FileStream.Write — no second encode pass, no staging array.
    //
    // FULL DROPS. It never blocks and never grows. The producer runs on a thread shared by
    // every chart, DOM and strategy on that instrument; making it wait on a disk write would
    // degrade the whole platform, and an unbounded queue turns a stalled disk into an OOM.
    // A drop is visible twice: Dropped counts them, and the seq byte gaps in the file.
    public sealed class RawEventQueue
    {
        private readonly byte[] _buf;
        private readonly int _slots;

        private volatile int _write;   // producer-owned; the volatile write publishes the record
        private volatile int _read;    // consumer-owned
        private long _dropped;         // producer-only, read for the HUD; a torn read on x64 is not a thing
        private byte _seq;

        public RawEventQueue(int slots)
        {
            if (slots < 2) slots = 2;
            _slots = slots;
            _buf = new byte[slots * RawFile.RecordBytes];
        }

        public long Dropped { get { return _dropped; } }
        public int Capacity { get { return _slots - 1; } }   // one slot separates full from empty

        public int Count
        {
            get { int w = _write, r = _read; return w >= r ? w - r : _slots - r + w; }
        }

        // Zero allocation, ~20 ns. Returns false when the record was dropped.
        public bool Enqueue(RawRecord rec)
        {
            rec.Seq = _seq++;   // before the full test on purpose: the gap IS the drop record
            int w = _write;
            int next = w + 1; if (next == _slots) next = 0;
            if (next == _read) { _dropped++; return false; }
            RawFile.Write(_buf, w * RawFile.RecordBytes, rec);
            _write = next;
            return true;
        }

        // Copies out one contiguous run. Returns bytes written into dest; call again until 0.
        public int Drain(byte[] dest, int destOff, int maxBytes)
        {
            int r = _read, w = _write;
            if (r == w) return 0;
            int slots = (w > r ? w : _slots) - r;           // stop at the wrap, the caller loops
            int cap = maxBytes / RawFile.RecordBytes;
            if (cap <= 0) return 0;
            if (slots > cap) slots = cap;
            int bytes = slots * RawFile.RecordBytes;
            Buffer.BlockCopy(_buf, r * RawFile.RecordBytes, dest, destOff, bytes);
            int nr = r + slots; if (nr == _slots) nr = 0;
            _read = nr;
            return bytes;
        }
    }
}
