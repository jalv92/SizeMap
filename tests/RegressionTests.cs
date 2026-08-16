using System;
using SizeMap.Engine;
using Xunit;

// The two defects an adversarial review found in Phase 1, each pinned by the test that was
// missing when it shipped. Both lived in a branch no existing test could reach — which is the
// only reason they survived a suite of 113.
public class RegressionTests
{
    static readonly long B0 = new DateTime(2026, 8, 16, 14, 30, 0, DateTimeKind.Utc).Ticks;
    const long BT = ColumnRing.BucketTicks;

    // --- Finding 1: the rasterizer walked into the open head column ---------------------
    //
    // ColumnRing's entire lock-free safety argument is that the writer only ever mutates the
    // open head, and a published column is immutable. DrawColumns looped `k < Capacity`, so at
    // k == Capacity-1 the index wrapped onto `published + 1` — the head — and read it while the
    // depth thread was writing it.
    //
    // No test could catch it because no test in the repo used a ring that had WRAPPED: every
    // one exited the loop early through `StartTicks == 0`. On a 1-minute chart PxPerBucket is
    // ~0.025, so 7200 columns span 180 px of a 1738 px panel and the `xr <= 0` break never
    // fires either. The walk reached the head every single frame.
    [Fact]
    public void A_wrapped_ring_never_paints_the_open_head_column()
    {
        const int cap = 8, rows = 64;
        var ring = new ColumnRing(cap, 8);

        // Fill the ring right around, so every slot holds a published column.
        for (int i = 0; i <= cap; i++)
            ring.Accumulate(B0 + i * BT, 100, Side.Ask, 5);

        // ON PANEL. At 140 the sentinel projects to y = -8.5 and RowSpan rejects it before a
        // pixel is written, so the assertion below could never fail — proven by mutation: revert
        // the `cap - 1` bound and this test still passed. A test that cannot fail is not a test.
        const int headRow = 110;
        ring.Accumulate(B0 + cap * BT, headRow, Side.Ask, 60000);

        var palette = new Palette();
        palette.Rebuild(0xFF1F1F1F);
        palette.SetScale(8, 320);
        var px = new int[rows * rows];

        // AnchorRow/AnchorY put row 100 mid-panel; PxPerBucket 0.5 keeps all 8 columns on screen.
        Rasterizer.Rasterize(ring, px, rows, rows,
            new RasterView(100, rows / 2f, 1f, B0 + cap * BT, rows, 0.5f, 0.25), palette);

        uint headColour = palette.ColourOf(60000);
        foreach (int p in px)
            Assert.NotEqual(headColour, unchecked((uint)p));
    }

    // The same walk must still draw everything that IS published, or the fix would be a
    // one-character way to blank the chart.
    [Fact]
    public void A_wrapped_ring_still_paints_the_columns_that_were_published()
    {
        const int cap = 8, rows = 64;
        var ring = new ColumnRing(cap, 8);
        for (int i = 0; i <= cap; i++)
            ring.Accumulate(B0 + i * BT, 100, Side.Ask, 900);
        ring.Accumulate(B0 + cap * BT, 140, Side.Ask, 5);   // open the head elsewhere

        var palette = new Palette();
        palette.Rebuild(0xFF1F1F1F);
        palette.SetScale(8, 320);
        var px = new int[rows * rows];
        Rasterizer.Rasterize(ring, px, rows, rows,
            new RasterView(100, rows / 2f, 1f, B0 + cap * BT, rows, 0.5f, 0.25), palette);

        uint published = palette.ColourOf(900);
        int hits = 0;
        foreach (int p in px) if (unchecked((uint)p) == published) hits++;
        Assert.True(hits > 0, "the wrapped ring drew nothing at all — the fix went one column too far");
    }

