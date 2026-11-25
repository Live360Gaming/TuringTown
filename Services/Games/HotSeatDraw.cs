namespace TuringTown.Services.Games;

/// <summary>
/// The luck of the hot seat: who is drawn, and whose answer is shown under their name. It works on ids and text only,
/// so it cannot treat a bot differently from a human. <c>random(n)</c> returns 0 to n-1.
/// </summary>
internal static class HotSeatDraw
{
    /// <summary>Everyone can be drawn: a weight of 1, plus 2 for every suspicion vote.</summary>
    public static Guid? Pick(IReadOnlyList<Guid> players, IReadOnlyDictionary<Guid, int> votes, Func<int, int> random)
    {
        if (players.Count == 0) return null;
        int[] weights = players.Select(p => 1 + 2 * votes.GetValueOrDefault(p)).ToArray();
        int roll = random(weights.Sum());
        for (int i = 0; i < players.Count; i++)
        {
            roll -= weights[i];
            if (roll < 0) return players[i];
        }
        return players[^1];
    }

    /// <summary>
    /// The answer shown under <paramref name="player"/>: another player's <paramref name="swapPercent"/> times in a
    /// hundred, otherwise their own. A near copy of their own is not a swap. With no one to swap with, their own is
    /// shown; with none of their own, someone else's. <c>WasTheirs</c> is null when there was nothing at all to show.
    /// </summary>
    public static (string? Answer, bool? WasTheirs) Answer(Guid player, IReadOnlyDictionary<Guid, string> answers, int swapPercent, Func<int, int> random)
    {
        string? own = answers.GetValueOrDefault(player);
        string[] others = answers
            .Where(a => a.Key != player && (own is null || !OnboardingAnswerValidator.IsNearCopy(a.Value, [own])))
            .OrderBy(a => a.Key)
            .Select(a => a.Value)
            .ToArray();

        bool swap = others.Length > 0 && (own is null || random(100) < swapPercent);
        if (swap) return (others[random(others.Length)], false);
        return own is null ? (null, null) : (own, true);
    }
}
