using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TuringTown.Models;
using TuringTown.Services.Ai;

namespace TuringTown.Services.Games;

/// <summary>
/// Starts games and keeps track of the engine running each room.
/// </summary>
public sealed class GameCoordinator(
    IBotAgent agent,
    IQuestionGenerator questionGenerator,
    IContentFilter contentFilter,
    IRoomBroadcaster broadcaster,
    TimeProvider time,
    IOptions<TuringTownOptions> options,
    ILoggerFactory loggerFactory)
{
    private readonly ConcurrentDictionary<RoomCode, TuringTownEngine> _engines = new();

    /// <summary>
    /// Moves the room out of the lobby, seats the bots and starts the game.
    /// </summary>
    public RoomStartError StartGame(Room room)
    {
        RoomStartError error = room.TryStart(TuringTownEngine.BotCountFor);
        if (error != RoomStartError.None) return error;

        TuringTownEngine engine = new(room, agent, questionGenerator, contentFilter, broadcaster, time, options.Value,
            loggerFactory.CreateLogger<TuringTownEngine>(), finished => Finished(room, finished));

        _engines[room.Code] = engine;
        engine.Start();
        return RoomStartError.None;
    }

    public bool TryGetEngine(Room room, out TuringTownEngine? engine) => _engines.TryGetValue(room.Code, out engine);

    private void Finished(Room room, TuringTownEngine engine)
    {
        _engines.TryRemove(new KeyValuePair<RoomCode, TuringTownEngine>(room.Code, engine));
        engine.Dispose();
    }
}
