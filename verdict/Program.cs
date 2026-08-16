using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using SizeMap.Engine;

namespace SizeMap.Verdict
{
    // Does the wall state machine deserve to be painted?
    //
    // Phase 2's whole claim is that a wall is a stateful object with a death cause. That claim was
    // about to be RENDERED — grooves, glyphs, a three-segment ledger — without ever being MEASURED.
    // This replays recorded .smr tape through the same engine files the indicator ships
    // (BookMirror -> WallDetector -> EpisodeClassifier -> LiquidityMemory, via WallTracker) and
    // answers four questions with numbers:
    //
    //   1. is the detector's output plausible at all (census, lifetimes, outcome split)
    //   2. does EpisodeClassifier beat a ten-line null model (confusion matrix, agreement)
    //   3. does the label carry FORWARD information (price at +10/+30/+60 s, per outcome)
    //   4. is the TRADED vs CANCELLED split trustworthy (TradeRetention bias, W_assoc overlap)
    //
    // Nothing here is allowed to reimplement engine logic. Every number about an episode comes out
    // of the engine's own API — WallTracker.ErosionReads for the facts an open episode is sitting
    // on, WallTracker.ResolvedThisUpdate for the verdict — and the two are cross-checked against
    // each other on every classified episode (see FidelityOk/FidelityBad, printed in the report).
    // If that check ever prints a mismatch, this tool is measuring itself, not the engine.
    internal static class Program
    {
        const string Usage =
@"SizeMap verdict — does the wall classifier earn its complexity?

  dotnet run --project verdict -- --in <tape.smr> [options]

  --in <file.smr>     recorded tape (required)
  --hz <n>            engine ticks per second        (default 4 — SizeMapHeat's real rate)
  --retention <sec>   BookMirror trade retention     (default 1500, the shipped value)
  --retention2 <sec>  second full pass at this retention, to measure the TBF bias (default off)
  --k <mult>          RadarConfig.K_mult override    (default 1.5, the ES property default)
  --minabs <lots>     RadarConfig.MinAbsSize         (default 40)
  --dapproach <n>     RadarConfig.D_approach         (default 1; 2-3 = Phase 3 mechanism B)
  --csv <file>        one line per resolved episode, for audits the report does not do
  --tick <size>       tick size override             (default: from the file header)
  --minutes <n>       stop after n minutes of market time
  --label <name>      suffix for the report filename (one tape, several configs)
  --out-dir <dir>     report destination             (default docs/verdict)
  --no-write          stdout only
  --progress          progress to stderr
  --self-test         run the internal asserts and exit";

        static int Main(string[] argv)
        {
            for (int i = 0; i < argv.Length; i++)
            {
                if (argv[i] == "--self-test") return SelfTest();
                if (argv[i] == "-h" || argv[i] == "--help") { Console.WriteLine(Usage); return 0; }
            }

            Opt o;
            try { o = Opt.Parse(argv); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message + "\n\n" + Usage); return 2; }

            Result main = new Replay(o, o.Retention).Run();
            Result raised = o.Retention2 > 0 ? new Replay(o, o.Retention2).Run() : null;

            if (o.Csv != null) Dump(main, o.Csv);

            string text = Report.Build(main, raised);
            Console.Write(text);

            if (!o.NoWrite)
            {
                Directory.CreateDirectory(o.OutDir);
                string stem = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                              + "-" + Path.GetFileNameWithoutExtension(o.In)
                              + (o.Label != null ? "-" + o.Label : "");
                string md = Path.Combine(o.OutDir, stem + ".md");
                string js = Path.Combine(o.OutDir, stem + ".json");
                File.WriteAllText(md, text);
                File.WriteAllText(js, Report.Json(main, raised));
                Console.WriteLine();
                Console.WriteLine("wrote " + md);
                Console.WriteLine("wrote " + js);
            }
            return 0;
        }

        // One line per resolved episode. The report aggregates; this is what lets two RUNS be diffed
        // episode by episode — the only way to say "this mechanism moved exactly these labels and no
        // others", which is how Phase 3 concluded that reordering Pulled above Consumed moves none.
        static void Dump(Result r, string path)
        {
            StringBuilder b = new StringBuilder();
            b.AppendLine("side,price,opened,resolved,sizeAtOpen,displayed,traded,cancelled,drop,"
                         + "vanished,crossed,awayAtVanish,cls,null,sameTick,wallAgeSec,peak,tbf,mid0,m10,m30,m60");
            for (int i = 0; i < r.Episodes.Count; i++)
            {
                EpisodeRec e = r.Episodes[i];
                b.Append(e.Side).Append(',').Append(Inv(e.Price)).Append(',')
                 .Append(e.OpenedAt.Ticks).Append(',').Append(e.ResolvedAt.Ticks).Append(',')
                 .Append(e.SizeAtOpen).Append(',').Append(e.Displayed).Append(',').Append(e.Traded).Append(',')
                 .Append(e.Cancelled).Append(',').Append(e.Drop).Append(',').Append(e.Vanished ? 1 : 0).Append(',')
                 .Append(e.Crossed ? 1 : 0).Append(',').Append(e.TicksAwayAtVanish).Append(',')
                 .Append(e.Cls).Append(',').Append(e.Null).Append(',').Append(e.SameTick ? 1 : 0).Append(',')
                 .Append(e.WallAgeSec.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                 .Append(e.Peak).Append(',').Append(e.Tbf.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                 .Append(Inv(e.MidAtResolve)).Append(',').Append(Inv(e.Have[0] ? e.Mid[0] : 0)).Append(',')
                 .Append(Inv(e.Have[1] ? e.Mid[1] : 0)).Append(',').Append(Inv(e.Have[2] ? e.Mid[2] : 0)).AppendLine();
            }
            File.WriteAllText(path, b.ToString());
            Console.Error.WriteLine("wrote " + path + " (" + r.Episodes.Count + " episodes)");
        }

        static string Inv(double d) { return d.ToString(CultureInfo.InvariantCulture); }

        // The runnable check. Every branch below is one that would silently corrupt a headline
        // number if it broke, and none of them is covered by the engine's own 130 tests.
        static int SelfTest()
        {
            // The null model, one case per branch.
            Chk(NullModel.Classify(true, 2, 0, 100) == Outcome.Pulled, "vanished far from the quote = pulled");
            Chk(NullModel.Classify(true, 1, 0, 100) == Outcome.Consumed, "vanished at the touch, no trades = consumed");
            Chk(NullModel.Classify(false, 9, 100, 100) == Outcome.Absorbed, "traded >= drop = absorbed");
            Chk(NullModel.Classify(false, 0, 40, 100) == Outcome.Consumed, "traded < drop = consumed");
            Chk(NullModel.Classify(true, 5, 500, 100) == Outcome.Pulled, "pull test runs first, even with trades");

            // Order statistics: an off-by-one here shifts every median in the report.
            Chk(Stats.Median(new List<double> { 3, 1, 2 }) == 2, "odd median");
            Chk(Stats.Median(new List<double> { 4, 1, 2, 3 }) == 2.5, "even median");
            Chk(Stats.Pct(new List<double> { 1, 2, 3, 4, 5 }, 0.5) == 3, "p50");

            // Welch: identical samples must not be significant; separated ones must be.
            var a = new List<double>(); var b = new List<double>();
            for (int i = 0; i < 50; i++) { a.Add(i % 10); b.Add(i % 10); }
            Chk(Stats.Welch(a, b).P > 0.9, "identical samples are not significant");
            var c = new List<double>();
            for (int i = 0; i < 50; i++) c.Add(i % 10 + 20);
            Chk(Stats.Welch(a, c).P < 1e-9, "separated samples are significant");

            // Normal tail, against a table value.
            Chk(Math.Abs(Stats.TwoSidedZ(1.959964) - 0.05) < 1e-4, "z=1.96 -> p=0.05");

            // Holm, by hand. m=3: adjusted are 3p1, then max(3p1, 2p2), then max(., 1p3), monotone.
            double[] h3 = Stats.Holm(new double[] { 0.01, 0.04, 0.03 });
            Chk(Math.Abs(h3[0] - 0.03) < 1e-12, "holm smallest p x m");
            Chk(Math.Abs(h3[2] - 0.06) < 1e-12, "holm middle p x (m-1)");
            Chk(Math.Abs(h3[1] - 0.06) < 1e-12, "holm is monotone: the largest inherits the running max");
            Chk(Stats.Holm(new double[] { 0.9, 0.9 })[0] == 1.0, "holm caps at 1");
            Chk(Stats.Holm(new double[0]).Length == 0, "holm survives an empty family");

            // MDE of a difference of means: 2.8 x SE. Two unit-variance samples of 100 -> 2.8*sqrt(0.02).
            List<double> u1 = new List<double>(), u2 = new List<double>();
            for (int i = 0; i < 100; i++) { u1.Add(i % 2 == 0 ? 1 : -1); u2.Add(i % 2 == 0 ? 1 : -1); }
            Chk(Math.Abs(Stats.MdeMean(u1, u2) - 2.8 * Math.Sqrt(2.0 / 100 * (100.0 / 99))) < 1e-9, "MDE of a mean difference");

            // Cluster bootstrap: with EVERY episode on its own wall and a real separation, it must
            // find one; with all of them on ONE wall there are no clusters to resample and it must
            // refuse rather than invent a bound.
            List<string> ca = new List<string>(), cb = new List<string>();
            List<double> xa = new List<double>(), xb = new List<double>();
            for (int i = 0; i < 60; i++) { ca.Add("w" + i); xa.Add(10 + i % 3); cb.Add("v" + i); xb.Add(i % 3); }
            double lo, hi, pc; int nc;
            Stats.ClusterBoot(ca, xa, cb, xb, out lo, out hi, out pc, out nc);
            Chk(nc == 120 && lo > 0 && pc < 0.01, "cluster bootstrap separates well-separated clusters");
            Stats.ClusterBoot(new List<string> { "w", "w" }, new List<double> { 1, 2 },
                              new List<string> { "w", "w" }, new List<double> { 3, 4 }, out lo, out hi, out pc, out nc);
            Chk(nc == 1 && double.IsNaN(pc), "one cluster is not a sample: no p, no CI");

            // Key packing must never collide across sides or across neighbouring ticks.
            Chk(Replay.KeyOf(Side.Bid, 6000.25, 0.25) != Replay.KeyOf(Side.Ask, 6000.25, 0.25), "sides differ");
            Chk(Replay.KeyOf(Side.Bid, 6000.25, 0.25) != Replay.KeyOf(Side.Bid, 6000.50, 0.25), "ticks differ");

            // The .md is written BEFORE the .json, so a serializer throw leaves a report on disk with
            // no machine-readable twin and an exit code nobody reads in a background sweep. An empty
            // run is the cheapest way to drive every statistic to NaN at once.
            Result empty = new Result { File = "none", TickSize = 0.25 };
            string js = Report.Json(empty, null);
            Chk(js.Contains("\"lifetime_p50_sec\": null"), "an uncomputable statistic serialises as null, not NaN");
            Chk(Report.Build(empty, null).Length > 0, "the report builds on a tape with no episodes");

            Console.WriteLine("self-test OK");
            return 0;
        }

        static void Chk(bool ok, string what)
        {
            if (!ok) { Console.Error.WriteLine("SELF-TEST FAILED: " + what); Environment.Exit(1); }
        }
    }

    internal sealed class Opt
    {
        public string In, Label, OutDir = Path.Combine("docs", "verdict");
        // The SHIPPED configuration, not the tool's old convenience defaults. SizeMapHeat gates
        // WallTracker.Update to EngineIntervalMs = 250 (4 Hz), and WallDetector's temporal baseline
        // is a median over the SAMPLES that gate produces — so the call rate is not a performance
        // knob, it is an input to the detector. Phase 2 measured at 20 Hz and reported it as the
        // indicator's behaviour. K/MinAbs/Retention are the indicator's ES property defaults.
        public double Hz = 4, Retention = 1500, Retention2 = 0, K = 1.5, Tick = 0;
        public long MinAbs = 40;
        public double Minutes = 0;
        public int DApproach = 1;
        public string Csv;
        public bool NoWrite, Progress;

        public static Opt Parse(string[] a)
        {
            Opt o = new Opt();
            for (int i = 0; i < a.Length; i++)
            {
                string k = a[i];
                if (k == "--no-write") { o.NoWrite = true; continue; }
                if (k == "--progress") { o.Progress = true; continue; }
                if (i + 1 >= a.Length) throw new ArgumentException("missing value for " + k);
                string v = a[++i];
                switch (k)
                {
                    case "--in": o.In = v; break;
                    case "--label": o.Label = v; break;
                    case "--out-dir": o.OutDir = v; break;
                    case "--hz": o.Hz = Dbl(v, k); break;
                    case "--retention": o.Retention = Dbl(v, k); break;
                    case "--retention2": o.Retention2 = Dbl(v, k); break;
                    case "--k": o.K = Dbl(v, k); break;
                    case "--minabs": o.MinAbs = (long)Dbl(v, k); break;
                    case "--dapproach": o.DApproach = (int)Dbl(v, k); break;
                    case "--csv": o.Csv = v; break;
                    case "--tick": o.Tick = Dbl(v, k); break;
                    case "--minutes": o.Minutes = Dbl(v, k); break;
                    default: throw new ArgumentException("unknown option " + k);
                }
            }
            if (o.In == null) throw new ArgumentException("--in is required");
            if (!File.Exists(o.In)) throw new FileNotFoundException("no such tape: " + o.In);
            if (o.Hz <= 0) throw new ArgumentException("--hz must be > 0");
            return o;
        }

        static double Dbl(string v, string k)
        {
            double r;
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out r))
                throw new ArgumentException(k + " wants a number, got " + v);
            return r;
        }
    }

