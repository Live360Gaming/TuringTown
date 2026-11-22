using TuringTown.Models;

namespace TuringTown.Services.Games;

/// <summary>
/// One game of a game night (Turing Town, Witty Bits, ...). The night (<see cref="GameNight"/>) plays each engine with
/// <see cref="PlayAsync"/> and the hub routes the calls every game shares through this interface. A game's own calls
/// (chat, answers, votes) go to the concrete engine.
///
/// An engine never puts the room back in the lobby: when its Results stage is over it returns a
/// <see cref="GameOutcome"/> and the night decides what comes next.
/// </summary>
public interface IGameEngine : IDisposable
{
    /// <summary>The <see cref="GameState.GameId"/> this engine broadcasts (see <see cref="GameIds"/>).</summary>
    string GameKey { get; }

    /// <summary>
    /// Plays the game from its first stage to the end of its Results stage. Never throws: a game that fails or is
    /// cancelled returns an outcome with <see cref="GameOutcome.Completed"/> false.
    /// </summary>
    Task<GameOutcome> PlayAsync();

    /// <summary>Stops the game early. <see cref="PlayAsync"/> then returns.</summary>
    void Cancel();

    bool IsPaused { get; }

    Task<string?> SetPausedAsync(bool paused);

    Task<string?> TutorialStepAsync(int step);

    string? TutorialDone();

    /// <summary>The host or leader leaves the Results screen. Returns an error message, or null.</summary>
    string? EndResults();

    /// <summary>Call when someone connects or drops, so the game does not wait for a player who left.</summary>
    Task NotifyPresenceChangedAsync();

    IReadOnlyList<ChatMessage> GetTranscript();

    /// <summary>Bots whose reveal has been announced in this game.</summary>
    IReadOnlyList<Guid> GetUnmasked();
}

/// <summary>
/// How a game ended, for the night's score.
/// </summary>
/// <param name="Completed">False when the game failed or was cancelled; the night then ends.</param>
/// <param name="Winner">The side that won a team game (<c>Humans</c> or <c>Bots</c>), or null for a points game.</param>
/// <param name="SeatPoints">Points per seat for a points game (empty for a team game).</param>
/// <param name="Unmasked">Bots whose reveal was announced during the game.</param>
public sealed record GameOutcome(
    bool Completed,
    string? Winner,
    IReadOnlyDictionary<Guid, int> SeatPoints,
    IReadOnlyList<Guid> Unmasked)
{
    public static GameOutcome Failed { get; } = new(false, null, new Dictionary<Guid, int>(), []);
}
