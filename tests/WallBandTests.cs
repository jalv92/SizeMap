using System;
using System.Collections.Generic;
using SizeMap.Engine;
using Xunit;

namespace SizeMap.Tests
{
    // The wall-band layer, pinned at the pixel. Every assertion here is a sentence from
    // visual-spec §4 turned into an int comparison — because "observed is solid, remembered is
    // hollow" is a claim about pixels, and a claim about pixels that nothing checks is a wish.
    public class WallBandTests
    {
        const long BT = ColumnRing.BucketTicks;
        const long B0 = 1000 * BT;
        const uint Bg = 0xFF1F1F1F;
        const double Tick = 0.25;

        static Palette Pal()
        {
            var p = new Palette();
            p.Rebuild(Bg);
            p.SetScale(8, 320);
            return p;
        }

        // 40x20 panel, 4 px per tick, 4 px per 250 ms column, now at the right edge.
        // Row 100 (price 25.00) occupies y [8,12); its grooves are y 7 and y 12.
        static RasterView View()
        {
            return new RasterView(100, 10f, 4f, B0, 40f, 4f, Tick);
        }

        const int W = 40, H = 20, Y0 = 8, Y1 = 12;

        // Born 4 buckets ago => the column's left edge is x 20; the band runs 20..39.
        const int XBirth = 20;

        static int At(int[] px, int x, int y) { return px[y * W + x]; }
        static int Ink { get { return unchecked((int)Palette.Ink); } }
        static int Text { get { return unchecked((int)Palette.Text); } }
        static int BgI { get { return unchecked((int)Bg); } }

        static RadarNode Wall(long size, long peak, double conf = 0.9)
        {
            return new RadarNode
            {
                Price = 25.00, Side = Side.Ask, LastKnownSize = size, PeakSize = peak,
                State = NodeState.Wall, RawState = NodeState.Wall, Confidence = conf,
                InWindow = true, FirstSeenTicks = B0 - 4 * BT
            };
        }

        static RadarNode Remembered(long size, double conf, Side side = Side.Ask)
        {
            return new RadarNode
            {
                Price = 25.00, Side = side, LastKnownSize = size, PeakSize = size,
                State = NodeState.Remembered, RawState = NodeState.Wall, Confidence = conf,
                InWindow = false, FirstSeenTicks = B0 - 4 * BT
            };
        }

        static int[] Render(IReadOnlyList<RadarNode> nodes, ColumnRing ring = null)
        {
            var px = new int[W * H];
            Rasterizer.Rasterize(ring ?? new ColumnRing(8, 8), px, W, H, View(), Pal(), nodes);
            return px;
        }

        // ---------------------------------------------------------------- confirmed walls

        [Fact]
        public void A_confirmed_wall_gets_an_ink_groove_above_below_and_a_left_cap()
        {
            var px = Render(new List<RadarNode> { Wall(400, 400) });

            for (int x = XBirth; x < W; x++)
            {
                Assert.Equal(Ink, At(px, x, Y0 - 1));   // immediately above the band
                Assert.Equal(Ink, At(px, x, Y1));       // immediately below
            }
            for (int y = Y0 - 1; y <= Y1; y++)
                Assert.Equal(Ink, At(px, XBirth, y));   // the left cap at the birth column

            Assert.Equal(BgI, At(px, XBirth - 1, Y0 - 1));   // nothing before it was born
            Assert.Equal(BgI, At(px, XBirth + 5, Y0 - 2));   // and the groove is exactly 1 px
        }

        [Fact]
        public void The_groove_overwrites_the_heat_under_it()
        {
            // A groove that lost to the heat field would vanish on exactly the busy chart where it
            // is the only thing separating one wall from the level above it.
            var ring = new ColumnRing(8, 8);
            ring.Accumulate(B0 - 4 * BT, 101, Side.Ask, 300);   // row 101 covers y [4,8): its bottom
            ring.Accumulate(B0, 101, Side.Ask, 300);            //   pixel row is 7 = the wall's groove
            ring.Accumulate(B0 + BT, 0, Side.Ask, 1);           // publish

            var px = Render(new List<RadarNode> { Wall(400, 400) }, ring);
            Assert.Equal(Ink, At(px, 30, Y0 - 1));
        }

