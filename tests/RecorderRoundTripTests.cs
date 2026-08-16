using System;
using System.Collections.Generic;
using SizeMap.Engine;
using Xunit;

namespace SizeMap.Tests
{
    // The corpus format is the one thing in this project that can never be regenerated: a day
    // recorded with a broken layout is a day lost. These tests pin the bytes, not the behaviour.
    public class RecorderRoundTripTests
    {
        [Fact]
        public void Record_IsExactly16Bytes_AtTheDocumentedOffsets()
        {
            byte[] b = new byte[RawFile.RecordBytes * 2];
            for (int i = 0; i < b.Length; i++) b[i] = 0xEE;

            RawRecord r = new RawRecord
            {
                DtMs = unchecked((int)0x01020304),
                Row = 0x05060708,
                Size = 0x090A0B0C,
                Kind = RawKind.EpochBreak,
                Pos = 7,
                Op = 2,
                Seq = 200
            };
            RawFile.Write(b, 0, r);

            Assert.Equal(16, RawFile.RecordBytes);
            // little-endian, offsets 0/4/8
            Assert.Equal(new byte[] { 0x04, 0x03, 0x02, 0x01 }, new[] { b[0], b[1], b[2], b[3] });
            Assert.Equal(new byte[] { 0x08, 0x07, 0x06, 0x05 }, new[] { b[4], b[5], b[6], b[7] });
            Assert.Equal(new byte[] { 0x0C, 0x0B, 0x0A, 0x09 }, new[] { b[8], b[9], b[10], b[11] });
            Assert.Equal((byte)RawKind.EpochBreak, b[12]);
            Assert.Equal((byte)7, b[13]);
            Assert.Equal((byte)2, b[14]);
            Assert.Equal((byte)200, b[15]);
            // and not one byte more
            for (int i = 16; i < b.Length; i++) Assert.Equal((byte)0xEE, b[i]);
        }

        [Fact]
        public void Header_IsExactly32Bytes_AtTheDocumentedOffsets()
        {
            byte[] b = new byte[RawFile.HeaderBytes + 4];
            for (int i = 0; i < b.Length; i++) b[i] = 0xEE;
            long t0 = new DateTime(2026, 8, 16, 9, 30, 0).Ticks;
            RawFile.WriteHeader(b, 0, t0, 0.25, RawFile.FlagReplay);

            Assert.Equal(32, RawFile.HeaderBytes);
            Assert.Equal("SMR1", "" + (char)b[0] + (char)b[1] + (char)b[2] + (char)b[3]);
            for (int i = RawFile.HeaderBytes; i < b.Length; i++) Assert.Equal((byte)0xEE, b[i]);

            RawHeader h;
            Assert.True(RawFile.TryReadHeader(b, 0, b.Length, out h));
            Assert.Equal(RawFile.Version, h.Version);
            Assert.Equal(RawFile.HeaderBytes, h.HeaderBytes);
            Assert.Equal(RawFile.RecordBytes, h.RecordBytes);
            Assert.Equal(t0, h.T0Ticks);
            Assert.Equal(0.25, h.TickSize);
            Assert.True(h.IsReplay);
            Assert.False(h.IsContinuation);
        }

        [Fact]
        public void TryReadHeader_RejectsForeignAndShortFiles()
        {
            RawHeader h;
            byte[] junk = new byte[RawFile.HeaderBytes];
            Assert.False(RawFile.TryReadHeader(junk, 0, junk.Length, out h));

            byte[] ok = new byte[RawFile.HeaderBytes];
            RawFile.WriteHeader(ok, 0, 1000, 0.25, 0);
            Assert.False(RawFile.TryReadHeader(ok, 0, RawFile.HeaderBytes - 1, out h));
            Assert.True(RawFile.TryReadHeader(ok, 0, ok.Length, out h));
        }

        // A synthetic session written exactly as the recorder writes it, then read back.
        [Fact]
        public void SyntheticStream_RoundTrips_EveryFieldOfEveryRecord()
        {
            const int n = 50000;
            RawRecord[] src = Synthesize(n, 4242);

            byte[] file = new byte[RawFile.HeaderBytes + n * RawFile.RecordBytes];
            long t0 = new DateTime(2026, 8, 16, 9, 30, 0).Ticks;
            RawFile.WriteHeader(file, 0, t0, 0.25, 0);
            for (int i = 0; i < n; i++)
                RawFile.Write(file, RawFile.HeaderBytes + i * RawFile.RecordBytes, src[i]);

            RawHeader h;
            RawRecord[] back = new RawRecord[n];
            Assert.Equal(n, RawFile.ReadAll(file, out h, back));
            Assert.Equal(t0, h.T0Ticks);
            for (int i = 0; i < n; i++)
            {
                Assert.Equal(src[i].DtMs, back[i].DtMs);
                Assert.Equal(src[i].Row, back[i].Row);
                Assert.Equal(src[i].Size, back[i].Size);
                Assert.Equal(src[i].Kind, back[i].Kind);
                Assert.Equal(src[i].Pos, back[i].Pos);
                Assert.Equal(src[i].Op, back[i].Op);
                Assert.Equal(src[i].Seq, back[i].Seq);
            }
        }

