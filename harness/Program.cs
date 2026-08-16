using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using SizeMap.Engine;

namespace SizeMap.Harness
{
    // Headless renderer: .smr tape (or a seeded synthetic book) -> the SAME ColumnRing the
    // indicator fills -> the SAME Rasterizer.Rasterize the indicator calls -> a PNG.
    //
    // The point is the loop time. Without this, judging a palette or a coordinate change costs
    // edit -> copy to Custom/ -> F5 in the NinjaScript Editor -> restart the chart -> load Replay
    // -> play -> wait for a wall. Minutes, with a human in it, per pixel. Here it is `dotnet run`.
    // And because Rasterize is pure, what comes out is not a mockup of the indicator: it is the
    // indicator's own output, byte for byte, minus the blit.
    internal static class Program
    {
        const string Usage =
@"SizeMap headless harness — renders the real Rasterizer to a PNG.

  dotnet run --project harness -- [options]

  --in <file.smr>    recorded tape. Omitted => --synthetic.
  --synthetic        seeded fake book (price-anchored, persistent, two walls).
  --out <path>       PNG path                       (default out/sizemap.png)
  --w <px> --h <px>  panel size                     (default 1600 x 900)
  --ticks <n>        visible price rows             (default 100)
  --span <30s|12m>   visible time window            (default 100s)
  --at <HH:mm:ss>    with --in: stop the replay at this market time
  --levels <n>       synthetic depth levels/side    (default 10)
  --seed <n>         synthetic seed                 (default 7)
  --s0 <n>           ramp foot, robust p50 size     (default 8)
  --cap <n>          ramp ceiling size              (default 320)
  --bg <RRGGBB>      chart background               (default 1F1F1F, the measured NT8 dark)
  --bench <n>        rasterize n times and report   (default 20)";

        static int Main(string[] argv)
        {
            Opt o;
            try { o = Opt.Parse(argv); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message + "\n\n" + Usage); return 2; }
            if (o == null) { Console.WriteLine(Usage); return 0; }

            // cellsPerColumn has to hold both sides plus the walls that sit outside the quoted
            // window, or the ring starts evicting the very levels this exists to show.
            ColumnRing ring = new ColumnRing(7200, Math.Max(48, o.Levels * 2 + 16));
            Scene sc = o.In != null ? Tape.Replay(o.In, ring, o) : Synthetic.Build(ring, o);

            Palette pal = new Palette();
            pal.Rebuild(o.Bg);
            pal.SetScale(o.S0, o.Cap);

            float pxPerBucket = (float)(o.Width / (o.SpanSeconds * 1000.0 / ColumnRing.BucketMs));
            RasterView view = new RasterView(
                sc.AnchorRow, o.Height / 2f, o.Height / (float)o.Ticks,
                sc.NowTicks, o.Width, pxPerBucket);

            int[] px = new int[o.Width * o.Height];
            Rasterizer.Rasterize(ring, px, o.Width, o.Height, view, pal);   // JIT warm-up, discarded

            double[] ms = new double[o.Bench];
            Stopwatch sw = new Stopwatch();
            for (int i = 0; i < o.Bench; i++)
            {
                sw.Restart();
                Rasterizer.Rasterize(ring, px, o.Width, o.Height, view, pal);
                sw.Stop();
                ms[i] = sw.Elapsed.TotalMilliseconds;
            }
            Array.Sort(ms);

            Png.Write(o.Out, px, o.Width, o.Height);

