using System.Text.Json.Serialization;

namespace TuringTown.Models;

/// <summary>
/// The ids clients use to pick the scenes for a state (<c>&lt;GameId&gt;&lt;Role&gt;&lt;Stage&gt;</c>, see core/director.js).
/// </summary>
public static class GameIds
{
    /// <summary>The game night itself: the lobby, Planning, the Scoreboard between games and the Finale.</summary>
    public const string Night = "game-night";

    public const string TuringTown = "turing-town";

    public const string WittyBits = "witty-bits";

    public const string ShoutOut = "shout-out";
}

/// <summary>
/// Every stage any game can be in. The game night owns Lobby, Planning, Scoreboard and Finale; each game uses its own
/// stages in between (Turing Town: Tutorial to Results). See docs/game-night.md and docs/game-flow.md.
/// </summary>
public enum Stage
{
    Lobby,
    Tutorial,
    Onboarding,
    Discussion,
    Voting,
    Reveal,
    Results,
    Planning,
    Scoreboard,
    Finale,

    /// <summary>Turing Town: five public questions.</summary>
    RapidFire,

    /// <summary>Turing Town: everyone answers the hot topic; only the drawn answer is ever shown.</summary>
    HotTopic,

    /// <summary>Turing Town: the TV explains the next part.</summary>
    Transition,

    /// <summary>Witty Bits: players write their answers to the prompts they were given.</summary>
    Writing,

    /// <summary>Witty Bits: the answers to one prompt, with no authors, and everyone else votes.</summary>
    Matchup,

    /// <summary>Witty Bits: who wrote which answer, the votes and the points.</summary>
    MatchupResult,

    /// <summary>Shout Out: everyone picks the trivia categories they like.</summary>
    Categories,

    /// <summary>Shout Out: a trivia question is up and anyone may buzz in.</summary>
    Question,

    /// <summary>Shout Out: one player buzzed and is answering.</summary>
    Buzzed,

    /// <summary>Shout Out: the right answer, who got it and the points.</summary>
    AnswerReveal
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

/// <summary>
/// One option of a choice question. Ids are <c>a</c>..<c>f</c>, assigned by the server. <paramref name="Era"/> is set
/// only on an era question (see <see cref="Eras"/>): the decade the option is typical of. It stays on the server: it is
/// never serialized, so no client ever sees it.
/// </summary>
public readonly record struct QuestionOption(string Id, string Text, bool Mature, [property: JsonIgnore] string? Era = null);

/// <summary>
/// The decades an era question's options can point to. An era question ("Which cartoon did you grow up with?") is a
/// single choice whose every option is typical of a decade. It never asks for an age, a year or an age range; the
/// room's answers only help the bots sound like they grew up when the humans did.
/// </summary>
public static class Eras
{
    public static readonly string[] All = ["1960s", "1970s", "1980s", "1990s", "2000s", "2010s", "2020s"];

    /// <summary>The fewest different decades an era question's options must cover.</summary>
    public const int MinDistinct = 3;

    public static bool IsValid(string? era) => era is not null && All.Contains(era);
}

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

    /// <summary>An era question: a single choice whose options all carry an era. Server-side only.</summary>
    [JsonIgnore]
    public bool IsEra => Kind == QuestionKind.SingleChoice && Options is { Count: > 0 } && Options.All(o => o.Era is not null);

    /// <summary>The era of the option picked, for an era question answered with <paramref name="optionIds"/>.</summary>
    public string? EraOf(IReadOnlyList<string>? optionIds) =>
        IsEra && optionIds is { Count: > 0 } ? Options.FirstOrDefault(o => o.Id == optionIds[0]).Era : null;
}

/// <summary>
/// A player's answer to one question: free <paramref name="Text"/> for a text question, or the chosen
/// <paramref name="OptionIds"/> for a choice question. Never both.
/// </summary>
public sealed record OnboardingAnswer(string QuestionId, string? Text, IReadOnlyList<string>? OptionIds);