        // NT8 killed mid-write leaves a partial record. Everything before it is still corpus.
        [Theory]
        [InlineData(1)]
        [InlineData(7)]
        [InlineData(15)]
        public void TruncatedTail_YieldsTheRecordsBeforeIt_AndDoesNotThrow(int tailBytes)
        {
            const int n = 100;
            RawRecord[] src = Synthesize(n, 7);
            byte[] file = new byte[RawFile.HeaderBytes + n * RawFile.RecordBytes + tailBytes];
            RawFile.WriteHeader(file, 0, 1_000_000L, 0.25, 0);
            for (int i = 0; i < n; i++)
                RawFile.Write(file, RawFile.HeaderBytes + i * RawFile.RecordBytes, src[i]);

            RawHeader h;
            RawRecord[] back = new RawRecord[n + 4];
            Assert.Equal(n, RawFile.ReadAll(file, out h, back));
            Assert.Equal(src[n - 1].Row, back[n - 1].Row);
        }

        [Fact]
        public void EpochBreak_CarriesTheNextEpochT0_Exactly()
        {
            // A ticks value big enough to need all 60 bits, and one with the low word's sign bit set.
            long[] cases = { new DateTime(2026, 8, 16, 14, 3, 27, 123).Ticks, 0x7FFFFFFF_FFFFFFFFL >> 3, 0xFFFFFFFFL, 1 };
            foreach (long t0 in cases)
            {
                RawRecord r = RawFile.MakeEpochBreak(-1234, t0);
                byte[] b = new byte[RawFile.RecordBytes];
                RawFile.Write(b, 0, r);
                RawRecord back = RawFile.Read(b, 0);
                Assert.Equal(RawKind.EpochBreak, back.Kind);
                Assert.Equal(-1234, back.DtMs);           // negative deltas survive: a rewind is legal
                Assert.Equal(t0, RawFile.EpochT0Of(back));
            }
        }

        // The writer splits its file at this offset. An off-by-one on the stride welds two tape
        // epochs into one timeline — the exact corruption the epoch record exists to prevent.
        [Fact]
        public void FindEpochBreak_FindsTheFirstOne_OnTheRecordStrideOnly()
        {
            byte[] buf = new byte[RawFile.RecordBytes * 6];
            for (int i = 0; i < 6; i++)
                RawFile.Write(buf, i * RawFile.RecordBytes,
                    new RawRecord { Kind = RawKind.DepthBid, Row = 100 + i });

            Assert.Equal(-1, RawFile.FindEpochBreak(buf, 0, buf.Length));

            RawFile.Write(buf, 2 * RawFile.RecordBytes, RawFile.MakeEpochBreak(0, 12345));
            RawFile.Write(buf, 5 * RawFile.RecordBytes, RawFile.MakeEpochBreak(0, 6789));
            Assert.Equal(2 * RawFile.RecordBytes, RawFile.FindEpochBreak(buf, 0, buf.Length));
            Assert.Equal(5 * RawFile.RecordBytes, RawFile.FindEpochBreak(buf, 3 * RawFile.RecordBytes, buf.Length));
            Assert.Equal(-1, RawFile.FindEpochBreak(buf, 0, 2 * RawFile.RecordBytes));

            // a body byte that happens to equal EpochBreak must not be mistaken for the kind byte
            byte[] decoy = new byte[RawFile.RecordBytes];
            RawFile.Write(decoy, 0, new RawRecord { Kind = RawKind.DepthAsk, Row = (int)RawKind.EpochBreak, Size = (int)RawKind.EpochBreak });
            Assert.Equal(-1, RawFile.FindEpochBreak(decoy, 0, decoy.Length));
        }

        [Fact]
        public void TimeOf_InvertsMsSince()
        {
            DateTime t0 = new DateTime(2026, 8, 16, 9, 30, 0);
            DateTime t = t0.AddMilliseconds(3_600_123);
            Assert.Equal(3_600_123, RawFile.MsSince(t0.Ticks, t));
            Assert.Equal(t, RawFile.TimeOf(t0.Ticks, RawFile.MsSince(t0.Ticks, t)));
        }

        // The whole point of the bounded queue: a stalled drain must cost records, never the
        // depth thread and never the heap.
        [Fact]
        public void Queue_DropsWhenTheDrainStalls_AndNeverGrows()
        {
            var q = new RawEventQueue(64);
            Assert.Equal(63, q.Capacity);

            for (int i = 0; i < q.Capacity + 10; i++)
                q.Enqueue(new RawRecord { Row = i, Kind = RawKind.DepthBid });

            Assert.Equal(10L, q.Dropped);
            Assert.Equal(q.Capacity, q.Count);

            // and what survived is the OLDEST run, contiguous, in order
            var got = DrainAll(q);
            Assert.Equal(q.Capacity, got.Count);
            for (int i = 0; i < got.Count; i++) Assert.Equal(i, got[i].Row);
            Assert.Equal(0, q.Count);
        }

