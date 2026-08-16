using System;
using System.Collections.Generic;
using SizeMap.Engine;
using Xunit;

// The range filter, pinned against the session that motivated it.
//
// SizeMapZoneStrategy armed a short 5 ticks off the low of an hour-long 22-point band because the
// SMA stack ordered itself bearish on the return leg. These tests hold the line that separates that
// hour from a leg worth trading -- and they hold it at 0.30, the strategy's default threshold, so a
// change to the ratio's behaviour fails here rather than in Replay.
public class TrendQualityTests
{
    // Mirrors MinEfficiencyRatio's default in nt8/SizeMapZoneStrategy.cs. A PROVISIONAL number:
    // the strategy logs efficiency_ratio on every row precisely so the forward test can replace it
    // with a measured one. If that default moves, move this with it.
    const double DefaultThreshold = 0.30;

    // Closes arrive newest-first (Close[0] is the current bar), so tests author the series in the
    // order a human reads a chart -- left to right, oldest to newest -- and this flips it.
    static double[] NewestFirst(IEnumerable<double> oldestFirst)
    {
        var l = new List<double>(oldestFirst);
        l.Reverse();
        return l.ToArray();
    }

    [Fact]
    public void A_straight_line_scores_one()
    {
        var up = new List<double>();
        for (int i = 0; i < 40; i++) up.Add(7790.25 + i * 0.25);
        double[] a = NewestFirst(up);

        Assert.Equal(1.0, TrendQuality.EfficiencyRatio(a, a.Length), 6);
    }

    [Fact]
    public void A_round_trip_scores_near_zero_however_far_it_travelled()
    {
        // One tick up, one tick down, forty bars: 39 ticks of path for 1 tick of progress.
        var chop = new List<double>();
        for (int i = 0; i < 40; i++) chop.Add(7770.00 + (i % 2) * 0.25);
        double[] a = NewestFirst(chop);

        double er = TrendQuality.EfficiencyRatio(a, a.Length);
        Assert.True(er < 0.05, "sawtooth scored " + er);
    }

    // The hour in the 2026-08-16 screenshot: ES 30s, 9:43-10:37, three round trips inside a
    // 22-point band, ending 5 points below where it started.
    static List<double> RangeHourOldestFirst()
    {
        var band = new List<double>();
        const double mid = 7773.0, amp = 11.0;
        int period = 36;                                   // ~18 minutes per round trip
        for (int i = 0; i < 108; i++)
        {
            // Triangle wave: linear up half the period, linear down the other half.
            double phase = (i % period) / (double)period;
            double t = phase < 0.5 ? phase * 2.0 : (1.0 - phase) * 2.0;
            band.Add(mid - amp + 2.0 * amp * t - i * 0.05);   // slight net drift down, as it did
        }
        return band;
    }

    // This is the case the filter exists for -- if the hour as a whole ever clears the threshold,
    // the filter has stopped doing its job.
    [Fact]
    public void The_range_that_motivated_this_filter_does_not_clear_the_default_threshold()
    {
        double[] a = NewestFirst(RangeHourOldestFirst());

        double er = TrendQuality.EfficiencyRatio(a, a.Length);
        Assert.True(er < DefaultThreshold,
            "the range hour scored " + er.ToString("F3") + ", which would have armed");
    }

    // ...and the test above is NOT the shipped configuration. It scores the whole 108-bar hour,
    // while the gate only ever sees RangeFilterPeriod = 40 bars, sliding. A filter that blocks the
    // hour in aggregate but opens on some 40-bar window inside it would still have armed that
    // short 5 ticks off the low. The production claim is this one: NO window arms.
    [Fact]
    public void No_forty_bar_window_inside_that_range_hour_clears_the_threshold_either()
    {
        var band = RangeHourOldestFirst();
        const int window = 40;                             // RangeFilterPeriod's default
        double worst = 0; int worstAt = -1;

        for (int end = window; end <= band.Count; end++)
        {
            // The strategy's buffer: the newest `window` closes as of that bar.
            double[] a = NewestFirst(band.GetRange(end - window, window));
            double er = TrendQuality.EfficiencyRatio(a, window);
            if (er > worst) { worst = er; worstAt = end - 1; }
        }

        Assert.True(worst < DefaultThreshold, "the most efficient 40-bar window in the range hour " +
            "ended at bar " + worstAt + " and scored " + worst.ToString("F3") + ", which would have armed");
    }