/// <summary>How many questions of each kind a game asks. <paramref name="Era"/> era questions come on top of the
/// <paramref name="SingleChoice"/> ones (an era question is a single choice too, see <see cref="Eras"/>).</summary>
public readonly record struct QuestionMix(int Text, int SingleChoice, int MultiChoice, int Era = 0)
{
    public int Total => Text + SingleChoice + MultiChoice + Era;
}

/// <summary>Data for the Onboarding stage.</summary>
public readonly record struct OnboardingData(IReadOnlyList<OnboardingQuestion> Questions);

public readonly record struct VoteCount(string Alias, int Votes);

/// <summary>The three kinds of Turing Town round. The last one is always <see cref="Final"/>.</summary>
public enum RoundKind
{
    RapidFire,
    HotSeat,
    Final
}

/// <summary>
/// One bar of the suspicion meter: the votes a player has drawn so far this game, and their share of all votes cast.
/// Built only from votes, so it says nothing about roles.
/// </summary>
public readonly record struct SuspicionLevel(Guid PlayerId, string Alias, int Votes, int Percent);

/// <summary>One player's answers to the five public questions; null where they left one blank.</summary>
public sealed record PublicAnswers(Guid PlayerId, string Alias, IReadOnlyList<string?> Answers);

/// <summary>
/// Data for the RapidFire, HotTopic, Discussion and Voting stages. Each extra is set only in its own beat:
/// <see cref="Questions"/> in the five questions, <see cref="Answers"/> at the vote on them, and the hot seat's
/// <see cref="Topic"/> and <see cref="ShownAnswer"/> (which may well not be that player's own).
/// </summary>
public sealed record RoundData(RoundKind Kind, IReadOnlyList<SuspicionLevel> Meter)
{
    public PlayerInfo? HotSeat { get; init; }
    public IReadOnlyList<string>? Questions { get; init; }
    public IReadOnlyList<PublicAnswers>? Answers { get; init; }
    public string? Topic { get; init; }
    public string? ShownAnswer { get; init; }

    /// <summary>The AI host's line for the beat said over this screen (the vote on the answers, the final talk and vote).</summary>
    public string? HostLine { get; init; }
}

/// <summary>
/// Data for a Transition: the beat the TV is about to explain, and the AI host's extra on-screen line.
/// </summary>
public sealed record TransitionData(string Beat, string? HostLine, PlayerInfo? HotSeat, string? Topic, string? Answer);

/// <summary>
/// Data for the Reveal stage. Only the <paramref name="Final"/> round eliminates anyone: before it,
/// <paramref name="Eliminated"/> and <paramref name="WasBot"/> are always null and only the meter moves.
/// </summary>
public readonly record struct RevealData(PlayerInfo? Eliminated, bool? WasBot, IReadOnlyList<VoteCount> Tally, bool Final, IReadOnlyList<SuspicionLevel> Meter)
{
    /// <summary>The AI host's line for the meter, said over it; null at the verdict.</summary>
    public string? HostLine { get; init; }
}

/// <param name="Imposter">This human was Turing Town's secret Imposter (trying to be voted out as the AI).</param>
public readonly record struct RoleReveal(Guid PlayerId, string Alias, bool WasBot, bool Imposter = false);

/// <summary>
/// Something only one player is told, never broadcast: today, that they are Turing Town's Imposter
/// (<paramref name="Kind"/> <c>imposter</c>). Sent to that player's connection alone, and again in their join result.
/// </summary>
public sealed record SecretInfo(string Kind, string Title, string Text);

/// <summary>Data for the Results stage. This is the first point where every role is public.</summary>
public readonly record struct ResultsData(string Winner, IReadOnlyList<RoleReveal> Roles)
{
    /// <summary>Whether the hot seat's shown answer was really theirs; null when there was no hot seat answer.</summary>
    public bool? HotSeatWasTheirs { get; init; }
}

/// <summary>One game of the night, as the screens name it.</summary>
public readonly record struct NightGame(string GameId, string Title);