    // One resolved episode, with everything both models need and everything the report reports.
    internal sealed class EpisodeRec
    {
        public Side Side;
        public double Price;
        public DateTime OpenedAt, ResolvedAt;
        public long SizeAtOpen, Displayed, Traded, Cancelled, Drop;
        public bool Vanished, Crossed;
        public int TicksAwayAtVanish;
        public int Cls;              // 0 Absorbed, 1 Pulled, 2 Consumed, 3 the classifier refused
        public int Null;             // same coding, from NullModel
        public bool SameTick;        // opened AND resolved inside one engine tick: no facts captured
        public double MidAtResolve;
        public double[] Mid = new double[3];
        public bool[] Have = new bool[3];
        public double WallAgeSec;    // promotion -> resolution
        // Cluster id: price/side PLUS the promotion stamp, so a wall that dies and is re-promoted at
        // the same price is a different cluster. Several episodes can sit on ONE wall, which is the
        // dependence that makes an i.i.d. p-value optimistic.
        public string Wall;
        public long Peak;
        public double Tbf;           // ConsumptionTracker.TradeBackedFraction at resolution
    }

    // One contrast: bucket A's mean forward move against bucket B's, on ONE side at ONE horizon.
    internal sealed class MoveTest
    {
        public string Pair;
        public int Side, H, N1, N2, Clusters;
        public double M1, M2, Diff, Mde, T, P, HolmP, CiLo, CiHi, PCluster;

        // null when either bucket has fewer than 2 reads: a t on n<2 is not a weak test, it is
        // undefined, and a NaN row in a Holm family silently shrinks the correction.
        public static MoveTest Build(List<EpisodeRec> eps, int c1, int c2, int side, int h)
        {
            List<string> wa, wb;
            List<double> a = Report.Moves(Report.Bucket(eps, c1, side), h, out wa);
            List<double> b = Report.Moves(Report.Bucket(eps, c2, side), h, out wb);
            if (a.Count < 2 || b.Count < 2) return null;

            MoveTest t = new MoveTest
            {
                Pair = Report.NameOf(c1) + " vs " + Report.NameOf(c2), Side = side, H = h,
                N1 = a.Count, N2 = b.Count, M1 = Stats.Mean(a), M2 = Stats.Mean(b)
            };
            t.Diff = t.M1 - t.M2;
            Stats.Test w = Stats.Welch(a, b);
            t.T = w.T; t.P = w.P;
            t.Mde = Stats.MdeMean(a, b);
            Stats.ClusterBoot(wa, a, wb, b, out t.CiLo, out t.CiHi, out t.PCluster, out t.Clusters);
            return t;
        }
    }

    internal sealed class NodeTrack
    {
        public long FirstSeenTicks;
        public DateTime LastSeen;
        public long Peak;
        public bool Alive;
    }

    internal sealed class Result
    {
        public string File, Instrument = "?";
        public double TickSize, RetentionSec, Hz, K;
        public long MinAbs;
        public DateTime T0, First, Last;
        public int Records, Depth, Trades, Resets, EpochBreaks;
        public int Ticks, CensusSamples;
        public double EngineSeconds;

        public List<EpisodeRec> Episodes = new List<EpisodeRec>();
        public int FidelityOk, FidelityBad, SameTick, NoNode;

        // The RENDERER's three buckets, per census sample. Phase 2 counted `InWindow ? live : rem`
        // with no confidence test, so it reasoned about a population the chart never showed. These
        // three mirror Rasterizer.DrawWalls exactly and are exhaustive by construction.
        public List<double> Solid = new List<double>();      // InWindow            -> grooved band
        public List<double> Hollow = new List<double>();     // else Conf >= 0.25   -> hollow rule
        public List<double> Undrawn = new List<double>();    // tracked, under the floor: INVISIBLE
        public List<double> SolidBand = new List<double>();  // the drawn two, clipped to +/-25 ticks
        public List<double> HollowBand = new List<double>();
        public int CapHitSolid, CapHitHollow;                // samples where the §4 draw caps bit
        public int Dead;                                     // nodes that left memory (evicted)
        public int WallsDetected;
        public List<double> Lifetimes = new List<double>();
        public List<double> Peaks = new List<double>();
        public List<double> DepthSizes = new List<double>(); // sampled resting sizes, for context
        public List<double> MidSeries = new List<double>();  // mid at every census sample, and its stamp:
        public List<long> MidTicks = new List<long>();       // the unconditional drift control for Q3
    }

    internal sealed class Replay
    {
        // The spec's HUD counts what is ON SCREEN. LiquidityMemory.Snapshot no longer clips (that
        // clip moved to the rasterizer), so the census is taken twice: unclipped, and clipped here
        // to the +/-25 ticks the target band 2-6 live / 10-25 remembered was written against.
        const int ScreenBandTicks = 25;
        const double CensusHz = 4;          // NT8 repaints at 4 fps; a census the eye never sees is fiction

        // Copied from Rasterizer.DrawWalls, deliberately by value and not by reference: `engine/` is
        // netstandard2.0 and these are private consts there. If either moves, this tool starts
        // counting a different population than the chart draws — which is the exact Phase 2 bug.
        const double ConfidenceFloor = 0.25;
        const int MaxGrooved = 12, MaxRemembered = 24;

        sealed class Shadow
        {
            public Side Side; public double Price; public DateTime OpenedAt;
            public long SizeAtOpen, Displayed, Traded, Cancelled;
            public bool Vanished, Crossed; public int TicksAwayAtVanish;
        }

        struct Pending { public long DueTicks; public EpisodeRec E; public int H; }

        static readonly double[] Horizons = { 10, 30, 60 };

        readonly Opt _o;
        readonly RadarConfig _cfg = new RadarConfig();
        readonly double _tick;
        readonly Result _r = new Result();
        readonly Dictionary<long, Shadow> _open = new Dictionary<long, Shadow>();
        readonly Dictionary<long, EpisodeResult> _res = new Dictionary<long, EpisodeResult>();
        readonly Dictionary<long, NodeTrack> _track = new Dictionary<long, NodeTrack>();
        readonly Queue<Pending>[] _pending = { new Queue<Pending>(), new Queue<Pending>(), new Queue<Pending>() };

        BookMirror _book;
        WallTracker _tracker;
        DateTime _lastRun = DateTime.MinValue, _lastCensus = DateTime.MinValue;

        public Replay(Opt o, double retentionSec)
        {
            _o = o;
            byte[] head = new byte[RawFile.HeaderBytes];
            using (FileStream fs = File.OpenRead(o.In)) fs.Read(head, 0, head.Length);
            RawHeader h;
            if (!RawFile.TryReadHeader(head, 0, head.Length, out h))
                throw new InvalidDataException(o.In + ": not an .smr file");
            _tick = o.Tick > 0 ? o.Tick : h.TickSize;

            _cfg.TickSize = _tick;
            _cfg.K_mult = o.K;
            _cfg.MinAbsSize = o.MinAbs;
            _cfg.D_approach = o.DApproach;
            _r.File = o.In; _r.TickSize = _tick; _r.RetentionSec = retentionSec;
            _r.Hz = o.Hz; _r.K = o.K; _r.MinAbs = o.MinAbs; _r.T0 = new DateTime(h.T0Ticks);

            string name = Path.GetFileNameWithoutExtension(o.In);
            string[] parts = name.Split('-');
            if (parts.Length >= 3) _r.Instrument = parts[2];

            _book = new BookMirror(_tick, TimeSpan.FromSeconds(retentionSec));
            _tracker = new WallTracker(_cfg);
        }