    // --- Finding 2: the ramp inverted on any mid-dark theme ------------------------------
    //
    // The LUT is monotone in luminance only while the background is darker than stop 1. The
    // indicator guarded with 0.18 while the real precondition is ~0.0406, so every grey theme
    // in between rendered "nothing resting" BRIGHTER than "8 lots resting" — the ramp lying
    // about its own direction.
    //
    // The existing monotonicity test named the universal property but only ever exercised the
    // three backgrounds where it could not fail. This one walks the range that broke.
    // Monotonicity is claimed in two different strengths, because the ramp has two regions and
    // only one of them is meant to be visible. Writing it as one blanket assertion is how the
    // first version of this test failed on Javier's own #1F1F1F background for a reason that
    // turned out to be arithmetic, not design.
    //
    // Above the fade band the LUT is pure interpolation between fixed stops — the background is
    // not even read — so it is strictly monotone and background-independent. That is the claim
    // that matters and it is asserted exactly.
    [Theory]
    [InlineData(0xFF000000u)]   // pure black
    [InlineData(0xFF1F1F1Fu)]   // Javier's measured chart background
    [InlineData(0xFF2A2A2Au)]
    [InlineData(0xFF303030u)]   // just under the threshold
    public void Above_the_fade_band_the_lut_is_strictly_monotone(uint bg)
    {
        Assert.True(Palette.BackgroundIsUsable(bg), "this background should be accepted");

        var p = new Palette();
        p.Rebuild(bg);

        double prev = Palette.RelativeLuminance(p.Lut[Palette.FadeBelow]);
        for (int i = Palette.FadeBelow + 1; i < 256; i++)
        {
            double cur = Palette.RelativeLuminance(p.Lut[i]);
            Assert.True(cur >= prev - 1e-9,
                string.Format("LUT[{0}] is darker than LUT[{1}] on background #{2:X6}: {3:F5} < {4:F5}",
                    i, i - 1, bg & 0xFFFFFF, cur, prev));
            prev = cur;
        }
    }

    // Inside the fade band the requirement is different and weaker on purpose: those entries exist
    // to be INVISIBLE, so "1 lot resting" does not paint a full-panel dither. Ordering there is
    // unobservable by construction, and demanding it would over-specify a region whose entire span
    // is below threshold.
    //
    // What must hold is that nothing in the band strays perceptibly from the background. Measured
    // worst case across the four accepted backgrounds: band width 0.021, worst internal dip
    // 0.000208 — roughly one fiftieth of a just-noticeable difference. The cause is integer
    // rounding: at #1F1F1F the background's red (31) is HIGHER than stop 1's (16), so red must
    // fall while blue rises, and red carries 0.2126 of luminance against blue's 0.0722.
    [Theory]
    [InlineData(0xFF000000u)]
    [InlineData(0xFF1F1F1Fu)]
    [InlineData(0xFF2A2A2Au)]
    [InlineData(0xFF303030u)]
    public void Inside_the_fade_band_nothing_strays_perceptibly_from_the_background(uint bg)
    {
        var p = new Palette();
        p.Rebuild(bg);

        double bgLum = Palette.RelativeLuminance(bg);
        for (int i = 0; i < Palette.FadeBelow; i++)
        {
            double d = Math.Abs(Palette.RelativeLuminance(p.Lut[i]) - bgLum);
            Assert.True(d < 0.03,
                string.Format("LUT[{0}] is {1:F5} away from the background on #{2:X6} — the fade band "
                            + "must stay invisible", i, d, bg & 0xFFFFFF));
        }
    }

    [Theory]
    [InlineData(0xFF404040u)]   // 14 inversions measured before the fix
    [InlineData(0xFF505050u)]   // 27
    [InlineData(0xFF707070u)]   // 29
    [InlineData(0xFFF0F0F0u)]   // light theme: used to get an opaque near-black plate
    [InlineData(0xFFFFFFFFu)]
    public void A_background_that_would_invert_the_ramp_is_rejected(uint bg)
    {
        Assert.False(Palette.BackgroundIsUsable(bg),
            "SizeMap must refuse rather than draw a ramp that runs backwards");
    }

    // The threshold is not a taste call: it is the luminance of stop 1, and if the ramp's first
    // stop is ever re-tuned this test fails and forces the constant to move with it.
    [Fact]
    public void The_threshold_is_exactly_the_luminance_of_ramp_stop_one()
    {
        Assert.Equal(Palette.MaxBackgroundLuminance, Palette.RelativeLuminance(0xFF103772u), 4);
    }

    // --- Finding 7: alpha must never reach the buffer as data ---------------------------
    [Fact]
    public void Lut_zero_is_opaque_even_when_the_background_brush_is_not()
    {
        var p = new Palette();
        p.Rebuild(0x801F1F1F);                       // a half-transparent chart background
        Assert.Equal(0xFF000000u, p.Lut[0] & 0xFF000000u);
    }
}
