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
/// <param name="PausedAtUtcMs">
/// Unix milliseconds when the host paused the game, or null while it runs. While paused the time left is
/// <c>EndsAtUtcMs - PausedAtUtcMs</c>; on resume the server pushes <paramref name="EndsAtUtcMs"/> back by the pause.
/// </param>
public sealed record GameState(string GameId, Stage Stage, int Round, int Step, long? EndsAtUtcMs, object? Data, long? PausedAtUtcMs = null)
{
    public static GameState InLobby(string gameId) => new(gameId, Stage.Lobby, 0, 0, null, null);
}

/// <summary>
/// The public view of a player. Deliberately has no token and no role.
/// </summary>
/// <remarks>
/// <paramref name="Ready"/> is the lobby "I'm ready" flag. <paramref name="Leader"/> marks the first human to join, who
/// may start the game from their phone. Neither says anything about roles.
/// </remarks>
public readonly record struct PlayerInfo(Guid Id, string Alias, string Color, string CharacterId, bool Alive, bool Connected, bool Ready, bool Leader);

/// <summary>
/// A server-side copy of a seat, including the secret role. Never serialize this.
/// </summary>
internal readonly record struct PlayerSeat(Guid Id, string Alias, string Color, string CharacterId, bool IsBot, bool Alive, bool Connected, bool Ready, bool Leader)
{
    public PlayerInfo ToInfo() => new(Id, Alias, Color, CharacterId, Alive, Connected, Ready, Leader);
}

/// <param name="Mentions">Ids of the players this message @mentions (only real, seated players; never the sender).</param>
public readonly record struct ChatMessage(Guid Id, Guid PlayerId, string Alias, string Color, string CharacterId, string Text, long SentAtUtcMs, IReadOnlyList<Guid> Mentions);

/// <summary>How a player answers an onboarding question. There is never a "correct" answer: these are opinion
/// questions. The answers are private for the whole game: no client ever receives another player's answers. They
/// only feed the bots' personas.</summary>
public enum QuestionKind
{
    Text,
    SingleChoice,
    MultiChoice
}

/// <summary>One option of a choice question. Ids are <c>a</c>..<c>f</c>, assigned by the server.</summary>
public readonly record struct QuestionOption(string Id, string Text, bool Mature);

/// <summary>
/// One onboarding question. A <see cref="QuestionKind.Text"/> question has no options. A single choice has 2-6 and
/// <c>MinSelect = MaxSelect = 1</c>. A multi choice has 3-6 and <c>1 &lt;= MinSelect &lt;= MaxSelect</c>.
/// </summary>
public sealed record OnboardingQuestion(
    string Id,
    QuestionKind Kind,
    string Text,
    bool Mature,
    IReadOnlyList<QuestionOption> Options,
    int MinSelect,
    int MaxSelect)
{
    /// <summary>A free-text question.</summary>
    public OnboardingQuestion(string id, string text, bool mature)
        : this(id, QuestionKind.Text, text, mature, [], 0, 0)
    {
    }
}

/// <summary>
/// A player's answer to one question: free <paramref name="Text"/> for a text question, or the chosen
/// <paramref name="OptionIds"/> for a choice question. Never both.
/// </summary>
public sealed record OnboardingAnswer(string QuestionId, string? Text, IReadOnlyList<string>? OptionIds);

/// <summary>How many questions of each kind a game asks.</summary>
public readonly record struct QuestionMix(int Text, int SingleChoice, int MultiChoice)
{
    public int Total => Text + SingleChoice + MultiChoice;
}

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
