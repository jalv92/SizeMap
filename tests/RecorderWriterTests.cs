using System;
using System.IO;
using SizeMap.Engine;
using NinjaTrader.NinjaScript.Indicators;
using Xunit;

// The one NinjaTrader symbol SizeMapRecorder touches. Stubbing it lets the file-rollover
// logic — the part that decides what a day of corpus looks like on disk — be tested off
// platform instead of only by staring at NT8.
// ponytail: a one-symbol stub. Ceiling — the day the recorder needs a second NT8 type this
// stops compiling. Upgrade path — stub that type too, or drop these tests and lose the
// rollover coverage entirely (the format tests in RecorderRoundTripTests would survive).
namespace NinjaTrader.Core { public static class Globals { public static string UserDataDir = "."; } }

namespace SizeMap.Tests
{
    public class RecorderWriterTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(),
            "sizemap-rec-" + Guid.NewGuid().ToString("N"));

        public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

        // One session, four hazards: a normal run, a Playback rewind, a date boundary and the
        // byte cap. Asserted on the files themselves, because the files are the deliverable.
        [Fact]
        public void Session_RollsAtEveryEpoch_AndNeverSplicesTwoTapeEpochs()
        {
            var rec = new SizeMapRecorder("NQ 09-26", 0.25, false, _dir, 1 << 16);
            rec.Start();

            DateTime t = new DateTime(2026, 8, 14, 9, 30, 0);
            for (int i = 0; i < 3000; i++)
                rec.OnDepth(new DepthEvent
                {
                    Side = i % 2 == 0 ? Side.Bid : Side.Ask,
                    Op = DepthOp.Update,
                    Position = i % 10,
                    Price = 22000.25 + (i % 10) * 0.25,
                    Volume = 100 + i % 400,
                    Time = t.AddMilliseconds(i * 20)          // 60 s of tape -> exactly one heartbeat
                });
            DateTime end = t.AddMilliseconds(3000 * 20);
            rec.OnTrade(new TradeEvent { Price = 22000.50, Volume = 7, Time = end }, true);

            // Playback slider dragged 5 minutes back. DataLoaded does NOT re-run, so only the
            // recorder's own clock guard can catch this.
            DateTime rewound = end.AddMinutes(-5);
            for (int i = 0; i < 500; i++)
                rec.OnDepth(new DepthEvent
                {
                    Side = Side.Bid, Op = DepthOp.Add, Position = 0, Price = 21990.00,
                    Volume = 300, Time = rewound.AddMilliseconds(i * 10)
                });

            rec.Stop();

            Assert.False(rec.Failed);
            Assert.Equal(0, rec.Dropped);

            Assert.Equal(2, Directory.GetFiles(_dir, "*.smr").Length);
            string f1 = Path.Combine(_dir, "sm-raw-NQ_09-26-20260814.smr");        // space sanitized out
            string f2 = Path.Combine(_dir, "sm-raw-NQ_09-26-20260814-part2.smr");
            Assert.True(File.Exists(f1));
            Assert.True(File.Exists(f2));

            RawHeader h1 = ReadHeader(f1);
            RawHeader h2 = ReadHeader(f2);
            Assert.Equal(t.Ticks, h1.T0Ticks);                              // part 1 keeps the FIRST epoch
            Assert.Equal(rewound.Ticks, h2.T0Ticks);                        // part 2 starts at the rewind
            Assert.False(h1.IsContinuation);
            Assert.True(h2.IsContinuation);
            Assert.False(h1.IsReplay);

            RawRecord[] p1 = ReadAll(f1), p2 = ReadAll(f2);
            Assert.Equal(3000 + 1 + 1 + 1, p1.Length);   // depth + trade + heartbeat + the break
            Assert.Equal(500, p2.Length);

            // The break is the LAST record of its file and nothing from the new epoch precedes it.
            Assert.Equal(RawKind.EpochBreak, p1[p1.Length - 1].Kind);
            Assert.Equal(rewound.Ticks, RawFile.EpochT0Of(p1[p1.Length - 1]));
            for (int i = 0; i < p1.Length - 1; i++) Assert.NotEqual(RawKind.EpochBreak, p1[i].Kind);
            foreach (RawRecord r in p2) Assert.NotEqual(RawKind.EpochBreak, r.Kind);

            // and the timeline inside each file is monotonic — the whole point of the split
            for (int i = 1; i < p1.Length - 1; i++) Assert.True(p1[i].DtMs >= p1[i - 1].DtMs);
            for (int i = 1; i < p2.Length; i++) Assert.True(p2[i].DtMs >= p2[i - 1].DtMs);

            Assert.Equal(1, Count(p1, RawKind.Heartbeat));
            Assert.Equal(1, Count(p1, RawKind.TradeBuy));
            Assert.Equal(88001, p1[0].Row);              // 22000.25 / 0.25, absolute tick space
        }

        [Fact]
        public void ByteCap_StopsTheDay_ThenLiftsAtTheNextOne()
        {
            var rec = new SizeMapRecorder("ES 09-26", 0.25, true, _dir, 1 << 16);
            rec.MaxBytesPerDay = 4096;
            rec.Start();

            DateTime day1 = new DateTime(2026, 8, 14, 10, 0, 0);
            for (int i = 0; i < 2000; i++)                      // 32 KB of records against a 4 KB cap
                rec.OnDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Update, Position = 0,
                                             Price = 5600.25, Volume = 50, Time = day1.AddMilliseconds(i * 10) });
            rec.Stop();
            Assert.True(rec.Capped);
            Assert.True(rec.BytesWritten <= 4096);

            var rec2 = new SizeMapRecorder("ES 09-26", 0.25, true, _dir, 1 << 16);
            rec2.MaxBytesPerDay = 4096;
            rec2.Start();
            DateTime day1b = new DateTime(2026, 8, 15, 10, 0, 0);
            for (int i = 0; i < 2000; i++)
                rec2.OnDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Update, Position = 0,
                                              Price = 5600.25, Volume = 50, Time = day1b.AddMilliseconds(i * 10) });
            // same recorder, next day: the cap is per DAY and must lift
            DateTime day2 = new DateTime(2026, 8, 16, 10, 0, 0);
            for (int i = 0; i < 10; i++)
                rec2.OnDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0,
                                              Price = 5601.00, Volume = 60, Time = day2.AddMilliseconds(i * 10) });
            rec2.Stop();

            Assert.False(rec2.Capped);
            Assert.True(File.Exists(Path.Combine(_dir, "sm-raw-ES_09-26-20260816.smr")));
            Assert.True(ReadHeader(Path.Combine(_dir, "sm-raw-ES_09-26-20260816.smr")).IsReplay);
        }

        // A second NT8 session on the same day must not truncate the morning's corpus.
        [Fact]
        public void Restart_OpensANewPart_InsteadOfClobberingTheExistingFile()
        {
            DateTime t = new DateTime(2026, 8, 14, 9, 30, 0);
            for (int session = 0; session < 3; session++)
            {
                var rec = new SizeMapRecorder("NQ 09-26", 0.25, false, _dir, 1024);
                rec.Start();
                for (int i = 0; i < 20; i++)
                    rec.OnDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Update, Position = 0,
                                                 Price = 22000.00, Volume = 10 + session,
                                                 Time = t.AddMilliseconds(i * 10) });
                rec.Stop();
            }
            Assert.Equal(3, Directory.GetFiles(_dir, "*.smr").Length);
            Assert.Equal(20, ReadAll(Path.Combine(_dir, "sm-raw-NQ_09-26-20260814.smr")).Length);
            Assert.Equal(10, ReadAll(Path.Combine(_dir, "sm-raw-NQ_09-26-20260814.smr"))[0].Size);
            Assert.Equal(12, ReadAll(Path.Combine(_dir, "sm-raw-NQ_09-26-20260814-part3.smr"))[0].Size);
        }

        [Fact]
        public void DefaultTimestamp_IsRejected_AndNeverThrows()
        {
            var rec = new SizeMapRecorder("NQ 09-26", 0.25, false, _dir, 64);
            rec.Start();
            // DateTime.MinValue.AddSeconds(-2) throws; the guard has to run before every other one
            rec.OnDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Update, Price = 22000, Volume = 1,
                                         Time = default(DateTime) });
            rec.OnEpochBreak(default(DateTime));
            rec.Stop();
            Assert.False(rec.Failed);
            // nothing happened, so not even the directory was made
            Assert.False(Directory.Exists(_dir) && Directory.GetFiles(_dir, "*.smr").Length > 0);
        }

        private static int Count(RawRecord[] rs, RawKind k)
        {
            int n = 0;
            foreach (RawRecord r in rs) if (r.Kind == k) n++;
            return n;
        }

        private static RawHeader ReadHeader(string path)
        {
            RawHeader h;
            Assert.True(RawFile.TryReadHeader(File.ReadAllBytes(path), 0, (int)new FileInfo(path).Length, out h));
            return h;
        }

        private static RawRecord[] ReadAll(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            RawHeader h;
            RawRecord[] recs = new RawRecord[RawFile.ReadAll(bytes, out h, null)];
            RawFile.ReadAll(bytes, out h, recs);
            return recs;
        }
    }
}
