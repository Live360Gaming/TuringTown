using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TuringTown.Models;
using TuringTown.Services.Ai;
using TuringTown.Services.Storage;

namespace TuringTown.Services.Games;

/// <summary>
/// Starts game nights and keeps track of the night running in each room.
/// </summary>
public sealed class GameCoordinator(
    AgentSelector agents,
    IGameNightPlanner planner,
    IBotMemoryStore memory,
    IGameArchive archive,
    IContentFilter contentFilter,
    IRoomBroadcaster broadcaster,
    TimeProvider time,
    IOptions<TuringTownOptions> turingTown,
    IOptions<WittyBitsOptions> wittyBits,
    IOptions<GameNightOptions> night,
    ILoggerFactory loggerFactory)
{
    private readonly ConcurrentDictionary<RoomCode, GameNight> _nights = new();
    private readonly ConcurrentDictionary<GameNight, IDisposable> _leases = new();

    /// <summary>
    /// Moves the room out of the lobby, seats the bots and starts the night.
    /// </summary>
    public RoomStartError StartGame(Room room)
    {
        RoomStartError error = room.TryStart(GameNight.BotCountFor);
        if (error != RoomStartError.None) return error;

        // The real AI while the limits allow, the scripted bots otherwise. The choice is made once, for the whole night.
        AgentSet set = agents.Select();

        NightContext context = new();
        GameNight gameNight = new(room, Catalog(room, set, context), set.Planner ?? planner, broadcaster, time, night.Value.Clone(),
            loggerFactory.CreateLogger<GameNight>(), finished => Finished(room, finished), context);

        if (set.Lease is not null) _leases[gameNight] = set.Lease;
        _nights[room.Code] = gameNight;
        gameNight.Start();
        return RoomStartError.None;
    }

    /// <summary>Every game a night can play, each making a fresh engine on the night's agents.</summary>
    private IReadOnlyList<NightGameEntry> Catalog(Room room, AgentSet set, NightContext context)
    {
        TuringTownOptions tuned = turingTown.Value.Clone();
        tuned.BotPaceToHumans ??= set.UsesAi;

        return
        [
            new NightGameEntry(
                new PlannableGame(GameIds.TuringTown, "Turing Town",
                    "Social deduction. The AI players hide among the humans, everyone chats anonymously, then votes someone out. " +
                    "The humans win by voting out every AI; the AI wins by surviving. The winning side gets a flat bonus."),
                () => new TuringTownEngine(room, set.Agent, set.Questions, memory, archive, contentFilter, broadcaster, time, tuned,
                    loggerFactory.CreateLogger<TuringTownEngine>(), night: context)),
            new NightGameEntry(
                new PlannableGame(GameIds.WittyBits, "Witty Bits",
                    "Comedy writing. Every player writes funny answers to prompts; two answers go head to head with no names shown " +
                    "and everyone else votes. Points are shared by vote, round 2 is worth double, and the last round has everyone " +
                    "answer one prompt. Each team scores the average points of its players."),
                () => new WittyBitsEngine(room, set.WittyAgent, set.WittyPrompts, contentFilter, broadcaster, time, wittyBits.Value.Clone(),
                    loggerFactory.CreateLogger<WittyBitsEngine>(), context))
        ];
    }

    public bool TryGetNight(Room room, out GameNight? gameNight) => _nights.TryGetValue(room.Code, out gameNight);

    /// <summary>The engine of the game being played in the room, if one is.</summary>
    public bool TryGetEngine(Room room, out IGameEngine? engine)
    {
        engine = _nights.TryGetValue(room.Code, out GameNight? n) ? n.Current : null;
        return engine is not null;
    }

    /// <summary>
    /// Stops the room's night, if one is running.
    /// </summary>
    public void StopGame(Room room)
    {
        if (_nights.TryGetValue(room.Code, out GameNight? gameNight)) gameNight.Cancel();
    }

    private void Finished(Room room, GameNight gameNight)
    {
        _nights.TryRemove(new KeyValuePair<RoomCode, GameNight>(room.Code, gameNight));
        if (_leases.TryRemove(gameNight, out IDisposable? lease)) lease.Dispose();
        gameNight.Dispose();
    }
}
