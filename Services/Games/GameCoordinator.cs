using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TuringTown.Models;
using TuringTown.Services.Ai;
using TuringTown.Services.Storage;

namespace TuringTown.Services.Games;

/// <summary>
/// Starts games and keeps track of the engine running each room.
/// </summary>
public sealed class GameCoordinator(
    AgentSelector agents,
    IBotMemoryStore memory,
    IGameArchive archive,
    IContentFilter contentFilter,
    IRoomBroadcaster broadcaster,
    TimeProvider time,
    IOptions<TuringTownOptions> options,
    ILoggerFactory loggerFactory)
{
    private readonly ConcurrentDictionary<RoomCode, TuringTownEngine> _engines = new();
    private readonly ConcurrentDictionary<TuringTownEngine, IDisposable> _leases = new();

    /// <summary>
    /// Moves the room out of the lobby, seats the bots and starts the game.
    /// </summary>
    public RoomStartError StartGame(Room room)
    {
        RoomStartError error = room.TryStart(TuringTownEngine.BotCountFor);
        if (error != RoomStartError.None) return error;

        // The real AI while the limits allow, the scripted bots otherwise. The choice is made once, for the whole game.
        AgentSet set = agents.Select();

        TuringTownOptions tuned = options.Value.Clone();
        tuned.BotPaceToHumans ??= set.UsesAi;

        TuringTownEngine engine = new(room, set.Agent, set.Questions, memory, archive, contentFilter, broadcaster, time, tuned,
            loggerFactory.CreateLogger<TuringTownEngine>(), finished => Finished(room, finished));

        if (set.Lease is not null) _leases[engine] = set.Lease;
        _engines[room.Code] = engine;
        engine.Start();
        return RoomStartError.None;
    }

    public bool TryGetEngine(Room room, out TuringTownEngine? engine) => _engines.TryGetValue(room.Code, out engine);

    private void Finished(Room room, TuringTownEngine engine)
    {
        _engines.TryRemove(new KeyValuePair<RoomCode, TuringTownEngine>(room.Code, engine));
        if (_leases.TryRemove(engine, out IDisposable? lease)) lease.Dispose();
        engine.Dispose();
    }
}