        public static long KeyOf(Side s, double price, double tick)
        {
            return ((long)Math.Round(price / tick)) * 2 + (s == Side.Ask ? 1 : 0);
        }

        long Key(Side s, double price) { return KeyOf(s, price, _tick); }

        public Result Run()
        {
            byte[] file = File.ReadAllBytes(_o.In);
            int n = (file.Length - RawFile.HeaderBytes) / RawFile.RecordBytes;
            RawHeader h; RawFile.TryReadHeader(file, 0, file.Length, out h);
            long t0 = h.T0Ticks;
            long stopTicks = _o.Minutes > 0 ? h.T0Ticks + (long)(_o.Minutes * 60 * TimeSpan.TicksPerSecond) : long.MaxValue;
            double intervalMs = 1000.0 / _o.Hz;
            Stopwatch engine = new Stopwatch();
            _r.Records = n;

            for (int i = 0; i < n; i++)
            {
                RawRecord r = RawFile.Read(file, RawFile.HeaderBytes + i * RawFile.RecordBytes);

                if (r.Kind == RawKind.EpochBreak)
                {
                    // The writer splits here because market time jumped. Two epochs welded together
                    // would hand the engine a book from another moment; rebuild, exactly as the
                    // indicator's HandleReplayReset does.
                    t0 = RawFile.EpochT0Of(r);
                    _book = new BookMirror(_tick, TimeSpan.FromSeconds(_r.RetentionSec));
                    _tracker.OnReset(new DateTime(t0));
                    _lastRun = DateTime.MinValue;
                    _r.EpochBreaks++;
                    continue;
                }

                long ticks = t0 + (long)r.DtMs * TimeSpan.TicksPerMillisecond;
                if (ticks >= stopTicks) break;
                DateTime now = new DateTime(ticks);
                if (_r.First == default(DateTime)) _r.First = now;
                _r.Last = now;

                switch (r.Kind)
                {
                    case RawKind.FeedReset:
                        _book.ApplyDepth(new DepthEvent { IsReset = true, Time = now });
                        _tracker.OnReset(now);
                        _r.Resets++;
                        break;
                    case RawKind.DepthBid:
                    case RawKind.DepthAsk:
                        _book.ApplyDepth(new DepthEvent
                        {
                            Side = r.Kind == RawKind.DepthBid ? Side.Bid : Side.Ask,
                            Op = (DepthOp)r.Op,
                            Position = r.Pos == RawFile.NA ? -1 : r.Pos,
                            Price = r.Row * _tick,
                            Volume = r.Size,
                            Time = now
                        });
                        _r.Depth++;
                        break;
                    case RawKind.TradeBuy:
                    case RawKind.TradeSell:
                        // The recorded buy/sell flag is deliberately NOT replayed: BookMirror infers
                        // the aggressor from its own book, which is what it does live.
                        _book.ApplyTrade(new TradeEvent { Price = r.Row * _tick, Volume = r.Size, Time = now });
                        _r.Trades++;
                        break;
                }

                FillPending(now);

                if (_lastRun != DateTime.MinValue)
                {
                    double dMs = (now - _lastRun).TotalMilliseconds;
                    if (dMs < -2000 || dMs > 60000)
                    {
                        _book = new BookMirror(_tick, TimeSpan.FromSeconds(_r.RetentionSec));
                        _tracker.OnReset(now);
                    }
                    else if (dMs < 0) { _lastRun = now; continue; }
                    else if (dMs < intervalMs) continue;
                }
                _lastRun = now;
                engine.Start();
                Tick(now);
                engine.Stop();

                if (_o.Progress && (_r.Ticks % 20000) == 0)
                    Console.Error.Write("\r  " + now.ToString("HH:mm:ss") + "  episodes " + _r.Episodes.Count + "   ");
            }
            if (_o.Progress) Console.Error.WriteLine();

            _r.EngineSeconds = engine.Elapsed.TotalSeconds;
            foreach (NodeTrack t in _track.Values) if (t.Alive) EndTrack(t);
            return _r;
        }

        void EndTrack(NodeTrack t)
        {
            _r.Lifetimes.Add((t.LastSeen.Ticks - t.FirstSeenTicks) / (double)TimeSpan.TicksPerSecond);
            _r.Peaks.Add(t.Peak);
        }

        void Tick(DateTime now)
        {
            _r.Ticks++;

            // 1) The facts the classifier is ABOUT to resolve on, read from its own state at this
            //    same `now` and this same book. Read after Update instead and they are one tick
            //    stale, which is exactly the window in which a wall dies.
            ObserveOpen(now);
            List<long> before = new List<long>(_open.Keys);

            _tracker.Update(_book, now);

            _res.Clear();
            IReadOnlyList<EpisodeResult> results = _tracker.ResolvedThisUpdate;
            for (int i = 0; i < results.Count; i++) _res[Key(results[i].Side, results[i].Price)] = results[i];

            // 2) Whatever is still open after Update (this also arms episodes opened by it).
            HashSet<long> live = ObserveOpen(now);
            for (int i = 0; i < before.Count; i++)
                if (!live.Contains(before[i])) Close(before[i], now);

            // 3) A verdict with no shadow: the episode opened and died inside one Update. No facts
            //    were ever observable, so it is counted and excluded rather than guessed at.
            foreach (KeyValuePair<long, EpisodeResult> kv in _res)
            {
                EpisodeRec e = new EpisodeRec
                {
                    Side = kv.Value.Side, Price = kv.Value.Price, OpenedAt = now, ResolvedAt = now,
                    Traded = kv.Value.Traded, Cancelled = kv.Value.Cancelled,
                    Cls = (int)kv.Value.Outcome, Null = -1, SameTick = true
                };
                _r.SameTick++;
                _r.Episodes.Add(e);
            }

            if (_lastCensus == DateTime.MinValue || (now - _lastCensus).TotalSeconds >= 1.0 / CensusHz)
            {
                _lastCensus = now;
                Census(now);
            }
        }

        HashSet<long> ObserveOpen(DateTime now)
        {
            IReadOnlyList<ErosionRead> reads = _tracker.ErosionReads(_book, now);
            HashSet<long> keys = new HashSet<long>();
            for (int i = 0; i < reads.Count; i++)
            {
                ErosionRead e = reads[i];
                long k = Key(e.Side, e.Price);
                keys.Add(k);
                Shadow s;
                if (!_open.TryGetValue(k, out s))
                {
                    s = new Shadow { Side = e.Side, Price = e.Price, OpenedAt = now, SizeAtOpen = e.SizeAtOpen };
                    _open[k] = s;
                }
                s.Displayed = e.Displayed;
                s.Traded = e.Traded;
                s.Cancelled = e.Cancelled;
                if (e.Displayed == 0 && !s.Vanished)
                {
                    s.Vanished = true;
                    s.TicksAwayAtVanish = QuoteTicksAway(e.Side, e.Price);
                }
                if (QuoteCrossed(e.Side, e.Price)) s.Crossed = true;
            }
            return keys;
        }

        void Close(long k, DateTime now)
        {
            Shadow s = _open[k];
            _open.Remove(k);

            EpisodeRec e = new EpisodeRec
            {
                Side = s.Side, Price = s.Price, OpenedAt = s.OpenedAt, ResolvedAt = now,
                SizeAtOpen = s.SizeAtOpen, Displayed = s.Displayed, Traded = s.Traded,
                Cancelled = s.Cancelled, Drop = Math.Max(0, s.SizeAtOpen - s.Displayed),
                Vanished = s.Vanished, Crossed = s.Crossed, TicksAwayAtVanish = s.TicksAwayAtVanish
            };

            EpisodeResult r;
            if (_res.TryGetValue(k, out r))
            {
                e.Cls = (int)r.Outcome;
                // THE fidelity check: the engine's own numbers against the ones this tool captured
                // from ErosionReads one call earlier. Equal means the null model is being fed
                // exactly what the classifier was fed.
                if (r.Traded == s.Traded && r.Cancelled == s.Cancelled) _r.FidelityOk++;
                else _r.FidelityBad++;
                _res.Remove(k);
            }
            else e.Cls = 3;   // the classifier refused to name a cause

            e.Null = (int)NullModel.Classify(e.Vanished, e.TicksAwayAtVanish, e.Traded, e.Drop);

            NodeTrack t;
            DateTime armTime;
            if (_track.TryGetValue(k, out t))
            {
                e.Peak = t.Peak;
                armTime = new DateTime(t.FirstSeenTicks);   // LiquidityMemory's own promotion stamp
                e.Wall = k + ":" + t.FirstSeenTicks;
            }
            else
            {
                // Promoted and resolved between two 4 Hz census samples: no node was ever observed.
                e.Peak = s.SizeAtOpen;
                armTime = s.OpenedAt;
                e.Wall = k + ":@" + s.OpenedAt.Ticks;   // never observed as a node: its own cluster
                _r.NoNode++;
            }
            e.WallAgeSec = (now - armTime).TotalSeconds;

            ConsumptionRead c = ConsumptionTracker.Read(s.Side, s.Price, e.Peak, s.Displayed, armTime, _book);
            e.Tbf = c.TradeBackedFraction;

            e.MidAtResolve = Mid();
            _r.Episodes.Add(e);
            if (e.MidAtResolve > 0)
                for (int i = 0; i < Horizons.Length; i++)
                    _pending[i].Enqueue(new Pending
                    {
                        DueTicks = now.Ticks + (long)(Horizons[i] * TimeSpan.TicksPerSecond),
                        E = e, H = i
                    });
        }

        void FillPending(DateTime now)
        {
            for (int i = 0; i < _pending.Length; i++)
            {
                Queue<Pending> q = _pending[i];
                while (q.Count > 0 && q.Peek().DueTicks <= now.Ticks)
                {
                    Pending p = q.Dequeue();
                    double m = Mid();
                    if (m > 0) { p.E.Mid[p.H] = m; p.E.Have[p.H] = true; }
                }
            }
        }