/// <summary>
/// Data for the Planning stage. <paramref name="Order"/> is empty while the AI is still choosing, then the games in
/// the order they will be played. <paramref name="Line"/> is the AI team's short public line about its choice.
/// </summary>
public readonly record struct PlanningData(IReadOnlyList<NightGame> Order, string? Line);

/// <summary>A player's points so far tonight, by alias. Says nothing about roles.</summary>
public readonly record struct Standing(Guid PlayerId, string Alias, int Points);

/// <summary>The Humans vs AI totals.</summary>
public readonly record struct TeamTotals(int Humans, int Ai);

/// <summary>
/// Data for the Scoreboard between two games. <paramref name="Teams"/> is null while any bot is still hidden: a
/// team total could point at the bot whose points it matches.
/// </summary>
public readonly record struct ScoreboardData(
    IReadOnlyList<NightGame> Played,
    NightGame? Next,
    IReadOnlyList<Standing> Standings,
    TeamTotals? Teams);

/// <summary>Data for the Finale: who won the night (<c>Humans</c>, <c>AI</c> or <c>Tie</c>) and every role.</summary>
public readonly record struct FinaleData(
    string Winner,
    TeamTotals Teams,
    IReadOnlyList<Standing> Standings,
    IReadOnlyList<RoleReveal> Roles)
{
    /// <summary>The goodbye line after "Thanks for playing!" (a narration key), the same on every screen.</summary>
    public string? Goodbye { get; init; }
}

/// <summary>A Witty Bits prompt and the players who answer it (two in rounds 1 and 2, everyone in the Last Bit).</summary>
public readonly record struct WittyAssignment(string PromptId, string Text, IReadOnlyList<Guid> PlayerIds);

/// <summary>Data for the Witty Bits Writing stage. Answers are never in it.</summary>
public readonly record struct WritingData(int Round, bool LastBit, IReadOnlyList<WittyAssignment> Assignments);

/// <summary>
/// One answer in a matchup, without its author. <paramref name="Missing"/> marks a player who did not answer: it
/// cannot be voted for.
/// </summary>
public readonly record struct WittyEntry(string Id, string Text, bool Missing);

/// <summary>
/// Data for a Witty Bits Matchup. <paramref name="Authors"/> are the players who wrote these answers and so cannot
/// vote (for the Last Bit that is everyone, and a player just cannot vote for their own answer). Which author wrote
/// which answer is only told in the result.
/// </summary>
public readonly record struct MatchupData(
    int Round,
    bool LastBit,
    int Index,
    int Count,
    string Prompt,
    IReadOnlyList<WittyEntry> Answers,
    IReadOnlyList<Guid> Authors);

/// <summary>An answer with its author, votes and points, once the matchup is over.</summary>
public readonly record struct WittyEntryResult(string Id, string Text, bool Missing, Guid AuthorId, string Alias, int Votes, int Points, bool Sweep);

/// <summary>Data for a Witty Bits MatchupResult.</summary>
public readonly record struct MatchupResultData(
    int Round,
    bool LastBit,
    int Index,
    int Count,
    string Prompt,
    IReadOnlyList<WittyEntryResult> Answers);

/// <summary>Data for the Witty Bits Results stage: everyone's points this game, by alias.</summary>
public readonly record struct WittyResultsData(IReadOnlyList<Standing> Standings);

/// <summary>
/// What a client gets back when it connects to the hub, enough to render the current screen immediately.
/// <paramref name="Unmasked"/> lists the players who have already been revealed as bots (so a refreshed screen can
/// show them as robots). It only ever holds bots whose reveal has been announced. <paramref name="Room"/> is the
/// lobby's settings and limits (also pushed as <c>RoomChanged</c>), so no screen has to poll for them.
/// </summary>
public sealed record JoinResult(
    GameState State,
    IReadOnlyList<PlayerInfo> Roster,
    PlayerInfo? You,
    IReadOnlyList<ChatMessage> Transcript,
    long ServerTimeUtcMs,
    IReadOnlyList<Guid> Unmasked,
    RoomSnapshot Room,
    SecretInfo? Secret = null);