            Console.WriteLine("source     {0}", sc.Desc);
            Console.WriteLine("columns    {0} fed, published {1}, overflows {2}, late drops {3}",
                sc.Columns, ring.PublishedIndex, ring.Overflows, ring.LateDrops);
            Console.WriteLine("view       {0}x{1}  {2} ticks @ {3:0.00} px/tick  {4:0.0}s @ {5:0.00} px/250ms",
                o.Width, o.Height, o.Ticks, o.Height / (float)o.Ticks, o.SpanSeconds, pxPerBucket);
            Console.WriteLine("ramp       s0 {0} cap {1}  bg #{2:X6}", o.S0, o.Cap, o.Bg & 0xFFFFFF);
            Console.WriteLine("rasterize  p50 {0:0.00} ms   max {1:0.00} ms   ({2} frames, budget ~60 ms)",
                ms[ms.Length / 2], ms[ms.Length - 1], o.Bench);
            Console.WriteLine("wrote      {0}  ({1} bytes)", o.Out, new FileInfo(o.Out).Length);
            return 0;
        }
    }

    internal struct Scene
    {
        public int AnchorRow;     // the tick row pinned to the vertical centre
        public long NowTicks;     // market time at the right edge
        public int Columns;
        public string Desc;
    }

    internal sealed class Opt
    {
        public string In, Out = Path.Combine("out", "sizemap.png");
        public int Width = 1600, Height = 900, Ticks = 100, Levels = 10, Seed = 7, Bench = 20;
        public double SpanSeconds = 100, S0 = 8, Cap = 320;
        public uint Bg = 0xFF1F1F1F;
        public TimeSpan? At;

        public static Opt Parse(string[] a)
        {
            Opt o = new Opt();
            for (int i = 0; i < a.Length; i++)
            {
                string k = a[i];
                switch (k)
                {
                    case "-h": case "--help": return null;
                    case "--synthetic": o.In = null; continue;
                }
                if (i + 1 >= a.Length) throw new ArgumentException("missing value for " + k);
                string v = a[++i];
                switch (k)
                {
                    case "--in": o.In = v; break;
                    case "--out": o.Out = v; break;
                    case "--w": o.Width = Int(v, k); break;
                    case "--h": o.Height = Int(v, k); break;
                    case "--ticks": o.Ticks = Int(v, k); break;
                    case "--levels": o.Levels = Int(v, k); break;
                    case "--seed": o.Seed = Int(v, k); break;
                    case "--bench": o.Bench = Math.Max(1, Int(v, k)); break;
                    case "--s0": o.S0 = Dbl(v, k); break;
                    case "--cap": o.Cap = Dbl(v, k); break;
                    case "--span": o.SpanSeconds = Span(v); break;
                    case "--at": o.At = TimeSpan.Parse(v, CultureInfo.InvariantCulture); break;
                    case "--bg": o.Bg = 0xFF000000u | Convert.ToUInt32(v.TrimStart('#'), 16); break;
                    default: throw new ArgumentException("unknown option " + k);
                }
            }
            if (o.Width < 16 || o.Height < 16) throw new ArgumentException("--w/--h must be >= 16");
            if (o.Ticks < 4) throw new ArgumentException("--ticks must be >= 4");
            if (o.SpanSeconds <= 0) throw new ArgumentException("--span must be > 0");
            if (o.In != null && !File.Exists(o.In)) throw new FileNotFoundException("no such tape: " + o.In);
            return o;
        }

        static int Int(string v, string k)
        {
            int r;
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r))
                throw new ArgumentException(k + " wants an integer, got " + v);
            return r;
        }

        static double Dbl(string v, string k)
        {
            double r;
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out r))
                throw new ArgumentException(k + " wants a number, got " + v);
            return r;
        }

        static double Span(string v)
        {
            double mult = 1;
            if (v.EndsWith("m", StringComparison.OrdinalIgnoreCase)) { mult = 60; v = v.Substring(0, v.Length - 1); }
            else if (v.EndsWith("s", StringComparison.OrdinalIgnoreCase)) { v = v.Substring(0, v.Length - 1); }
            return Dbl(v, "--span") * mult;
        }
    }

    // Deterministic across runs: Random(seed) uses the legacy compatible algorithm whenever a
    // seed is supplied, so a checked-in reference PNG stays reproducible.
    internal sealed class Rng
    {
        readonly Random _r;
        public Rng(int seed) { _r = new Random(seed); }
        public double NextDouble() { return _r.NextDouble(); }
        public int Next(int n) { return _r.Next(n); }

        public double Gauss(double mu, double sd)
        {
            double u1 = 1.0 - _r.NextDouble(), u2 = _r.NextDouble();
            return mu + sd * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        public double LogNormal(double mu, double sd) { return Math.Exp(Gauss(mu, sd)); }
    }

    // Replays a recorded .smr into the ring. Depth records are already absolute resting sizes per
    // (row, side), which is exactly ColumnRing's input, so there is no book to reconstruct here.
    internal static class Tape
    {
        public static Scene Replay(string path, ColumnRing ring, Opt o)
        {
            byte[] file = File.ReadAllBytes(path);
            RawHeader h;
            int n = RawFile.ReadAll(file, out h, null);
            if (n < 0) throw new InvalidDataException(path + ": not an .smr file (bad magic, or version > " + RawFile.Version + ")");
            RawRecord[] recs = new RawRecord[n];
            RawFile.ReadAll(file, out h, recs);

            long t0 = h.T0Ticks;
            int bid = ColumnRing.NoRow, ask = ColumnRing.NoRow, lastRow = 0;
            long last = 0, lastBucket = -1;
            int cols = 0, used = 0;

            for (int i = 0; i < n; i++)
            {
                RawRecord r = recs[i];
                if (r.Kind == RawKind.EpochBreak) { t0 = RawFile.EpochT0Of(r); continue; }

                long t = t0 + (long)r.DtMs * TimeSpan.TicksPerMillisecond;
                if (o.At.HasValue && new DateTime(t).TimeOfDay >= o.At.Value) break;

                if (r.Kind == RawKind.FeedReset)
                {
                    // A feed reset is not a book that emptied; the ring wipes rather than draw a
                    // cliff of zero-size levels that never happened.
                    ring.Reset(t);
                    bid = ask = ColumnRing.NoRow;
                    lastBucket = -1; cols = 0;
                }
                else if (r.Kind == RawKind.DepthBid || r.Kind == RawKind.DepthAsk)
                {
                    Side s = r.Kind == RawKind.DepthBid ? Side.Bid : Side.Ask;
                    ring.Accumulate(t, r.Row, s, r.Op == (byte)DepthOp.Remove ? 0 : r.Size);
                    if (r.Pos == 0)
                    {
                        if (s == Side.Bid) bid = r.Row; else ask = r.Row;
                        ring.SetInside(t, bid, ask);
                    }
                    lastRow = r.Row;
                    used++;
                }
                else continue;                       // trades and heartbeats: Phase 1 draws neither

                long b = ColumnRing.BucketOf(t);
                if (b != lastBucket) { lastBucket = b; cols++; }
                last = t;
            }

            if (last == 0) throw new InvalidDataException(path + ": no depth records before --at");

            // The head column is still open, and ColumnRing only publishes on a roll. One event in
            // the next bucket closes it, so the newest 250 ms is visible instead of missing.
            ring.SetInside(last + ColumnRing.BucketTicks, bid, ask);

            Scene sc = new Scene();
            sc.AnchorRow = bid != ColumnRing.NoRow && ask != ColumnRing.NoRow ? (bid + ask) / 2 : lastRow;
            sc.NowTicks = last;
            sc.Columns = cols;
            sc.Desc = string.Format(CultureInfo.InvariantCulture,
                "{0}  {1} records ({2} depth), tick {3}, t0 {4:yyyy-MM-dd HH:mm:ss}{5}",
                Path.GetFileName(path), n, used, h.TickSize, new DateTime(h.T0Ticks),
                h.IsReplay ? ", replay" : "");
            return sc;
        }
    }

    // A seeded fake book, so the harness works before a single minute of tape exists.
    //
    // The one thing that must not be cheap here: resting liquidity is PRICE-ANCHORED and
    // persistent — a level keeps its size and mean-reverts. Re-rolling sizes per column gives
    // confetti, and confetti makes a good design look broken. Behaviour ported from
    // docs/design/preview.py, which is where the visual spec was judged.
    internal static class Synthetic
    {
        struct Wall { public int Row, C0, C1, CDeath, Peak; public double Floor; }

        public static Scene Build(ColumnRing ring, Opt o)
        {
            Rng rng = new Rng(o.Seed);
            const int BaseRow = 100000;                          // NQ 25000.00 at a 0.25 tick
            int half = Math.Max(4, o.Ticks / 2 - 6);
            int cols = (int)Math.Ceiling(o.SpanSeconds * 1000.0 / ColumnRing.BucketMs) + 1;
            long b0 = new DateTime(2026, 8, 14, 14, 32, 0).Ticks / ColumnRing.BucketTicks;

            Wall[] walls = MakeWalls(rng, cols, BaseRow, half);
            Dictionary<int, int> resting = new Dictionary<int, int>();
            List<int> window = new List<int>();
            List<int> stale = new List<int>();

            double p = BaseRow, v = 0;
            int bid = BaseRow, ask = BaseRow + 1;
            long t = 0;

            for (int c = 0; c < cols; c++)
            {
                v = v * 0.90 + rng.Gauss(0, 1.0);
                p = Math.Max(BaseRow - half, Math.Min(BaseRow + half, p + v * 0.34));
                bid = (int)Math.Round(p);
                // The spread is 1 tick most of the time and occasionally widens, which is the whole
                // reason the envelope is drawn from two sides instead of one line.
                ask = bid + (rng.NextDouble() > 0.06 ? 1 : 2 + rng.Next(3));
                t = (b0 + c) * ColumnRing.BucketTicks;

                window.Clear();
                for (int lv = 0; lv < o.Levels; lv++) { window.Add(bid - lv); window.Add(ask + lv); }

                stale.Clear();
                foreach (KeyValuePair<int, int> kv in resting)
                    if (!InWindow(kv.Key, bid, ask, o.Levels)) stale.Add(kv.Key);
                for (int i = 0; i < stale.Count; i++) resting.Remove(stale[i]);   // scrolled out of the feed's vision

                for (int i = 0; i < window.Count; i++)
                {
                    int row = window[i];
                    int lv = Math.Min(Math.Abs(row - bid), Math.Abs(row - ask));
                    double b = 34.0 * Math.Exp(-lv * 0.050);
                    int sz, prev;
                    if (!resting.TryGetValue(row, out prev))
                        sz = (int)rng.LogNormal(Math.Log(b), 0.70);
                    else
                        sz = (int)(prev * 0.90 + b * Math.Exp(rng.Gauss(0, 0.10)) * 0.10 + rng.Gauss(0, 1.4));
                    if (sz < 1) sz = 1;
                    resting[row] = sz;
                    ring.Accumulate(t, row, row <= bid ? Side.Bid : Side.Ask, sz);
                }

                // Walls last, so they overwrite the level they sit on — last value wins per
                // (row, side) inside a bucket.
                for (int i = 0; i < walls.Length; i++)
                {
                    int sz = WallSize(walls[i], c);
                    if (sz <= 0 || !InWindow(walls[i].Row, bid, ask, o.Levels + 2)) continue;
                    ring.Accumulate(t, walls[i].Row, walls[i].Row <= bid ? Side.Bid : Side.Ask, sz);
                }

                ring.SetInside(t, bid, ask);
            }

            ring.SetInside(t + ColumnRing.BucketTicks, bid, ask);   // close the head column

            Scene sc = new Scene();
            sc.AnchorRow = BaseRow;
            sc.NowTicks = t;
            sc.Columns = cols;
            sc.Desc = string.Format(CultureInfo.InvariantCulture,
                "synthetic  seed {0}, {1} levels/side, {2} walls", o.Seed, o.Levels, walls.Length);
            return sc;
        }

        static bool InWindow(int row, int bid, int ask, int levels)
        {
            return row <= bid ? bid - row < levels : row - ask < levels;
        }

        // Two walls: one that gets eaten to a floor, one that mostly holds. Without at least one
        // wall the render is a pretty ribbon that says nothing about the product.
        static Wall[] MakeWalls(Rng rng, int cols, int baseRow, int half)
        {
            Wall a = new Wall();
            a.Row = baseRow + 6 + rng.Next(Math.Max(1, half - 8));
            a.C0 = cols / 5; a.C1 = cols; a.CDeath = cols * 3 / 4; a.Peak = 620; a.Floor = 0.30;

            Wall b = new Wall();
            b.Row = baseRow - 6 - rng.Next(Math.Max(1, half - 8));
            b.C0 = cols * 2 / 5; b.C1 = cols; b.CDeath = cols * 7 / 8; b.Peak = 280; b.Floor = 0.55;

            return new Wall[] { a, b };
        }

        static int WallSize(Wall w, int c)
        {
            if (c < w.C0 || c >= w.C1) return 0;
            double frac = c < w.CDeath ? 1.0 : Math.Max(0.0, 1.0 - (c - w.CDeath) / (double)Math.Max(1, w.C1 - w.CDeath));
            return (int)(w.Peak * (w.Floor + (1 - w.Floor) * frac));
        }
    }
}