        [Fact]
        public void Queue_StampsSeq_SoADropLeavesAGapInTheFile()
        {
            var q = new RawEventQueue(8);          // capacity 7
            for (int i = 0; i < 10; i++) q.Enqueue(new RawRecord { Row = i });   // 3 dropped
            var before = DrainAll(q);
            q.Enqueue(new RawRecord { Row = 99 });
            var after = DrainAll(q);

            Assert.Equal(3L, q.Dropped);
            Assert.Equal((byte)6, before[before.Count - 1].Seq);      // records 0..6 kept
            Assert.Equal((byte)10, after[0].Seq);                     // 7,8,9 dropped -> gap of 3
        }

        [Fact]
        public void Queue_WrapsWithoutLosingOrDuplicating()
        {
            var q = new RawEventQueue(16);
            int next = 0, drained = 0;
            for (int round = 0; round < 50; round++)
            {
                for (int i = 0; i < 10; i++) Assert.True(q.Enqueue(new RawRecord { Row = next++ }));
                foreach (var r in DrainAll(q)) Assert.Equal(drained++, r.Row);
            }
            Assert.Equal(500, drained);
            Assert.Equal(0L, q.Dropped);
        }

        [Fact]
        public void Queue_DrainRespectsASmallDestination()
        {
            var q = new RawEventQueue(64);
            for (int i = 0; i < 40; i++) q.Enqueue(new RawRecord { Row = i });
            byte[] dest = new byte[RawFile.RecordBytes * 3];
            int bytes = q.Drain(dest, 0, dest.Length);
            Assert.Equal(RawFile.RecordBytes * 3, bytes);
            Assert.Equal(37, q.Count);
        }

        [Fact]
        public void Queue_EnqueueAllocatesNothing()
        {
            var q = new RawEventQueue(1024);
            var rec = new RawRecord { Row = 1, Kind = RawKind.DepthAsk };
            byte[] sink = new byte[RawFile.RecordBytes * 512];
            q.Enqueue(rec); q.Drain(sink, 0, sink.Length);       // JIT warm-up

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100000; i++)
            {
                q.Enqueue(rec);
                if ((i & 511) == 511) while (q.Drain(sink, 0, sink.Length) > 0) { }
            }
            Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // One producer, one consumer, no lock: every record must come out exactly once, in order.
        [Fact]
        public async System.Threading.Tasks.Task Queue_SingleProducerSingleConsumer_LosesNothingItDidNotCount()
        {
            var q = new RawEventQueue(256);
            const int n = 200000;
            var seen = new List<int>(n);

            var writer = System.Threading.Tasks.Task.Run(() =>
            {
                for (int i = 0; i < n; i++) q.Enqueue(new RawRecord { Row = i });
            });

            byte[] buf = new byte[RawFile.RecordBytes * 256];
            while (!writer.IsCompleted || q.Count > 0)
            {
                int bytes;
                while ((bytes = q.Drain(buf, 0, buf.Length)) > 0)
                    for (int o = 0; o < bytes; o += RawFile.RecordBytes)
                        seen.Add(RawFile.Read(buf, o).Row);
            }
            await writer;   // surfaces a producer-side exception instead of hiding it

            Assert.Equal(n, seen.Count + (int)q.Dropped);
            for (int i = 1; i < seen.Count; i++) Assert.True(seen[i] > seen[i - 1], "out of order at " + i);
        }

        private static List<RawRecord> DrainAll(RawEventQueue q)
        {
            var outp = new List<RawRecord>();
            byte[] buf = new byte[RawFile.RecordBytes * 512];
            int bytes;
            while ((bytes = q.Drain(buf, 0, buf.Length)) > 0)
                for (int o = 0; o < bytes; o += RawFile.RecordBytes)
                    outp.Add(RawFile.Read(buf, o));
            return outp;
        }

        private static RawRecord[] Synthesize(int n, int seed)
        {
            var rnd = new Random(seed);
            var recs = new RawRecord[n];
            int dt = 0, row = 88000;   // NQ 22000.00 at 0.25 -> row 88000
            for (int i = 0; i < n; i++)
            {
                dt += rnd.Next(0, 25);
                row += rnd.Next(-8, 9);
                var kind = (RawKind)rnd.Next(0, 7);
                bool depth = kind == RawKind.DepthBid || kind == RawKind.DepthAsk;
                recs[i] = new RawRecord
                {
                    DtMs = dt,
                    Row = row,
                    Size = rnd.Next(0, 5000),
                    Kind = kind,
                    Pos = depth ? (byte)rnd.Next(0, 40) : RawFile.NA,
                    Op = depth ? (byte)rnd.Next(0, 3) : RawFile.NA,
                    Seq = (byte)i
                };
            }
            return recs;
        }
    }
}
