using System;
using SizeMap.Engine;
using Xunit;

// The distinction Phase 3 died for lack of, pinned.
//
// Every consumer asks the book for the size at a price and gets 0 for anything absent. But 0 means
// two different things — "this level emptied" and "this level is past the far end of a window that
// only holds N levels" — and on 5.8 M records of ES the second is 85-92% of every wall
// disappearance, with a median 230 lots still resting and 89-92% of them back within a second at
// >= 90% of the size they left with.
//
// These tests do not change what the chart draws today. They pin the capability the next
// measurement needs, on live 40-level tape, where the far end moves from ~2.25 points to ~10.
public class WindowVisibilityTests
{
    static readonly DateTime T = new DateTime(2026, 8, 16, 14, 30, 0, DateTimeKind.Utc);
    const double Tick = 0.25;

    // Size at a price, the way every consumer gets it: scan the visible ladder. The point of these
    // tests is that this returns 0 for BOTH "emptied" and "scrolled out", which is the ambiguity.
    static long SizeAt(BookMirror b, Side side, double price)
    {
        var levels = b.Levels(side);
        for (int i = 0; i < levels.Count; i++)
            if (Math.Abs(levels[i].Price - price) < Tick / 2.0) return levels[i].Volume;
        return 0;
    }

    static BookMirror TenDeep()
    {
        var b = new BookMirror(Tick, TimeSpan.FromSeconds(30));
        // Ten levels a side, contiguous at one level per tick — which is what ES actually is:
        // the measured ladder reach is 9 ticks for 10 levels.
        for (int i = 0; i < 10; i++)
        {
            b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = i,
                Price = 7800.00 - i * Tick, Volume = 70, Time = T });
            b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = i,
                Price = 7800.25 + i * Tick, Volume = 70, Time = T });
        }
        return b;
    }

    [Fact]
    public void A_price_inside_the_ladder_is_visible_on_both_sides()
    {
        var b = TenDeep();
        Assert.True(b.IsWithinWindow(Side.Bid, 7800.00));   // the touch
        Assert.True(b.IsWithinWindow(Side.Bid, 7797.75));   // the far end, 9 ticks down
        Assert.True(b.IsWithinWindow(Side.Ask, 7800.25));
        Assert.True(b.IsWithinWindow(Side.Ask, 7802.50));
    }

    [Fact]
    public void A_price_past_the_far_end_is_not_visible_and_a_size_of_zero_there_means_nothing()
    {
        var b = TenDeep();
        Assert.False(b.IsWithinWindow(Side.Bid, 7797.50));  // one tick past the deepest bid
        Assert.False(b.IsWithinWindow(Side.Ask, 7802.75));
        // And this is the whole point: the book reports 0 for that price either way.
        Assert.Equal(0, SizeAt(b, Side.Bid, 7797.50));
    }

    // The exact event that produced 79,188 of the 79,192 "wall vanished" readings on part23: a new
    // level arrives at the touch, the ladder slides, and the deepest one leaves WITH ITS SIZE
    // INTACT. Nothing was cancelled and nothing was traded.
    [Fact]
    public void A_level_that_scrolls_out_is_reported_absent_but_is_no_longer_claimed_visible()
    {
        var b = TenDeep();
        const double deepest = 7797.75;
        Assert.True(b.IsWithinWindow(Side.Bid, deepest));
        Assert.Equal(70, SizeAt(b, Side.Bid, deepest));

        // Price ticks up: a new bid appears at the touch, the far end is pushed out. This is the
        // feed's own shape — an Add at position 0 and a Remove at the last position carrying a
        // NON-ZERO size, versus a genuine empty which always carries size 0.
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Add, Position = 0,
            Price = 7800.25, Volume = 70, Time = T.AddMilliseconds(10) });
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Remove, Position = 10,
            Price = deepest, Volume = 70, Time = T.AddMilliseconds(10) });

        Assert.Equal(0, SizeAt(b, Side.Bid, deepest));          // absent...
        Assert.False(b.IsWithinWindow(Side.Bid, deepest));            // ...but NOT observed empty
    }

    // The contrast that makes the test above mean something: a level that genuinely empties inside
    // the window stays visible, so a size of 0 there IS an observation.
    [Fact]
    public void A_level_that_genuinely_empties_inside_the_window_stays_visible()
    {
        var b = TenDeep();
        const double inside = 7799.25;                                 // 3 ticks down, mid-ladder
        b.ApplyDepth(new DepthEvent { Side = Side.Bid, Op = DepthOp.Remove, Position = 3,
            Price = inside, Volume = 0, Time = T.AddMilliseconds(10) });

        Assert.Equal(0, SizeAt(b, Side.Bid, inside));
        Assert.True(b.IsWithinWindow(Side.Bid, inside),
            "a price between the touch and the far end is observed, so zero there is real");
    }

    [Fact]
    public void An_empty_or_reset_book_claims_nothing_is_visible()
    {
        var b = new BookMirror(Tick, TimeSpan.FromSeconds(30));
        Assert.False(b.IsWithinWindow(Side.Bid, 7800.00));

        var f = TenDeep();
        f.ApplyDepth(new DepthEvent { IsReset = true, Time = T.AddSeconds(1) });
        Assert.False(f.IsWithinWindow(Side.Ask, 7800.25));
    }

    // A deeper feed is the whole point of the upgrade, so the predicate must widen with it rather
    // than encode the 10-level shape it was discovered in.
    [Fact]
    public void The_window_widens_with_the_feed_rather_than_being_pinned_to_ten_levels()
    {
        var b = new BookMirror(Tick, TimeSpan.FromSeconds(30));
        for (int i = 0; i < 40; i++)
            b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = i,
                Price = 7800.25 + i * Tick, Volume = 70, Time = T });

        Assert.True(b.IsWithinWindow(Side.Ask, 7810.00));   // 39 ticks up, invisible at 10 levels
        Assert.False(b.IsWithinWindow(Side.Ask, 7810.25));
    }

    // The regression that actually bit, 2026-08-16 6 PM ET. The HUD's `obs` field is the one number
    // whose whole job is to say how deep the feed is, and it read 128 -- which was the default cap
    // saturating, not the feed reporting. A default that silently truncates gets read as a
    // measurement, so it has to sit far enough above any real feed that saturation means "something
    // is wrong", not "this is Tuesday".
    //
    // This pins the DEFAULT, not the parameter: BookMirrorTests already covers eviction by passing
    // maxLevels explicitly. Nothing in nt8/, verdict/ or harness/ ever passes it.
    [Fact]
    public void The_default_cap_does_not_truncate_a_feed_far_deeper_than_any_real_one()
    {
        var b = new BookMirror(Tick, TimeSpan.FromSeconds(30));
        const int deep = 300;                               // ~6x Rithmic's 40+, still under the cap

        for (int i = 0; i < deep; i++)
            b.ApplyDepth(new DepthEvent { Side = Side.Ask, Op = DepthOp.Add, Position = i,
                Price = 7800.25 + i * Tick, Volume = 70, Time = T });

        Assert.Equal(deep, b.Levels(Side.Ask).Count);
        Assert.True(b.IsWithinWindow(Side.Ask, 7800.25 + (deep - 1) * Tick),
            "the far end was clipped, so `obs` would report the cap instead of the feed");
    }
}