    // The other half of the claim: the filter must not be so tight that it blocks a real leg. A
    // trend that gives back one bar in four is ordinary, not exceptional.
    [Fact]
    public void A_directional_leg_with_pullbacks_still_clears_the_default_threshold()
    {
        var leg = new List<double>();
        double p = 7750.0;
        for (int i = 0; i < 40; i++)
        {
            p += (i % 4 == 3) ? -0.75 : 0.75;              // three up, one back
            leg.Add(p);
        }
        double[] a = NewestFirst(leg);

        double er = TrendQuality.EfficiencyRatio(a, a.Length);
        Assert.True(er >= DefaultThreshold,
            "a 3-up-1-back leg scored " + er.ToString("F3") + ", which would have been blocked");
    }

    // The gate runs on both sides, so the ratio has to be blind to direction: a downtrend and its
    // mirrored uptrend are equally tradeable.
    [Fact]
    public void Direction_does_not_change_the_score()
    {
        var up = new List<double>();
        for (int i = 0; i < 30; i++) up.Add(7700.0 + i * 0.5 - (i % 5 == 0 ? 1.25 : 0));
        var down = new List<double>();
        for (int i = 0; i < 30; i++) down.Add(7700.0 - (i * 0.5 - (i % 5 == 0 ? 1.25 : 0)));

        Assert.Equal(TrendQuality.EfficiencyRatio(NewestFirst(up), 30),
                     TrendQuality.EfficiencyRatio(NewestFirst(down), 30), 9);
    }

    // Every degenerate input returns 0 -- the value that BLOCKS. A filter that fails open is not a
    // filter; a 0/0 that returns NaN would compare false against any threshold and let everything
    // through.
    [Fact]
    public void Degenerate_input_scores_zero_so_the_gate_blocks_rather_than_permits()
    {
        double[] flat = new double[40];
        for (int i = 0; i < flat.Length; i++) flat[i] = 7770.0;
        Assert.Equal(0.0, TrendQuality.EfficiencyRatio(flat, flat.Length));

        Assert.Equal(0.0, TrendQuality.EfficiencyRatio(new double[] { 7770.0 }, 1));
        Assert.Equal(0.0, TrendQuality.EfficiencyRatio(new double[0], 0));
        Assert.Equal(0.0, TrendQuality.EfficiencyRatio(null, 40));
    }

    // The strategy sizes its buffer from RangeFilterPeriod but fills only CurrentBar+1 of it early
    // in a session, and passes that smaller count. Asking for more than the buffer holds must clamp,
    // not throw -- an unhandled exception here would take the strategy down mid-session.
    [Fact]
    public void A_count_larger_than_the_buffer_is_clamped_rather_than_thrown()
    {
        var up = new List<double>();
        for (int i = 0; i < 10; i++) up.Add(7790.0 + i * 0.25);
        double[] a = NewestFirst(up);

        Assert.Equal(1.0, TrendQuality.EfficiencyRatio(a, 500), 6);
    }

    // Count is authoritative: the newest N bars, not the whole buffer. A stale tail left in a
    // reused buffer must not reach the arithmetic.
    [Fact]
    public void Only_the_newest_count_entries_are_read()
    {
        // Newest 4 are a clean line; everything behind them is garbage from an earlier fill.
        double[] a = { 7801.00, 7800.75, 7800.50, 7800.25, 9999.0, -1.0, 9999.0 };

        Assert.Equal(1.0, TrendQuality.EfficiencyRatio(a, 4), 6);
    }
}
