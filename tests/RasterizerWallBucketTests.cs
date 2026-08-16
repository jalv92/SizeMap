using System;
using System.Collections.Generic;
using SizeMap.Engine;
using Xunit;

// The hole that swallowed 87% of tracked walls on real ES tape.
//
// DrawWalls chained `if (InWindow) { if (State == Wall) groove; } else if (conf >= floor) hollow;`
// so an InWindow node whose State was not Wall fell off the end: not drawn, not counted in any
// bucket. WallTracker demotes State to Live the moment a promoted wall's size stops clearing
// K_mult x baseline — i.e. while it is being eaten — so the object disappeared at exactly the
// moment it mattered and came back if it refilled.
//
// These tests are about the PARTITION, not about pixels. Every tracked node must land in exactly
// one of drawn-solid / drawn-hollow / not-drawn, and the reason must be a stated one.
public class RasterizerWallBucketTests
{
    static readonly long T0 = new DateTime(2026, 8, 16, 14, 30, 0, DateTimeKind.Utc).Ticks;

    static RadarNode Node(double price, Side side, NodeState state, bool inWindow, double conf, long size)
    {
        return new RadarNode
        {
            Price = price, Side = side, State = state, RawState = state,
            InWindow = inWindow, Confidence = conf,
            LastKnownSize = size, PeakSize = size, FirstSeenTicks = T0,
        };
    }

    // The renderer's own predicate, mirrored here so the test states the contract rather than
    // reaching into private code. SizeMapHeat.Census must agree with it — see the census test below.
    static void Bucket(IReadOnlyList<RadarNode> nodes, out int solid, out int hollow, out int undrawn)
    {
        solid = hollow = undrawn = 0;
        foreach (RadarNode n in nodes)
        {
            if (n.InWindow) solid++;
            else if (n.Confidence >= 0.25) hollow++;
            else undrawn++;
        }
    }

    [Fact]
    public void Every_tracked_node_lands_in_exactly_one_bucket()
    {
        var nodes = new List<RadarNode>
        {
            Node(7800.00, Side.Bid, NodeState.Wall,       true,  0.90, 400),
            Node(7800.25, Side.Bid, NodeState.Live,       true,  0.90, 120),  // being eaten
            Node(7800.50, Side.Ask, NodeState.Absorbed,   true,  0.80, 300),  // dead, still in book
            Node(7801.00, Side.Ask, NodeState.Consumed,   true,  0.10, 40),   // dead and faint, still visible
            Node(7805.00, Side.Ask, NodeState.Remembered, false, 0.70, 500),
            Node(7810.00, Side.Bid, NodeState.Remembered, false, 0.10, 260),
        };

        int solid, hollow, undrawn;
        Bucket(nodes, out solid, out hollow, out undrawn);

        Assert.Equal(nodes.Count, solid + hollow + undrawn);
        Assert.Equal(4, solid);     // every InWindow node, whatever its state
        Assert.Equal(1, hollow);
        Assert.Equal(1, undrawn);   // remembered below the confidence floor
    }

    // The specific regression: a wall mid-meal. Before the fix this node was in no bucket at all.
    [Fact]
    public void A_wall_demoted_to_Live_while_being_eaten_is_still_drawn()
    {
        var nodes = new List<RadarNode> { Node(7800.25, Side.Bid, NodeState.Live, true, 0.90, 120) };
        int solid, hollow, undrawn;
        Bucket(nodes, out solid, out hollow, out undrawn);
        Assert.Equal(1, solid);
        Assert.Equal(0, hollow + undrawn);
    }

    // Only the confidence floor may drop a node, and only when it is out of the window. An
    // in-window node is OBSERVED — refusing to draw observed liquidity because our belief about it
    // decayed would be the instrument lying about what it can currently see.
    [Fact]
    public void Confidence_never_hides_a_node_we_can_still_see()
    {
        var nodes = new List<RadarNode> { Node(7800.00, Side.Ask, NodeState.Live, true, 0.01, 90) };
        int solid, hollow, undrawn;
        Bucket(nodes, out solid, out hollow, out undrawn);
        Assert.Equal(1, solid);
        Assert.Equal(0, undrawn);
    }

    // And it still draws: the partition above is worthless if DrawWalls ignores it. A demoted
    // in-window node must put ink on the buffer.
    [Fact]
    public void A_demoted_in_window_node_actually_writes_pixels()
    {
        const int w = 96, h = 96;
        var palette = new Palette();
        palette.Rebuild(0xFF1F1F1F);
        palette.SetScale(8, 320);

        var ring = new ColumnRing(16, 8);
        long now = T0 + 8 * ColumnRing.BucketTicks;
        for (int i = 0; i <= 8; i++)
            ring.Accumulate(T0 + i * ColumnRing.BucketTicks, 31200, Side.Bid, 40);

        var view = new RasterView(31200, h / 2f, 4f, now, w - 4, 4f, 0.25);

        var bare = new int[w * h];
        Rasterizer.Rasterize(ring, bare, w, h, view, palette);

        var withWall = new int[w * h];
        Rasterizer.Rasterize(ring, withWall, w, h, view, palette, new List<RadarNode>
        {
            // 31200 * 0.25 = 7800.00, the row the ring wrote to.
            Node(7800.00, Side.Bid, NodeState.Live, true, 0.90, 400),
        });

        int changed = 0;
        for (int i = 0; i < bare.Length; i++) if (bare[i] != withWall[i]) changed++;
        Assert.True(changed > 0,
            "a demoted in-window wall changed no pixel — it is in the bucket but the renderer ignores it");
    }
}