        void Census(DateTime now)
        {
            IReadOnlyList<RadarNode> snap = _tracker.GetSnapshot(now);
            double mid = Mid();
            int solid = 0, hollow = 0, undrawn = 0, solidBand = 0, hollowBand = 0;
            HashSet<long> seen = new HashSet<long>();

            for (int i = 0; i < snap.Count; i++)
            {
                RadarNode n = snap[i];
                long k = Key(n.Side, n.Price);
                seen.Add(k);
                bool band = mid <= 0 || Math.Abs(n.Price - mid) <= ScreenBandTicks * _tick + _tick / 2;
                // THE renderer's predicate, in the renderer's order. Nothing else may decide who is
                // counted: a verdict about walls the chart does not draw is a verdict about nothing.
                if (n.InWindow) { solid++; if (band) solidBand++; }
                else if (n.Confidence >= ConfidenceFloor) { hollow++; if (band) hollowBand++; }
                else undrawn++;

                NodeTrack t;
                if (!_track.TryGetValue(k, out t) || t.FirstSeenTicks != n.FirstSeenTicks)
                {
                    // A different FirstSeenTicks at the same price is a NEW wall, re-promoted where
                    // an older one died. Counting it as the same node would fuse two lifetimes.
                    if (t != null && t.Alive) EndTrack(t);
                    t = new NodeTrack { FirstSeenTicks = n.FirstSeenTicks, Alive = true };
                    _track[k] = t;
                    _r.WallsDetected++;
                }
                t.LastSeen = now;
                if (n.PeakSize > t.Peak) t.Peak = n.PeakSize;
            }

            foreach (KeyValuePair<long, NodeTrack> kv in _track)
            {
                if (!kv.Value.Alive || seen.Contains(kv.Key)) continue;
                kv.Value.Alive = false;   // gone from memory: evicted or died
                _r.Dead++;
                EndTrack(kv.Value);
            }

            if (mid > 0) { _r.MidSeries.Add(mid); _r.MidTicks.Add(now.Ticks); }
            _r.Solid.Add(solid); _r.Hollow.Add(hollow); _r.Undrawn.Add(undrawn);
            _r.SolidBand.Add(solidBand); _r.HollowBand.Add(hollowBand);
            if (solid > MaxGrooved) _r.CapHitSolid++;
            if (hollow > MaxRemembered) _r.CapHitHollow++;
            _r.CensusSamples++;

            // Resting-size context, so "is MinAbsSize sane for this instrument" is answerable
            // from the same report instead of from a separate script.
            if ((_r.CensusSamples % 20) == 0)
            {
                IReadOnlyList<DepthLevel> b = _book.Levels(Side.Bid), a = _book.Levels(Side.Ask);
                for (int i = 0; i < b.Count; i++) _r.DepthSizes.Add(b[i].Volume);
                for (int i = 0; i < a.Count; i++) _r.DepthSizes.Add(a[i].Volume);
            }
        }

        double Mid()
        {
            DepthLevel b, a;
            bool hb = _book.TryBestBid(out b), ha = _book.TryBestAsk(out a);
            if (hb && ha) return (b.Price + a.Price) / 2;
            if (hb) return b.Price;
            if (ha) return a.Price;
            return 0;
        }

        // Same definition EpisodeClassifier uses: distance to the OPPOSITE inside quote.
        int QuoteTicksAway(Side side, double price)
        {
            DepthLevel q;
            bool has = side == Side.Ask ? _book.TryBestBid(out q) : _book.TryBestAsk(out q);
            if (!has) return int.MaxValue;
            return (int)Math.Round(Math.Abs(price - q.Price) / _tick);
        }

        bool QuoteCrossed(Side side, double price)
        {
            DepthLevel q;
            if (side == Side.Ask) return _book.TryBestAsk(out q) && q.Price > price + _tick / 2;
            return _book.TryBestBid(out q) && q.Price < price - _tick / 2;
        }
    }

    internal static class Stats
    {
        public static double Median(List<double> v) { return Pct(v, 0.5); }

        public static double Pct(List<double> v, double p)
        {
            if (v == null || v.Count == 0) return double.NaN;
            List<double> s = new List<double>(v); s.Sort();
            if (s.Count == 1) return s[0];
            double x = p * (s.Count - 1);
            int lo = (int)Math.Floor(x), hi = (int)Math.Ceiling(x);
            return s[lo] + (s[hi] - s[lo]) * (x - lo);
        }

        public static double Mean(List<double> v)
        {
            if (v == null || v.Count == 0) return double.NaN;
            double s = 0; for (int i = 0; i < v.Count; i++) s += v[i];
            return s / v.Count;
        }

        public struct Test { public double T, P, Df; }

        // Welch's t, p from the normal tail. The normal approximation is stated in the report
        // rather than hidden: with n in the tens it is optimistic, which is the direction that
        // would flatter the product, so every bucket prints its n next to its p.
        public static Test Welch(List<double> a, List<double> b)
        {
            Test t = new Test { T = double.NaN, P = double.NaN, Df = double.NaN };
            if (a == null || b == null || a.Count < 2 || b.Count < 2) return t;
            double ma = Mean(a), mb = Mean(b);
            double va = Var(a, ma), vb = Var(b, mb);
            double sa = va / a.Count, sb = vb / b.Count;
            if (sa + sb <= 0) return t;
            t.T = (ma - mb) / Math.Sqrt(sa + sb);
            double den = sa * sa / (a.Count - 1) + sb * sb / (b.Count - 1);
            t.Df = den > 0 ? (sa + sb) * (sa + sb) / den : double.NaN;
            t.P = TwoSidedZ(Math.Abs(t.T));
            return t;
        }

        static double Var(List<double> v, double m)
        {
            double s = 0; for (int i = 0; i < v.Count; i++) { double d = v[i] - m; s += d * d; }
            return v.Count > 1 ? s / (v.Count - 1) : 0;
        }

        public static Test TwoProportion(int k1, int n1, int k2, int n2)
        {
            Test t = new Test { T = double.NaN, P = double.NaN, Df = double.NaN };
            if (n1 < 1 || n2 < 1) return t;
            double p1 = (double)k1 / n1, p2 = (double)k2 / n2;
            double p = (double)(k1 + k2) / (n1 + n2);
            double se = Math.Sqrt(p * (1 - p) * (1.0 / n1 + 1.0 / n2));
            if (se <= 0) return t;
            t.T = (p1 - p2) / se;
            t.P = TwoSidedZ(Math.Abs(t.T));
            return t;
        }

        // Smallest difference in proportions this pair of sample sizes could detect at 80% power,
        // alpha 0.05. Printed so an underpowered bucket says so in its own row.
        public static double Mde(int n1, int n2, double p)
        {
            if (n1 < 1 || n2 < 1) return double.NaN;
            return 2.8 * Math.Sqrt(p * (1 - p) * (1.0 / n1 + 1.0 / n2));
        }

        // The same 80%-power floor for a difference of MEANS: (z_.975 + z_.80) x SE(diff) = 2.8 x SE.
        // Phase 2 printed `p 0.2422` for a test that could not have seen 3.13 ES ticks and said
        // nothing about it, which reads as "no effect" and is not the same claim.
        public static double MdeMean(List<double> a, List<double> b)
        {
            if (a == null || b == null || a.Count < 2 || b.Count < 2) return double.NaN;
            double va = Var(a, Mean(a)) / a.Count, vb = Var(b, Mean(b)) / b.Count;
            return 2.8 * Math.Sqrt(va + vb);
        }

        // Holm-Bonferroni step-down over a whole family. Chosen over plain Bonferroni (uniformly
        // more powerful, same guarantee) and over Benjamini-Hochberg (which controls only the false
        // DISCOVERY rate and whose original proof wants independence these tests do not have).
        // Returns the ADJUSTED p, so every row is still read against the familiar 0.05.
        public static double[] Holm(double[] p)
        {
            int m = p.Length;
            double[] adj = new double[m];
            if (m == 0) return adj;
            int[] ix = new int[m];
            for (int i = 0; i < m; i++) ix[i] = i;
            Array.Sort(ix, delegate (int x, int y) { return p[x].CompareTo(p[y]); });
            double run = 0;
            for (int i = 0; i < m; i++)
            {
                double a = (m - i) * p[ix[i]];
                if (a > run) run = a;              // monotone: an adjusted p may never fall
                adj[ix[i]] = run > 1 ? 1 : run;
            }
            return adj;
        }

        // Cluster bootstrap of the difference of means, resampling WALLS (not episodes) with
        // replacement. Episodes are not independent — a wall that is approached, eaten back and
        // approached again produces several of them — and the Welch t above counts every one as
        // fresh evidence. Resampling the cluster keeps whatever within-wall correlation exists.
        //
        // ponytail: 2 000 replicates, fixed seed, no BCa correction. Ceiling — the percentile CI is
        // slightly off for very skewed statistics. Upgrade path — BCa, if a decision ever turns on
        // the third decimal of a bound.
        public static void ClusterBoot(List<string> ca, List<double> va, List<string> cb, List<double> vb,
                                       out double lo, out double hi, out double p, out int clusters)
        {
            lo = hi = p = double.NaN; clusters = 0;
            Dictionary<string, int> ix = new Dictionary<string, int>();
            List<List<double>> ga = new List<List<double>>(), gb = new List<List<double>>();
            for (int i = 0; i < ca.Count; i++) Put(ix, ga, gb, ca[i], va[i], true);
            for (int i = 0; i < cb.Count; i++) Put(ix, ga, gb, cb[i], vb[i], false);
            clusters = ix.Count;
            if (clusters < 2) return;

            const int B = 2000;
            Random rnd = new Random(20260816);
            List<double> diff = new List<double>(B);
            for (int r = 0; r < B; r++)
            {
                double sa = 0, sb = 0; int na = 0, nb = 0;
                for (int k = 0; k < clusters; k++)
                {
                    int j = rnd.Next(clusters);
                    List<double> la = ga[j], lb = gb[j];
                    for (int e = 0; e < la.Count; e++) { sa += la[e]; na++; }
                    for (int e = 0; e < lb.Count; e++) { sb += lb[e]; nb++; }
                }
                if (na == 0 || nb == 0) continue;   // a draw that took no cluster from one bucket
                diff.Add(sa / na - sb / nb);
            }
            if (diff.Count < 100) return;           // too few usable replicates to quote a bound
            lo = Pct(diff, 0.025); hi = Pct(diff, 0.975);
            int le = 0, ge = 0;
            for (int i = 0; i < diff.Count; i++) { if (diff[i] <= 0) le++; if (diff[i] >= 0) ge++; }
            double q = 2.0 * (le < ge ? le : ge) / diff.Count;
            p = q > 1 ? 1 : q;
        }

        static void Put(Dictionary<string, int> ix, List<List<double>> ga, List<List<double>> gb,
                        string cluster, double v, bool isA)
        {
            if (cluster == null) cluster = "?";
            int j;
            if (!ix.TryGetValue(cluster, out j))
            {
                j = ix.Count; ix[cluster] = j;
                ga.Add(new List<double>()); gb.Add(new List<double>());
            }
            (isA ? ga[j] : gb[j]).Add(v);
        }

