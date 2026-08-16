using SizeMap.Engine;

namespace SizeMap.Verdict
{
    // The ten-line rule EpisodeClassifier has to beat. Written exactly as the adversarial review
    // demanded, and kept in its own file so nobody can claim it was quietly given a hand.
    //
    // ticksAway is measured with the ENGINE's own definition (EpisodeClassifier.QuoteTicksAway:
    // distance from the OPPOSITE inside quote), so the two models see the same number and the
    // comparison is not rigged by a redefinition.
    internal static class NullModel
    {
        public static Outcome Classify(bool vanished, int ticksAwayAtVanish, long traded, long drop)
        {
            if (vanished && ticksAwayAtVanish >= 2) return Outcome.Pulled;
            if (traded >= drop) return Outcome.Absorbed;
            return Outcome.Consumed;
        }
    }
}
