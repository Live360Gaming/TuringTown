namespace TuringTown.Services.Ai;

/// <summary>
/// Turns a bot's suspicion of each candidate into one vote, with chance. A bot never knows who is human, so it must not
/// vote like it does: the strongest suspect is the most likely pick, never a certain one, and every candidate keeps a
/// floor chance. Two bots who read the same chat therefore draw independently instead of voting in lockstep.
/// </summary>
public static class VoteChoice
{
    /// <summary>The weight every candidate gets on top of its suspicion, so nobody is ever safe and nobody certain.</summary>
    public const double Floor = 0.15;

    /// <summary>Suspicion is squared, so a clear suspect stands out from a vague one without becoming a sure thing.</summary>
    public const double Sharpness = 2;

    /// <summary>The suspicion given to a candidate the bot did not rate (it has nothing to go on).</summary>
    public const double Unrated = 0.3;

    /// <summary>How much each candidate weighs in the draw, from a suspicion between 0 and 1.</summary>
    public static double Weight(double suspicion) => Floor + Math.Pow(Math.Clamp(suspicion, 0, 1), Sharpness);

    /// <summary>
    /// Draws one index from <paramref name="suspicion"/> (one entry per candidate, 0 to 1), weighted by <see cref="Weight"/>.
    /// <paramref name="roll"/> is a random number in [0, 1).
    /// </summary>
    public static int Pick(IReadOnlyList<double> suspicion, double roll)
    {
        if (suspicion.Count == 0) throw new ArgumentException("There is nobody to vote for.", nameof(suspicion));

        double[] weights = suspicion.Select(Weight).ToArray();
        double target = Math.Clamp(roll, 0, 0.999999) * weights.Sum();
        for (int i = 0; i < weights.Length; i++)
        {
            target -= weights[i];
            if (target < 0) return i;
        }
        return weights.Length - 1;
    }
}
