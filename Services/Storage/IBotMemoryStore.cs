using System.Collections.Concurrent;
using TuringTown.Services.Ai;

namespace TuringTown.Services.Storage;

/// <param name="Alias">The player this belief is about.</param>
/// <param name="Score">0 (trusts) to 1 (sure it is a bot).</param>
public sealed record Suspicion(string Alias, double Score, string Why);

/// <summary>
/// What one bot remembers across rounds: a short summary of what happened, how suspicious it is of each player, and
/// what it has claimed about itself. These are the bot's own beliefs, built from public chat only. They never
/// hold anyone's role.
/// </summary>
public sealed record BotMemory(string Summary, IReadOnlyList<Suspicion> Suspicions, IReadOnlyList<string> Claims)
{
    public static BotMemory Empty { get; } = new("", [], []);
}

/// <summary>
/// Per-bot memory, keyed by <see cref="BotKey"/> so two bots never share state. There is deliberately no way to list
/// or enumerate keys: an agent can only touch the key it was given. Memory only lives for one game.
/// </summary>
public interface IBotMemoryStore
{
    Task<BotMemory?> LoadAsync(BotKey key, CancellationToken ct);

    Task SaveAsync(BotKey key, BotMemory memory, CancellationToken ct);

    /// <summary>Forgets every bot of one game (called when the game ends).</summary>
    Task DeleteGameAsync(Guid gameId, CancellationToken ct);
}

/// <summary>
/// The default store, also used in production: rooms live in memory, so a bot's memory has nothing to survive.
/// </summary>
public sealed class InMemoryBotMemoryStore : IBotMemoryStore
{
    private readonly ConcurrentDictionary<BotKey, BotMemory> _memories = new();

    public Task<BotMemory?> LoadAsync(BotKey key, CancellationToken ct) =>
        Task.FromResult(_memories.TryGetValue(key, out BotMemory? memory) ? memory : null);

    public Task SaveAsync(BotKey key, BotMemory memory, CancellationToken ct)
    {
        _memories[key] = memory;
        return Task.CompletedTask;
    }

    public Task DeleteGameAsync(Guid gameId, CancellationToken ct)
    {
        foreach (BotKey key in _memories.Keys.Where(k => k.GameId == gameId))
            _memories.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