        [Fact]
        public void The_groove_is_suppressed_below_three_px_per_tick()
        {
            // Two ink rules around a 2 px band erase the band they exist to frame.
            var px = new int[W * H];
            Rasterizer.Rasterize(new ColumnRing(8, 8), px, W, H,
                new RasterView(100, 10f, 2f, B0, 40f, 4f, Tick), Pal(),
                new List<RadarNode> { Wall(300, 300) });   // under sCap: no saturation mark either

            for (int i = 0; i < px.Length; i++) Assert.Equal(BgI, px[i]);
        }

        // ---------------------------------------------------------------- remembered walls

        [Fact]
        public void A_remembered_wall_writes_no_fill_only_two_rules()
        {
            // THE load-bearing rule: observed is solid, remembered is hollow. A fill here — at any
            // alpha, at any stipple — is the failure this whole layer is shaped to avoid.
            var pal = Pal();
            var px = Render(new List<RadarNode> { Remembered(120, 1.0) });
            int rule = unchecked((int)pal.ColourOf(120));

            for (int x = XBirth; x < W; x++)
            {
                Assert.Equal(rule, At(px, x, Y0));         // top rule
                Assert.Equal(rule, At(px, x, Y1 - 1));     // bottom rule
                Assert.Equal(BgI, At(px, x, Y0 + 1));      // and NOTHING between them
                Assert.Equal(BgI, At(px, x, Y0 + 2));
            }
        }

        [Fact]
        public void The_remembered_rule_colour_is_the_size_at_last_observation_and_never_decays()
        {
            // Two orthogonal channels: colour carries magnitude, dash carries confidence. A faint
            // memory of a 900-lot wall must be the SAME colour as a certain one — only dashier.
            var pal = Pal();
            var sure = Render(new List<RadarNode> { Remembered(900, 1.0) });
            var faint = Render(new List<RadarNode> { Remembered(900, 0.3) });
            int expected = unchecked((int)pal.ColourOf(900));

            Assert.Equal(expected, At(sure, XBirth, Y0));
            Assert.Equal(expected, At(faint, XBirth, Y0));   // x = phase 0, painted at every duty
        }

        [Fact]
        public void The_side_tick_points_up_for_an_ask_and_down_for_a_bid()
        {
            var pal = Pal();
            int rule = unchecked((int)pal.ColourOf(120));

            var ask = Render(new List<RadarNode> { Remembered(120, 1.0, Side.Ask) });
            for (int k = 1; k <= 3; k++) Assert.Equal(rule, At(ask, XBirth, Y0 - k));
            Assert.Equal(BgI, At(ask, XBirth, Y1));

            var bid = Render(new List<RadarNode> { Remembered(120, 1.0, Side.Bid) });
            for (int k = 1; k <= 3; k++) Assert.Equal(rule, At(bid, XBirth, Y1 - 1 + k));
            Assert.Equal(BgI, At(bid, XBirth, Y0 - 1));
        }

        // ---------------------------------------------------------------- confidence -> dash duty

        [Theory]
        [InlineData(1.00, 8)]    // solid
        [InlineData(0.875, 8)]
        [InlineData(0.80, 6)]    // long dash
        [InlineData(0.625, 6)]   // the band is lower-inclusive: ToEven rounding would say 4 here
        [InlineData(0.50, 4)]    // even dash
        [InlineData(0.375, 4)]
        [InlineData(0.30, 2)]    // dotted
        [InlineData(0.25, 2)]
        [InlineData(0.24, 0)]    // below the floor: gone
        public void Dash_duty_maps_to_the_documented_pattern(double confidence, int expectedOn)
        {
            Assert.Equal(expectedOn, Rasterizer.DashOn(confidence));
        }

        [Fact]
        public void The_dash_is_painted_on_an_eight_px_period_phased_on_the_birth_column()
        {
            var pal = Pal();
            var px = Render(new List<RadarNode> { Remembered(120, 0.5) });   // on = 4 of 8
            int rule = unchecked((int)pal.ColourOf(120));

            for (int x = XBirth; x < W; x++)
            {
                bool on = ((x - XBirth) % 8) < 4;
                Assert.Equal(on ? rule : BgI, At(px, x, Y0));
            }
        }

        [Fact]
        public void Below_the_confidence_floor_the_band_is_not_drawn_at_all()
        {
            var px = Render(new List<RadarNode> { Remembered(900, 0.20) });
            for (int i = 0; i < px.Length; i++) Assert.Equal(BgI, px[i]);
        }

        // ---------------------------------------------------------------- saturation

