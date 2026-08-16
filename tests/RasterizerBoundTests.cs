using System;
using SizeMap.Engine;
using Xunit;

// The walk over the ring must be bound by what the PANEL can show, not by how much history the
// ring holds. Otherwise MinutesOfHistory is a frame-time dial disguised as a memory dial, and the
// whole-session view Javier wants is unreachable for a reason nobody can see in the settings.
//
// Everything here asserts on columns VISITED, never on wall-clock: a timing test on a shared
// machine is a coin flip, and the property that actually matters is the loop bound anyway.
//
// These pin the PROPERTY, not one mechanism. Two things deliver it — the explicit
// VisibleColumnLimit and the `xr <= 0` break — and today either alone is enough: a mutant that
// disables the limit still passes all three. That is not a weak test, it is the measurement:
// removing the SECOND of the two is what these catch, and nobody knows in advance which one a
// future rewrite will take out.
public class RasterizerBoundTests
{
    static readonly long B0 = new DateTime(2026, 8, 16, 14, 30, 0, DateTimeKind.Utc).Ticks;
    const long BT = ColumnRing.BucketTicks;
    const int W = 200, H = 64, Row = 100;

    // A ring wrapped right around, so every slot holds a published column and nothing exits the
    // walk early through `StartTicks == 0` — which is how every pre-existing test in this repo
    // accidentally avoided the bound being tested here.
    static int Render(int capacity, int visibleColumns, out int[] px)
    {
        ColumnRing ring = new ColumnRing(capacity, 8);
        for (int i = 0; i <= capacity; i++) ring.Accumulate(B0 + i * BT, Row, Side.Ask, 40);

        Palette pal = new Palette();
        pal.Rebuild(0xFF1F1F1F);
        pal.SetScale(8, 320);

        px = new int[W * H];
        RasterView v = new RasterView(Row, H / 2f, 1f, B0 + capacity * BT, W, W / (float)visibleColumns, 0.25);
        return Rasterizer.Rasterize(ring, px, W, H, v, pal);
    }

    [Fact]
    public void A_ring_100x_larger_costs_the_same_walk_for_the_same_visible_span()
    {
        int[] small, large;
        int visitedSmall = Render(200, 50, out small);
        int visitedLarge = Render(20000, 50, out large);

        Assert.Equal(visitedSmall, visitedLarge);
        Assert.True(visitedLarge <= 52,
            "50 columns fit the panel; the walk touched " + visitedLarge + " — it is still ring-bound");

        // And the pixels are identical, which is the half of the claim a column count cannot make:
        // the bound may only drop columns the `xr <= 0` break was going to drop anyway.
        Assert.Equal(small, large);
    }

    // The one-character way to "fix" the cost is to clip the walk short, so pin the opposite case
    // too: when the panel really does show the whole ring, the walk must reach cap - 1 and stop
    // there — never cap, because slot cap - 1 back is the open head the writer still owns.
    [Fact]
    public void When_the_whole_ring_fits_on_the_panel_the_walk_reaches_exactly_cap_minus_one()
    {
        int[] px;
        int visited = Render(200, 500, out px);
        Assert.Equal(199, visited);
    }

    // Degenerate but reachable: a panel scrolled so far that nothing is left of x = 0. Walking the
    // ring to discover that is exactly the waste this bound exists to remove.
    [Fact]
    public void Nothing_visible_means_nothing_walked()
    {
        ColumnRing ring = new ColumnRing(5000, 8);
        for (int i = 0; i <= 5000; i++) ring.Accumulate(B0 + i * BT, Row, Side.Ask, 40);

        Palette pal = new Palette();
        pal.Rebuild(0xFF1F1F1F);
        pal.SetScale(8, 320);

        int[] px = new int[W * H];
        RasterView v = new RasterView(Row, H / 2f, 1f, B0 + 5000 * BT, 0f, 4f, 0.25);
        Assert.True(Rasterizer.Rasterize(ring, px, W, H, v, pal) <= 1);
    }
}
