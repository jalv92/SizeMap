using System;

namespace SizeMap.Engine
{
    // Is price GOING somewhere, or just moving?
    //
    // A stacked-SMA trend filter answers the wrong question. On 2026-08-16 ES held a 22-point band
    // for the best part of an hour (7762-7784 on 30s bars) and the stack ordered itself cleanly at
    // every swing extreme -- which is precisely where a trend-continuation entry is worst. The stack
    // is a statement about three averages, not about whether the market is travelling.
    //
    // Kaufman's Efficiency Ratio asks the question directly: net displacement over the path walked
    // to get there.
    //
    //     ER = |C[0] - C[n-1]| / SUM |C[i] - C[i+1]|
    //
    // A straight line scores 1. A round trip scores 0. That band scored roughly 0.07.
    //
    // Bounded 0..1 and scale-free, so one threshold means the same thing on ES and NQ, on 30s and
    // on 5m -- which is also why it can be logged per trade and calibrated from the CSV instead of
    // being guessed. Chosen over ADX for that: ADX(14) on 30s bars carries about seven minutes of
    // smoothing lag, and its "25" is folklore rather than a measured cut.
    //
    // ponytail: no smoothing, no EMA of the ratio. Raw ER over a fixed window is what gets logged,
    // so the number in the CSV is exactly the number the gate saw. Smooth it only if the forward
    // test shows the gate flapping bar-to-bar around the threshold.
    public static class TrendQuality
    {
        /// Efficiency ratio over the newest `count` entries of `closesNewestFirst` (index 0 = most
        /// recent, matching NinjaScript's Close[] indexing).
        ///
        /// Returns 0 -- "no efficiency, treat as range" -- for every degenerate case: fewer than two
        /// bars, or a path length of zero (a perfectly flat series, where net displacement is zero
        /// too). Zero is the safe default in both: it blocks trading rather than permitting it on
        /// data that cannot support the decision.
        public static double EfficiencyRatio(double[] closesNewestFirst, int count)
        {
            if (closesNewestFirst == null) return 0.0;
            if (count > closesNewestFirst.Length) count = closesNewestFirst.Length;
            if (count < 2) return 0.0;

            double path = 0.0;
            for (int i = 0; i < count - 1; i++)
                path += Math.Abs(closesNewestFirst[i] - closesNewestFirst[i + 1]);
            if (path <= 0.0) return 0.0;

            return Math.Abs(closesNewestFirst[0] - closesNewestFirst[count - 1]) / path;
        }
    }
}