        [Fact]
        public void A_wall_over_sCap_gets_two_verticals_with_a_one_px_gap()
        {
            // sCap is 320 here. Without this mark the ramp reports 2000 lots and 340 lots in the
            // same colour and says nothing about it.
            var px = Render(new List<RadarNode> { Wall(2000, 2000) });
            for (int y = Y0; y < Y1; y++)
            {
                Assert.Equal(Text, At(px, W - 4, y));
                Assert.Equal(Text, At(px, W - 2, y));
                Assert.NotEqual(Text, At(px, W - 3, y));   // the 1 px gap is what makes it read as a mark
            }
        }

        [Fact]
        public void A_wall_under_sCap_gets_no_saturation_mark()
        {
            var px = Render(new List<RadarNode> { Wall(300, 300) });
            for (int y = Y0; y < Y1; y++) Assert.NotEqual(Text, At(px, W - 4, y));
        }

        // ---------------------------------------------------------------- the caps

        [Fact]
        public void The_remembered_cap_holds_at_24_and_keeps_the_highest_confidence_times_peak()
        {
            // A hard cap, not a fade: 30 candidates in, 24 bands out, and the 6 dropped are the 6
            // least sure smallest ones. Ranking by score and then clipping is what stops a 40-level
            // chart from turning into line soup.
            const int Rows = 30, PanelH = 80, PanelW = 20;
            var view = new RasterView(100, 60f, 1f, B0, PanelW, 4f, Tick);
            var nodes = new List<RadarNode>();
            for (int i = 0; i < Rows; i++)
                nodes.Add(new RadarNode
                {
                    Price = (100 + i) * Tick, Side = Side.Ask, LastKnownSize = 120,
                    PeakSize = 100 + i, State = NodeState.Remembered, RawState = NodeState.Wall,
                    Confidence = 0.30 + i * 0.02, InWindow = false, FirstSeenTicks = B0 - 4 * BT
                });

            var px = new int[PanelW * PanelH];
            Rasterizer.Rasterize(new ColumnRing(8, 8), px, PanelW, PanelH, view, Pal(), nodes);

            int drawn = 0;
            for (int i = 0; i < Rows; i++)
            {
                int y = (int)Math.Floor(60f - (i + 0.5f));
                bool painted = At2(px, PanelW, XBirth % PanelW, y) != BgI;
                if (painted) drawn++;
                // score rises with i, so the six weakest (i < 6) are the ones dropped
                Assert.Equal(i >= Rows - 24, painted);
            }
            Assert.Equal(24, drawn);
        }

        [Fact]
        public void The_grooved_cap_holds_at_12_by_peak()
        {
            const int Rows = 20, PanelH = 200, PanelW = 20;
            var view = new RasterView(100, 190f, 3f, B0, PanelW, 4f, Tick);
            var nodes = new List<RadarNode>();
            for (int i = 0; i < Rows; i++)
                nodes.Add(new RadarNode
                {
                    Price = (100 + i) * Tick, Side = Side.Ask, LastKnownSize = 400,
                    PeakSize = 100 + i, State = NodeState.Wall, RawState = NodeState.Wall,
                    Confidence = 0.9, InWindow = true, FirstSeenTicks = B0 - 4 * BT
                });

            var px = new int[PanelW * PanelH];
            Rasterizer.Rasterize(new ColumnRing(8, 8), px, PanelW, PanelH, view, Pal(), nodes);

            int drawn = 0;
            for (int i = 0; i < Rows; i++)
            {
                int y0 = (int)Math.Floor(190f - (i + 0.5f) * 3f);
                bool painted = At2(px, PanelW, PanelW - 1, y0 - 1) == Ink;
                if (painted) drawn++;
                Assert.Equal(i >= Rows - 12, painted);   // peak rises with i
            }
            Assert.Equal(12, drawn);
        }

        [Fact]
        public void A_live_but_unconfirmed_level_is_never_grooved_and_never_remembered()
        {
            // Only confirmed walls are ever tracked as objects. A plain level is heat and nothing
            // else — the rule that keeps a 40-level chart readable.
            var live = Wall(400, 400);
            live.State = NodeState.Live;
            var px = Render(new List<RadarNode> { live });
            for (int i = 0; i < px.Length; i++) Assert.Equal(BgI, px[i]);
        }

        static int At2(int[] px, int w, int x, int y) { return px[y * w + x]; }
    }
}