        public static double TwoSidedZ(double z) { return 2 * (1 - Phi(z)); }

        static double Phi(double x) { return 0.5 * (1 + Erf(x / Math.Sqrt(2))); }

        // Abramowitz & Stegun 7.1.26, |error| < 1.5e-7. Enough for a p that is only ever read as
        // "yes / no / not with this n".
        static double Erf(double x)
        {
            int sign = x < 0 ? -1 : 1;
            x = Math.Abs(x);
            double t = 1.0 / (1.0 + 0.3275911 * x);
            double y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
            return sign * y;
        }
    }

    internal static class Report
    {
        static readonly string[] Names = { "ABSORBED", "PULLED", "CONSUMED", "UNCLASSIFIED" };

        public static string Build(Result r, Result raised)
        {
            _tickSize = r.TickSize;
            StringBuilder s = new StringBuilder();
            H(s, "SizeMap verdict — " + Path.GetFileName(r.File));
            s.AppendLine();
            s.AppendLine("Generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                         + " by `verdict/`, replaying recorded tape through the shipped engine files.");
            s.AppendLine();
            s.AppendLine("| | |");
            s.AppendLine("|---|---|");
            Row(s, "tape", Path.GetFileName(r.File) + "  (" + r.Instrument + ", tick " + F(r.TickSize, 2) + ")");
            Row(s, "market time", r.First.ToString("yyyy-MM-dd HH:mm:ss") + " -> " + r.Last.ToString("HH:mm:ss")
                    + "  (" + F((r.Last - r.First).TotalMinutes, 1) + " min)");
            Row(s, "records", r.Records.ToString("N0") + "  (" + r.Depth.ToString("N0") + " depth, "
                    + r.Trades.ToString("N0") + " trades, " + r.Resets + " feed resets, " + r.EpochBreaks + " epoch breaks)");
            Row(s, "engine", r.Ticks.ToString("N0") + " ticks at " + F(r.Hz, 0) + " Hz, "
                    + F(r.EngineSeconds, 1) + " s of CPU (" + F(r.EngineSeconds * 1000.0 / Math.Max(1, r.Ticks), 3) + " ms/tick)");
            Row(s, "config", "K_mult " + F(r.K, 1) + ", MinAbsSize " + r.MinAbs + ", TradeRetention " + F(r.RetentionSec, 0) + " s");
            Row(s, "resting size", "p50 " + F(Stats.Median(r.DepthSizes), 0) + ", p85 " + F(Stats.Pct(r.DepthSizes, 0.85), 0)
                    + ", p99 " + F(Stats.Pct(r.DepthSizes, 0.99), 0) + ", max " + F(Stats.Pct(r.DepthSizes, 1.0), 0)
                    + "  (" + r.DepthSizes.Count.ToString("N0") + " sampled levels)");
            Row(s, "fidelity check", r.FidelityOk + " classified episodes where this tool's captured "
                    + "Traded/Cancelled equal the engine's own, " + r.FidelityBad + " mismatches"
                    + (r.FidelityBad == 0 ? "" : "  <-- READ NOTHING BELOW UNTIL THIS IS 0"));
            s.AppendLine();

            List<EpisodeRec> eps = r.Episodes;
            List<EpisodeRec> usable = new List<EpisodeRec>();
            for (int i = 0; i < eps.Count; i++) if (!eps[i].SameTick) usable.Add(eps[i]);

            Q1(s, r, usable);
            Q2(s, usable);
            Q3(s, r, usable);
            Q4(s, r, raised, usable);
            return s.ToString();
        }

        // ---------------------------------------------------------------- Q1: plausibility
        static void Q1(StringBuilder s, Result r, List<EpisodeRec> eps)
        {
            H2(s, "1. Is the detector's output even plausible?");
            s.AppendLine();
            s.AppendLine("| | |");
            s.AppendLine("|---|---|");
            Row(s, "walls detected", r.WallsDetected + " distinct promotions (" + r.Dead + " left memory)");
            Row(s, "wall lifetime", Q(r.Lifetimes, "s"));
            Row(s, "wall peak size", Q(r.Peaks, " lots"));
            Row(s, "episodes", eps.Count + " resolved" + (r.SameTick > 0 ? " (+" + r.SameTick + " opened and died inside one engine tick, excluded)" : ""));
            s.AppendLine();
            s.AppendLine("**Census** — sampled " + r.CensusSamples.ToString("N0") + " times at 4 Hz, through the RENDERER's own predicate:");
            s.AppendLine("`InWindow` -> **solid** groove; else `Confidence >= 0.25` -> **hollow** rule; else **undrawn**. The third bucket is");
            s.AppendLine("the one Phase 2 had no name for: tracked, counted as \"remembered\", and never on the chart. Target band from");
            s.AppendLine("visual-spec §5: **2-6 solid, 10-25 hollow**.");
            s.AppendLine();
            s.AppendLine("| census | mean | p50 | p90 | max | in target band |");
            s.AppendLine("|---|---|---|---|---|---|");
            CensusRow(s, "SOLID (drawn, in window)", r.Solid, 2, 6);
            CensusRow(s, "HOLLOW (drawn, remembered)", r.Hollow, 10, 25);
            CensusRow(s, "UNDRAWN (tracked, invisible)", r.Undrawn, -1, -1);
            CensusRow(s, "SOLID, ±25 ticks of mid", r.SolidBand, 2, 6);
            CensusRow(s, "HOLLOW, ±25 ticks of mid", r.HollowBand, 10, 25);
            s.AppendLine();
            s.AppendLine("Draw caps (§4: 12 solid, 24 hollow) bit in " + Pc(r.CapHitSolid, r.CensusSamples) + " / "
                         + Pc(r.CapHitHollow, r.CensusSamples) + " of samples.");
            s.AppendLine();

            int[] c = new int[4];
            for (int i = 0; i < eps.Count; i++) c[eps[i].Cls]++;
            s.AppendLine("**Outcome split** (what the glyph layer would paint):");
            s.AppendLine();
            s.AppendLine("| outcome | n | share |");
            s.AppendLine("|---|---|---|");
            for (int i = 0; i < 4; i++)
                s.AppendLine("| " + Names[i] + " | " + c[i] + " | " + Pc(c[i], eps.Count) + " |");
            s.AppendLine();

            int vanished = 0, vanishedCrossed = 0, crossed = 0, cancelGtTraded = 0, awayGe2 = 0;
            for (int i = 0; i < eps.Count; i++)
            {
                EpisodeRec e = eps[i];
                if (e.Vanished) { vanished++; if (e.Crossed) vanishedCrossed++; if (e.TicksAwayAtVanish >= 2) awayGe2++; }
                if (e.Crossed) crossed++;
                if (e.Cancelled > e.Traded) cancelGtTraded++;
            }
            s.AppendLine("**Why a state does or does not fire.** `Consumed` is tested FIRST and wins on `Crossed` alone;");
            s.AppendLine("`Pulled` needs the level to empty while the quote has NOT crossed it.");
            s.AppendLine();
            s.AppendLine("| condition at resolution | n | share |");
            s.AppendLine("|---|---|---|");
            s.AppendLine("| the level emptied (`displayed == 0`) | " + vanished + " | " + Pc(vanished, eps.Count) + " |");
            s.AppendLine("| the quote crossed the level (`Crossed`) | " + crossed + " | " + Pc(crossed, eps.Count) + " |");
            s.AppendLine("| emptied **and** crossed -> `Consumed` preempts `Pulled` | " + vanishedCrossed + " | " + Pc(vanishedCrossed, vanished) + " of emptied |");
            s.AppendLine("| emptied with the quote >= 2 ticks away | " + awayGe2 + " | " + Pc(awayGe2, vanished) + " of emptied |");
            s.AppendLine("| cancelled > traded (the `Pulled` size test) | " + cancelGtTraded + " | " + Pc(cancelGtTraded, eps.Count) + " |");
            s.AppendLine();
            for (int i = 0; i < 3; i++)
                if (c[i] == 0 && eps.Count > 0)
                    s.AppendLine("> **`" + Names[i] + "` never fired once in " + eps.Count + " episodes.** A state that cannot be reached is not a"
                                 + " calibration problem, it is dead code with a glyph in the spec.");
            s.AppendLine();

            int top = 0; for (int i = 1; i < 4; i++) if (c[i] > c[top]) top = i;
            double share = eps.Count > 0 ? (double)c[top] / eps.Count : 0;
            if (eps.Count == 0)
                s.AppendLine("> **NO EPISODES AT ALL.** The detector never promoted a wall that reached the inside, so every");
            else if (share >= 0.95)
                s.AppendLine("> **" + F(share * 100, 1) + "% of episodes land in one bucket (" + Names[top] + ").** The thresholds are wrong for this");
            else if (share >= 0.80)
                s.AppendLine("> **" + F(share * 100, 1) + "% of episodes land in one bucket (" + Names[top] + ").** Not a hard failure, but the label is");
            else
                s.AppendLine("> Outcome split is spread across buckets (" + F(share * 100, 1) + "% in the largest, " + Names[top] + "), so the");
            if (eps.Count == 0)
                s.AppendLine("> number below this line is meaningless. Fix the detector before reading any of it.");
            else if (share >= 0.95)
                s.AppendLine("> instrument and everything below is close to meaningless — a constant is not a classification.");
            else if (share >= 0.80)
                s.AppendLine("> carrying much less information than four states suggest.");
            else
                s.AppendLine("> rest of this report is measuring something with variance in it.");
            s.AppendLine();
        }

        // ---------------------------------------------------------------- Q2: vs the null model
        static void Q2(StringBuilder s, List<EpisodeRec> eps)
        {
            H2(s, "2. Does EpisodeClassifier beat the null model?");
            s.AppendLine();
            s.AppendLine("```");
            s.AppendLine("if (size hit 0 && |quote - level| >= 2 ticks) return Pulled;");
            s.AppendLine("else if (tradedAt >= drop)                    return Absorbed;");
            s.AppendLine("else                                          return Consumed;");
            s.AppendLine("```");
            s.AppendLine();

            int[,] m = new int[4, 3];
            for (int i = 0; i < eps.Count; i++) if (eps[i].Null >= 0) m[eps[i].Cls, eps[i].Null]++;

            s.AppendLine("Rows = `EpisodeClassifier` (175 lines, 4 thresholds). Columns = the null (3 lines, 1 threshold).");
            s.AppendLine();
            s.AppendLine("| classifier \\ null | ABSORBED | PULLED | CONSUMED | row n | agrees |");
            s.AppendLine("|---|---|---|---|---|---|");
            int agree = 0, classified = 0;
            for (int i = 0; i < 4; i++)
            {
                int rowN = m[i, 0] + m[i, 1] + m[i, 2];
                string ag = i < 3 && rowN > 0 ? Pc(m[i, i], rowN) : "n/a";
                if (i < 3) { agree += m[i, i]; classified += rowN; }
                s.AppendLine("| **" + Names[i] + "** | " + m[i, 0] + " | " + m[i, 1] + " | " + m[i, 2] + " | " + rowN + " | " + ag + " |");
            }
            int nullTot = 0; for (int j = 0; j < 3; j++) for (int i = 0; i < 4; i++) nullTot += m[i, j];
            s.AppendLine();
            s.AppendLine("Overall agreement, over the **" + classified + "** episodes the classifier actually labelled: **"
                         + Pc(agree, classified) + "**.");
            s.AppendLine("Over all " + nullTot + " resolved episodes (counting the classifier's refusals as disagreement): **"
                         + Pc(agree, nullTot) + "**.");
            s.AppendLine();

            int degenerate = 0, dropPos = 0, dropPosAgree = 0, dropPosClassified = 0;
            for (int i = 0; i < eps.Count; i++)
            {
                EpisodeRec e = eps[i];
                if (e.Drop == 0) degenerate++;
                else
                {
                    dropPos++;
                    if (e.Cls < 3) { dropPosClassified++; if (e.Cls == e.Null) dropPosAgree++; }
                }
            }
            s.AppendLine("**Two caveats on the null, both in its favour and both stated rather than fixed.**");
            s.AppendLine();
            s.AppendLine("1. `traded >= drop` is trivially TRUE when nothing dropped: **" + degenerate + " of " + eps.Count
                         + "** (" + Pc(degenerate, eps.Count) + ") episodes resolved with `drop == 0`, and the null calls every one of them ABSORBED.");
            s.AppendLine("   On the **" + dropPos + "** episodes where the wall actually lost size, agreement over the classifier's own labels is **"
                         + Pc(dropPosAgree, dropPosClassified) + "** (" + dropPosAgree + "/" + dropPosClassified + ").");
            s.AppendLine("2. Neither model emits PULLED on this tape: the null needs the level to empty with the quote >= 2 ticks away,");
            s.AppendLine("   and episodes are only ever opened on walls AT the inside (`D_approach = 1`), where that distance is 1 tick.");
            s.AppendLine();

            double rate = classified > 0 ? (double)agree / classified : double.NaN;
            s.Append("> **Verdict:** ");
            if (classified == 0)
                s.AppendLine("the classifier labelled nothing on this tape, so the null model wins by default — there is no moat to defend yet.");
            else if (rate >= 0.90)
                s.AppendLine("the null model reproduces **" + Pc(agree, classified) + "** of the classifier's labels. The 175 lines and 4 thresholds are not earning their complexity on this tape; three lines get the same picture.");
            else if (rate >= 0.75)
                s.AppendLine("the null model reproduces **" + Pc(agree, classified) + "** of the labels — most of the signal is naive, and the difference has to justify itself in question 3 or the complexity is not worth carrying.");
            else
                s.AppendLine("the null model reproduces only **" + Pc(agree, classified) + "** of the labels, so the classifier is genuinely doing something different. Whether that difference is *better* is question 3, not this one.");
            s.AppendLine();
        }

        // ---------------------------------------------------------------- Q3: forward information
        //
        // Three corrections over Phase 2, each of which changes what may be concluded:
        //   per SIDE (a pooled bucket has two opposite drift baselines and therefore none),
        //   no P(through) (CONSUMED is DEFINED as crossed, so that comparison is a tautology), and
        //   Holm over the whole declared grid (Phase 2 ran ~81 tests with no correction).
        // Plus a cluster bootstrap by wall, because several episodes sit on one wall and an i.i.d.
        // p-value counts each of them as fresh evidence.
        static readonly string[] HN = { "+10s", "+30s", "+60s" };

        // The pre-declared contrast grid. Fixed here, in code, so it cannot grow after the numbers
        // are seen. CONSUMED against each other label answers "is the glyph worth painting"; the
        // fourth answers "is ABSORBED different from painting nothing".
        static readonly int[,] Pairs = { { 2, 3 }, { 2, 0 }, { 2, 1 }, { 0, 3 } };

        static void Q3(StringBuilder s, Result r, List<EpisodeRec> eps)
        {
            H2(s, "3. Does the label carry forward information?");
            s.AppendLine();
            s.AppendLine("For every resolved episode: the mid at resolution, and the mid at +10 s / +30 s / +60 s of MARKET time.");
            s.AppendLine("`move` = signed ticks in the wall's through-direction, so positive always means *the wall gave way*.");
            s.AppendLine();
            s.AppendLine("1. **Per side, never pooled.** An ask wall giving way is price UP; a bid wall's is price DOWN. A pooled");
            s.AppendLine("   bucket is a blend of two opposite baselines, so the report's own instruction to read every move");
            s.AppendLine("   against the drift row cannot be followed for it. Phase 2's one significant result was ~31% bid/ask mix.");
            s.AppendLine("2. **`P(through)` is gone, at every horizon.** CONSUMED is *defined* as \"the inside quote crossed the");
            s.AppendLine("   level\", so it starts already through and every other bucket starts at 0%. Its huge z-scores measured");
            s.AppendLine("   that definition, not the future. `move` is a CHANGE from the mid at resolution and is the only");
            s.AppendLine("   column here that can carry forward information.");
            s.AppendLine("3. **Every mean-move test prints its 80%-power MDE.** A flat p from a test blind to a 3-tick effect is");
            s.AppendLine("   not evidence of no effect, and Phase 2 printed one such p with no marker.");
            s.AppendLine();

            double[] d = { Drift(r, 10), Drift(r, 30), Drift(r, 60) };
            s.AppendLine("**Drift control.** The same measurement with no episode in it: the mean forward move of the mid from");
            s.AppendLine("every one of the " + r.MidSeries.Count.ToString("N0") + " census samples on this tape, in the ASK-wall sign convention (price up = +).");
            s.AppendLine("An ask wall's move IS this number when the label carries nothing; a bid wall's is its negative. The");
            s.AppendLine("windows overlap, so this row's own effective n is far below the sample count — it sets the CENTRE that");
            s.AppendLine("`excess` is measured from, and no test below depends on its precision (a same-side contrast cancels it).");
            s.AppendLine();
            s.AppendLine("| | +10s | +30s | +60s |");
            s.AppendLine("|---|---|---|---|");
            s.AppendLine("| unconditional mean move, ticks | " + F(d[0], 2) + " | " + F(d[1], 2) + " | " + F(d[2], 2) + " |");
            s.AppendLine();

            for (int side = 1; side >= 0; side--)
            {
                double sign = side == 1 ? 1 : -1;
                s.AppendLine("**" + (side == 1 ? "Ask walls (resistance)" : "Bid walls (support)") + "** — `excess` = mean move minus this side's drift null ("
                             + F(sign * d[0], 2) + " / " + F(sign * d[1], 2) + " / " + F(sign * d[2], 2) + " ticks).");
                s.AppendLine();
                s.AppendLine("| outcome | n | walls | ep/wall | mean +10s | excess | mean +30s | excess | mean +60s | excess |");
                s.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
                for (int cls = 0; cls < 4; cls++)
                {
                    List<EpisodeRec> b = Bucket(eps, cls, side);
                    if (b.Count == 0) { s.AppendLine("| " + Names[cls] + " | 0 | - | - | - | - | - | - | - | - |"); continue; }
                    int cl = ClusterCount(b);
                    StringBuilder line = new StringBuilder();
                    line.Append("| " + Names[cls] + " | " + b.Count + " | " + cl + " | " + F((double)b.Count / Math.Max(1, cl), 1));
                    for (int h = 0; h < 3; h++)
                    {
                        List<double> mv = Moves(b, h);
                        if (mv.Count == 0) { line.Append(" | - | -"); continue; }
                        line.Append(" | " + F(Stats.Mean(mv), 2) + " (n" + mv.Count + ")");
                        line.Append(" | " + F(Stats.Mean(mv) - sign * d[h], 2));
                    }
                    line.Append(" |");
                    s.AppendLine(line.ToString());
                }
                s.AppendLine();
            }

            // ---- the tests
            int declared = 0;
            List<MoveTest> tests = new List<MoveTest>();
            for (int p = 0; p < Pairs.GetLength(0); p++)
                for (int side = 1; side >= 0; side--)
                    for (int h = 0; h < 3; h++)
                    {
                        declared++;
                        MoveTest t = MoveTest.Build(eps, Pairs[p, 0], Pairs[p, 1], side, h);
                        if (t != null) tests.Add(t);
                    }

            double[] raw = new double[tests.Count];
            for (int i = 0; i < tests.Count; i++) raw[i] = tests[i].P;
            double[] adj = Stats.Holm(raw);
            for (int i = 0; i < tests.Count; i++) tests[i].HolmP = adj[i];

            s.AppendLine("**Significance, corrected.** The grid is pre-declared in code (4 contrasts x 2 sides x 3 horizons = "
                         + declared + " tests);");
            s.AppendLine("**" + tests.Count + "** have >= 2 episodes with a read on both sides and are testable. Correction is **Holm-Bonferroni**");
            s.AppendLine("over those " + tests.Count + " — step-down, exact family-wise error control, no independence assumption. The smallest raw p");
            s.AppendLine("must clear **" + F(tests.Count > 0 ? 0.05 / tests.Count : double.NaN, 5) + "**, the next 0.05/" + Math.Max(1, tests.Count - 1)
                         + ", and so on; `holm p` is that comparison folded back onto the usual 0.05.");
            s.AppendLine();
            s.AppendLine("`MDE` is the smallest true difference these two n could detect at 80% power, alpha 0.05. A row whose");
            s.AppendLine("|diff| sits far under its MDE has not found nothing — it has not looked. `cluster` columns resample WALLS");
            s.AppendLine("with replacement (2 000 replicates, fixed seed): several episodes sit on one wall, so an i.i.d. p-value");
            s.AppendLine("counts each of them as fresh evidence and is optimistic. The cluster CI is the honest one.");
            s.AppendLine();
            s.AppendLine("| contrast | side | h | n / n | means | diff | MDE | t | raw p | holm p | walls | cluster 95% CI | cluster p |");
            s.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
            int survivors = 0, clusterSurvivors = 0;
            for (int i = 0; i < tests.Count; i++)
            {
                MoveTest t = tests[i];
                bool pass = t.HolmP <= 0.05;
                if (pass) survivors++;
                if (pass && !double.IsNaN(t.PCluster) && t.PCluster <= 0.05) clusterSurvivors++;
                s.AppendLine("| " + t.Pair + " | " + (t.Side == 1 ? "ask" : "bid") + " | " + HN[t.H] + " | "
                             + t.N1 + " / " + t.N2 + " | " + F(t.M1, 2) + " vs " + F(t.M2, 2) + " | " + F(t.Diff, 2) + " | "
                             + F(t.Mde, 2) + " | " + F(t.T, 2) + " | " + P(t.P) + " | " + P(t.HolmP) + (pass ? " **" : "") + " | "
                             + t.Clusters + " | [" + F(t.CiLo, 2) + ", " + F(t.CiHi, 2) + "] | " + P(t.PCluster) + " |");
            }
            s.AppendLine();

            int censored = 0, noWall = 0;
            for (int i = 0; i < eps.Count; i++) { if (!eps[i].Have[2]) censored++; if (eps[i].Wall == null) noWall++; }
            int allClusters = ClusterCount(eps);
            s.AppendLine("Censoring: " + censored + " of " + eps.Count + " episodes have no +60 s read (the tape ended first). Clustering: "
                         + eps.Count + " episodes sit on **" + allClusters + "** distinct walls (" + F(eps.Count / (double)Math.Max(1, allClusters), 1)
                         + " episodes per wall),");
            s.AppendLine("and they also cluster in TIME, which the wall bootstrap does not undo. Both push the same way: the");
            s.AppendLine("effective n is smaller than the printed n, so every raw p above is optimistic and every CI is too narrow.");
            s.AppendLine();
            if (tests.Count == 0)
                s.AppendLine("> **Nothing was testable.** No contrast had two episodes with a forward read on both sides.");
            else if (survivors == 0)
                s.AppendLine("> **Not one of the " + tests.Count + " contrasts survives Holm at 0.05.** On this tape the outcome label does not"
                             + " separate the forward move of the mid, on either side, at any of the three horizons.");
            else if (clusterSurvivors == 0)
                s.AppendLine("> **" + survivors + " of " + tests.Count + " contrasts survive Holm, and NONE of them survives the wall bootstrap.**"
                             + " That gap is the dependence: the i.i.d. test counted repeat episodes on one wall as independent evidence.");
            else
                s.AppendLine("> **" + clusterSurvivors + " of " + tests.Count + " contrasts survive BOTH Holm and the wall bootstrap.**"
                             + " Read the sign, the side and the horizon: a result that flips sign between sides or tapes is a session, not a signal.");
            s.AppendLine();
        }

        // ---------------------------------------------------------------- Q4: the ledger's honesty
        static void Q4(StringBuilder s, Result r, Result raised, List<EpisodeRec> eps)
        {
            H2(s, "4. Is the TRADED vs CANCELLED split trustworthy?");
            s.AppendLine();

            List<double> epDur = new List<double>(), wallAge = new List<double>();
            int overEp = 0, overWall = 0, both = 0, tradedOnly = 0, cancelledOnly = 0, neither = 0;
            for (int i = 0; i < eps.Count; i++)
            {
                EpisodeRec e = eps[i];
                double d = (e.ResolvedAt - e.OpenedAt).TotalSeconds;
                epDur.Add(d); wallAge.Add(e.WallAgeSec);
                if (d > r.RetentionSec) overEp++;
                if (e.WallAgeSec > r.RetentionSec) overWall++;
                bool t = e.Traded > 0, c = e.Cancelled > 0;
                if (t && c) both++; else if (t) tradedOnly++; else if (c) cancelledOnly++; else neither++;
            }

            s.AppendLine("| | |");
            s.AppendLine("|---|---|");
            Row(s, "TradeRetention", F(r.RetentionSec, 0) + " s (the indicator's value)");
            Row(s, "episode duration", Q(epDur, "s") + " — bounded by `T_episode` 3 s");
            Row(s, "episodes older than retention", overEp + " of " + eps.Count + " (" + Pc(overEp, eps.Count) + ")");
            Row(s, "WALL age at resolution", Q(wallAge, "s"));
            Row(s, "walls older than retention", overWall + " of " + eps.Count + " (" + Pc(overWall, eps.Count) + ")");
            s.AppendLine();
            s.AppendLine("The two ages answer two different questions. `EpisodeClassifier` sums trades from the episode's own");
            s.AppendLine("open time, which `T_episode` caps at 3 s, so its Traded is safe from pruning. The **ledger** is the");
            s.AppendLine("exposed one: `ConsumptionTracker.Read` sums from the wall's ARM time, and any wall older than the");
            s.AppendLine("retention window has had the trades that ate it deleted before they could be counted.");
            s.AppendLine();

            if (raised != null)
            {
                Dictionary<string, EpisodeRec> byKey = new Dictionary<string, EpisodeRec>();
                for (int i = 0; i < raised.Episodes.Count; i++)
                {
                    EpisodeRec e = raised.Episodes[i];
                    if (e.SameTick) continue;
                    byKey[e.ResolvedAt.Ticks + "/" + e.Side + "/" + e.Price] = e;
                }
                int matched = 0, moved = 0, glyphFlip = 0, outcomeFlip = 0;
                List<double> d1 = new List<double>(), d2 = new List<double>(), delta = new List<double>();
                for (int i = 0; i < eps.Count; i++)
                {
                    EpisodeRec a = eps[i]; EpisodeRec b;
                    if (!byKey.TryGetValue(a.ResolvedAt.Ticks + "/" + a.Side + "/" + a.Price, out b)) continue;
                    matched++;
                    d1.Add(a.Tbf); d2.Add(b.Tbf); delta.Add(b.Tbf - a.Tbf);
                    if (Math.Abs(b.Tbf - a.Tbf) > 0.05) moved++;
                    if (Glyph(a.Tbf) != Glyph(b.Tbf)) glyphFlip++;
                    if (a.Cls != b.Cls) outcomeFlip++;
                }
                s.AppendLine("**Re-run at TradeRetention " + F(raised.RetentionSec, 0) + " s** (longest wall lifetime on this tape: "
                             + F(Stats.Pct(r.Lifetimes, 1.0), 0) + " s).");
                s.AppendLine();
                s.AppendLine("| | |");
                s.AppendLine("|---|---|");
                Row(s, "episodes matched 1:1", matched + " of " + eps.Count + " (episode boundaries must not move; they did not)");
                Row(s, "outcome flips", outcomeFlip + " — retention does not reach `EpisodeClassifier`, as predicted above");
                Row(s, "mean TradeBackedFraction", F(Stats.Mean(d1), 3) + " -> " + F(Stats.Mean(d2), 3)
                        + "  (mean shift " + F(Stats.Mean(delta), 3) + ")");
                Row(s, "median TradeBackedFraction", F(Stats.Median(d1), 3) + " -> " + F(Stats.Median(d2), 3));
                Row(s, "TBF moved by > 0.05", moved + " of " + matched + " (" + Pc(moved, matched) + ")");
                Row(s, "**ledger glyph changes**", glyphFlip + " of " + matched + " (" + Pc(glyphFlip, matched)
                        + ") cross a §4 TBF boundary (0.15 / 0.85) and would be painted as a DIFFERENT death");
                s.AppendLine();
            }
            else
            {
                s.AppendLine("*(No second pass: re-run with `--retention2 <sec>` to measure the bias directly.)*");
                s.AppendLine();
            }

            s.AppendLine("**`W_assoc` (250 ms trade-association window, declared in `RadarConfig` and read by nothing).**");
            s.AppendLine("Attribution sums over the whole episode instead, so any episode containing BOTH a trade and a");
            s.AppendLine("cancellation can mis-split the two. How often that can bite:");
            s.AppendLine();
            s.AppendLine("| episode contains | n | share |");
            s.AppendLine("|---|---|---|");
            s.AppendLine("| trades AND cancellation (imprecision possible) | " + both + " | " + Pc(both, eps.Count) + " |");
            s.AppendLine("| trades only | " + tradedOnly + " | " + Pc(tradedOnly, eps.Count) + " |");
            s.AppendLine("| cancellation only | " + cancelledOnly + " | " + Pc(cancelledOnly, eps.Count) + " |");
            s.AppendLine("| neither (no size change) | " + neither + " | " + Pc(neither, eps.Count) + " |");
            s.AppendLine();
        }

        // ---------------------------------------------------------------- helpers
        // Mean forward move of the mid over `sec` of market time, across every census sample.
        // Two-pointer, so the control costs nothing next to the replay that produced the samples.
        static double Drift(Result r, double sec)
        {
            int j = 0, n = 0; double sum = 0;
            for (int i = 0; i < r.MidTicks.Count; i++)
            {
                long due = r.MidTicks[i] + (long)(sec * TimeSpan.TicksPerSecond);
                if (j < i) j = i;
                while (j < r.MidTicks.Count && r.MidTicks[j] < due) j++;
                if (j >= r.MidTicks.Count) break;
                sum += (r.MidSeries[j] - r.MidSeries[i]) / r.TickSize; n++;
            }
            return n > 0 ? sum / n : double.NaN;
        }

        static int Glyph(double tbf) { return tbf >= 0.85 ? 2 : (tbf <= 0.15 ? 0 : 1); }

        static string[] Wrap(string[] a, string p)
        {
            string[] o = new string[a.Length];
            for (int i = 0; i < a.Length; i++) o[i] = p + a[i];
            return o;
        }

        static void CensusRow(StringBuilder s, string label, List<double> v, double lo, double hi)
        {
            s.AppendLine("| " + label + " | " + F(Stats.Mean(v), 2) + " | " + F(Stats.Median(v), 0) + " | "
                         + F(Stats.Pct(v, 0.9), 0) + " | " + F(Stats.Pct(v, 1), 0) + " | "
                         + (lo < 0 ? "n/a" : Band(v, lo, hi)) + " |");
        }

        public static string NameOf(int cls) { return Names[cls]; }

        static int ClusterCount(List<EpisodeRec> b)
        {
            HashSet<string> h = new HashSet<string>();
            for (int i = 0; i < b.Count; i++) h.Add(b[i].Wall ?? "?");
            return h.Count;
        }

        public static List<EpisodeRec> Bucket(List<EpisodeRec> eps, int cls, int side)
        {
            List<EpisodeRec> o = new List<EpisodeRec>();
            for (int i = 0; i < eps.Count; i++)
            {
                if (eps[i].Cls != cls) continue;
                if (side >= 0 && (int)eps[i].Side != side) continue;
                if (eps[i].MidAtResolve <= 0) continue;
                o.Add(eps[i]);
            }
            return o;
        }

        // CountThrough is gone with P(through): a comparison whose t0 level is fixed by the label's
        // own definition cannot be a forward test, and printing it invited exactly the reading it
        // does not support.
        public static List<double> Moves(List<EpisodeRec> b, int h) { List<string> w; return Moves(b, h, out w); }

        // One implementation, so the values and their cluster ids can never fall out of step — the
        // bootstrap pairs them by index.
        public static List<double> Moves(List<EpisodeRec> b, int h, out List<string> walls)
        {
            List<double> o = new List<double>();
            walls = new List<string>();
            for (int i = 0; i < b.Count; i++)
            {
                if (!b[i].Have[h]) continue;
                double d = b[i].Mid[h] - b[i].MidAtResolve;
                o.Add((b[i].Side == Side.Ask ? d : -d) / TickOf(b[i]));
                walls.Add(b[i].Wall);
            }
            return o;
        }

        // Set once per report from the tape header. A hardcoded 0.25 is right for ES and NQ and
        // silently wrong for everything else, which is the worst kind of wrong.
        static double _tickSize = 0.25;
        static double TickOf(EpisodeRec e) { return _tickSize; }

        static string Q(List<double> v, string unit)
        {
            if (v == null || v.Count == 0) return "n/a";
            return "p50 " + F(Stats.Median(v), 1) + unit + ", p90 " + F(Stats.Pct(v, 0.9), 1) + unit
                   + ", max " + F(Stats.Pct(v, 1), 1) + unit + "  (n " + v.Count + ")";
        }

        static string Band(List<double> v, double lo, double hi)
        {
            if (v.Count == 0) return "n/a";
            int n = 0;
            for (int i = 0; i < v.Count; i++) if (v[i] >= lo && v[i] <= hi) n++;
            return Pc(n, v.Count);
        }

        // NaN and Infinity are not JSON numbers and System.Text.Json aborts the whole document on
        // one of them — which it did, at K_mult 4.0 on a tape with a one-cluster bucket, AFTER the
        // .md was already written. `null` is what "could not be computed" means in JSON, and every
        // consumer already reads it.
        static double? N(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? (double?)null : v; }

        static string Pc(int k, int n) { return n > 0 ? F(100.0 * k / n, 1) + "%" : "n/a"; }
        static string P(double p) { return double.IsNaN(p) ? "-" : (p < 1e-4 ? "<0.0001" : F(p, 4)); }
        static string F(double v, int d) { return double.IsNaN(v) ? "-" : v.ToString("F" + d, CultureInfo.InvariantCulture); }
        static void Row(StringBuilder s, string k, string v) { s.AppendLine("| " + k + " | " + v + " |"); }
        static void H(StringBuilder s, string t) { s.AppendLine("# " + t); }
        static void H2(StringBuilder s, string t) { s.AppendLine(); s.AppendLine("## " + t); }

        public static string Json(Result r, Result raised)
        {
            _tickSize = r.TickSize;
            List<EpisodeRec> eps = new List<EpisodeRec>();
            for (int i = 0; i < r.Episodes.Count; i++) if (!r.Episodes[i].SameTick) eps.Add(r.Episodes[i]);

            int[,] m = new int[4, 3];
            for (int i = 0; i < eps.Count; i++) if (eps[i].Null >= 0) m[eps[i].Cls, eps[i].Null]++;
            int agree = 0, classified = 0;
            int[] split = new int[4];
            for (int i = 0; i < eps.Count; i++) split[eps[i].Cls]++;
            for (int i = 0; i < 3; i++) { agree += m[i, i]; classified += m[i, 0] + m[i, 1] + m[i, 2]; }

            List<object> rows = new List<object>();
            for (int i = 0; i < 4; i++) rows.Add(new { classifier = Names[i], absorbed = m[i, 0], pulled = m[i, 1], consumed = m[i, 2] });

            // Per side, and with no p_through: both are the Phase 3 corrections, and a JSON that
            // still carried the pooled/tautological fields would let a future script re-derive the
            // exact conclusion this report retracts.
            List<object> fwd = new List<object>();
            string[] hn = { "10s", "30s", "60s" };
            for (int side = 1; side >= 0; side--)
                for (int cls = 0; cls < 4; cls++)
                {
                    List<EpisodeRec> b = Bucket(eps, cls, side);
                    Dictionary<string, object> h = new Dictionary<string, object>();
                    for (int k = 0; k < 3; k++)
                    {
                        List<double> mv = Moves(b, k);
                        h[hn[k]] = new
                        {
                            n = mv.Count,
                            mean_move_ticks = mv.Count > 0 ? Stats.Mean(mv) : (double?)null,
                            median_move_ticks = mv.Count > 0 ? Stats.Median(mv) : (double?)null
                        };
                    }
                    fwd.Add(new { side = side == 1 ? "ask" : "bid", outcome = Names[cls], n = b.Count, walls = ClusterCount(b), horizons = h });
                }

            List<object> mt = new List<object>();
            {
                List<MoveTest> tests = new List<MoveTest>();
                for (int p = 0; p < Pairs.GetLength(0); p++)
                    for (int side = 1; side >= 0; side--)
                        for (int k = 0; k < 3; k++)
                        {
                            MoveTest t = MoveTest.Build(eps, Pairs[p, 0], Pairs[p, 1], side, k);
                            if (t != null) tests.Add(t);
                        }
                double[] rawp = new double[tests.Count];
                for (int i = 0; i < tests.Count; i++) rawp[i] = tests[i].P;
                double[] adj = Stats.Holm(rawp);
                for (int i = 0; i < tests.Count; i++)
                    mt.Add(new
                    {
                        contrast = tests[i].Pair,
                        side = tests[i].Side == 1 ? "ask" : "bid",
                        horizon = hn[tests[i].H],
                        n1 = tests[i].N1, n2 = tests[i].N2,
                        diff_ticks = N(tests[i].Diff), mde_ticks = N(tests[i].Mde),
                        t = N(tests[i].T), p_raw = N(tests[i].P), p_holm = N(adj[i]),
                        walls = tests[i].Clusters,
                        cluster_ci = new double?[] { N(tests[i].CiLo), N(tests[i].CiHi) },
                        p_cluster = N(tests[i].PCluster)
                    });
            }

            List<double> wallAge = new List<double>();
            int overWall = 0, both = 0;
            for (int i = 0; i < eps.Count; i++)
            {
                wallAge.Add(eps[i].WallAgeSec);
                if (eps[i].WallAgeSec > r.RetentionSec) overWall++;
                if (eps[i].Traded > 0 && eps[i].Cancelled > 0) both++;
            }

            object retention = null;
            if (raised != null)
            {
                Dictionary<string, EpisodeRec> byKey = new Dictionary<string, EpisodeRec>();
                for (int i = 0; i < raised.Episodes.Count; i++)
                {
                    EpisodeRec e = raised.Episodes[i];
                    if (!e.SameTick) byKey[e.ResolvedAt.Ticks + "/" + e.Side + "/" + e.Price] = e;
                }
                int matched = 0, glyphFlip = 0, outcomeFlip = 0;
                List<double> d1 = new List<double>(), d2 = new List<double>();
                for (int i = 0; i < eps.Count; i++)
                {
                    EpisodeRec b; if (!byKey.TryGetValue(eps[i].ResolvedAt.Ticks + "/" + eps[i].Side + "/" + eps[i].Price, out b)) continue;
                    matched++; d1.Add(eps[i].Tbf); d2.Add(b.Tbf);
                    if (Glyph(eps[i].Tbf) != Glyph(b.Tbf)) glyphFlip++;
                    if (eps[i].Cls != b.Cls) outcomeFlip++;
                }
                retention = new
                {
                    raised_to_sec = raised.RetentionSec,
                    matched,
                    outcome_flips = outcomeFlip,
                    glyph_flips = glyphFlip,
                    tbf_mean_before = d1.Count > 0 ? Stats.Mean(d1) : (double?)null,
                    tbf_mean_after = d2.Count > 0 ? Stats.Mean(d2) : (double?)null
                };
            }

            var doc = new
            {
                generated = DateTime.Now.ToString("s"),
                tape = new
                {
                    file = Path.GetFileName(r.File),
                    instrument = r.Instrument,
                    tick = r.TickSize,
                    from = r.First.ToString("s"),
                    to = r.Last.ToString("s"),
                    minutes = (r.Last - r.First).TotalMinutes,
                    records = r.Records,
                    depth = r.Depth,
                    trades = r.Trades
                },
                config = new { hz = r.Hz, k_mult = r.K, min_abs_size = r.MinAbs, trade_retention_sec = r.RetentionSec },
                fidelity = new { ok = r.FidelityOk, mismatched = r.FidelityBad, same_tick_excluded = r.SameTick, no_node = r.NoNode },
                q1_plausibility = new
                {
                    walls_detected = r.WallsDetected,
                    walls_dead = r.Dead,
                    lifetime_p50_sec = N(Stats.Median(r.Lifetimes)),
                    lifetime_p90_sec = N(Stats.Pct(r.Lifetimes, 0.9)),
                    peak_p50 = N(Stats.Median(r.Peaks)),
                    census_solid_mean = N(Stats.Mean(r.Solid)),
                    census_hollow_mean = N(Stats.Mean(r.Hollow)),
                    census_undrawn_mean = N(Stats.Mean(r.Undrawn)),
                    census_solid_mean_band25 = N(Stats.Mean(r.SolidBand)),
                    census_hollow_mean_band25 = N(Stats.Mean(r.HollowBand)),
                    census_samples = r.CensusSamples,
                    cap_hit_solid = r.CapHitSolid,
                    cap_hit_hollow = r.CapHitHollow,
                    episodes = eps.Count,
                    split = new { absorbed = split[0], pulled = split[1], consumed = split[2], unclassified = split[3] }
                },
                q2_null_model = new
                {
                    confusion = rows,
                    classified = classified,
                    agree = agree,
                    agreement_rate = classified > 0 ? (double)agree / classified : (double?)null
                },
                q3_forward = fwd,
                q3_move_tests_holm = mt,
                q3_drift_control_ticks = new { h10 = N(Drift(r, 10)), h30 = N(Drift(r, 30)), h60 = N(Drift(r, 60)), census_samples = r.MidSeries.Count },
                q4_ledger = new
                {
                    wall_age_p50_sec = N(Stats.Median(wallAge)),
                    wall_age_max_sec = N(Stats.Pct(wallAge, 1)),
                    walls_older_than_retention = overWall,
                    episodes_with_trade_and_cancel = both,
                    retention_experiment = retention
                }
            };
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
