namespace TuringTown.Models;

/// <summary>
/// The stages of a Turing Town game. See docs/game-flow.md for the flow between them.
/// </summary>
public enum Stage
{
    Lobby,
    Tutorial,
    Onboarding,
    Discussion,
    Voting,
    Reveal,
    Results
}

/// <summary>
/// What every client renders from. It is always broadcast as a full snapshot, so a late or reconnecting
/// client needs nothing else. <paramref name="Data"/> carries stage-specific content and never holds a secret
/// role before the reveal.
/// </summary>
/// <param name="GameId">Which game is running (lets the client pick the right scenes).</param>
/// <param name="Round">Discussion/vote round, starting at 1. 0 before the first round.</param>
/// <param name="Step">Free-form step within a stage (e.g. the tutorial step).</param>
/// <param name="EndsAtUtcMs">Unix milliseconds when the stage ends, or null if it has no deadline.</param>
public sealed record GameState(string GameId, Stage Stage, int Round, int Step, long? EndsAtUtcMs, object? Data)
{
    public static GameState InLobby(string gameId) => new(gameId, Stage.Lobby, 0, 0, null, null);
}

/// <summary>
/// The public view of a player. Deliberately has no token and no role.
/// </summary>
public readonly record struct PlayerInfo(Guid Id, string Alias, string Color, string CharacterId, bool Alive, bool Connected);

/// <summary>
/// A server-side copy of a seat, including the secret role. Never serialize this.
/// </summary>
internal readonly record struct PlayerSeat(Guid Id, string Alias, string Color, string CharacterId, bool IsBot, bool Alive, bool Connected)
{
    public PlayerInfo ToInfo() => new(Id, Alias, Color, CharacterId, Alive, Connected);
}

public readonly record struct ChatMessage(Guid Id, Guid PlayerId, string Alias, string Color, string CharacterId, string Text, long SentAtUtcMs);

public readonly record struct OnboardingQuestion(string Id, string Text, bool Mature);

/// <summary>Data for the Onboarding stage.</summary>
public readonly record struct OnboardingData(IReadOnlyList<OnboardingQuestion> Questions);

public readonly record struct VoteCount(string Alias, int Votes);

/// <summary>Data for the Reveal stage. <paramref name="Eliminated"/> is null when the vote tied.</summary>
public readonly record struct RevealData(PlayerInfo? Eliminated, bool? WasBot, IReadOnlyList<VoteCount> Tally);

public readonly record struct RoleReveal(Guid PlayerId, string Alias, bool WasBot);

/// <summary>Data for the Results stage. This is the first point where every role is public.</summary>
public readonly record struct ResultsData(string Winner, IReadOnlyList<RoleReveal> Roles);

/// <summary>
/// What a client gets back when it connects to the hub, enough to render the current screen immediately.
/// <paramref name="Unmasked"/> lists the players who have already been revealed as bots (so a refreshed screen can
/// show them as robots). It only ever holds bots whose reveal has been announced.
/// </summary>
public sealed record JoinResult(
    GameState State,
    IReadOnlyList<PlayerInfo> Roster,
    PlayerInfo? You,
    IReadOnlyList<ChatMessage> Transcript,
    long ServerTimeUtcMs,
    IReadOnlyList<Guid> Unmasked);
