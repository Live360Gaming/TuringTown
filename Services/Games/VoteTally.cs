namespace TuringTown.Services.Games;

/// <summary>Counting votes. Knows nothing about who the players are; callers say which targets are valid.</summary>
internal static class VoteTally
{
    /// <param name="Counts">Votes per target. Abstentions and invalid targets are not counted.</param>
    /// <param name="Winner">The single target with the most votes, or null on a tie or when nobody got a vote.</param>
    public readonly record struct Result(IReadOnlyDictionary<Guid, int> Counts, Guid? Winner);

    /// <summary>Plurality wins. A tie (or no votes) has no winner.</summary>
    public static Result Plurality(IEnumerable<Guid?> votes, Func<Guid, bool> isValidTarget)
    {
        Dictionary<Guid, int> counts = [];
        foreach (Guid? target in votes)
        {
            if (target is not { } id || !isValidTarget(id)) continue;
            counts[id] = counts.GetValueOrDefault(id) + 1;
        }

        int max = counts.Count == 0 ? 0 : counts.Values.Max();
        Guid[] top = counts.Where(kv => kv.Value == max).Select(kv => kv.Key).ToArray();
        return new Result(counts, max > 0 && top.Length == 1 ? top[0] : null);
    }
}
