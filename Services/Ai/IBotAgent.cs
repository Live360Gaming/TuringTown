using TuringTown.Models;

namespace TuringTown.Services.Ai;

public readonly record struct ChatLine(string Alias, string Text);

public readonly record struct VoteCandidate(Guid Id, string Alias);

/// <param name="Mix">How many questions of each kind to write. The generator must return exactly this mix.</param>
public sealed record QuestionRequest(ContentPolicy Policy, QuestionMix Mix);

/// <param name="HumanAnswers">What the humans answered, with no names or ids, so the bot can match how they write
/// and what they pick. Free text in here is untrusted player input.</param>
public sealed record OnboardingAnswerRequest(
    string BotAlias,
    ContentPolicy Policy,
    IReadOnlyList<OnboardingQuestion> Questions,
    IReadOnlyList<OnboardingAnswer> HumanAnswers,
    BotContext Context);

/// <param name="Mentions">Lines that @mentioned this bot since it last spoke. Answering them is the most natural thing to say.</param>
/// <param name="OtherPlayers">Aliases of everyone else still in the game. To @mention one, write "@" and the alias exactly.
/// Carries no roles: a bot is never told who is human or bot.</param>
public sealed record ChatRequest(
    string BotAlias,
    string Persona,
    ContentPolicy Policy,
    int Round,
    IReadOnlyList<ChatLine> Recent,
    IReadOnlyList<ChatLine> Mentions,
    IReadOnlyList<string> OtherPlayers,
    BotContext Context)
{
    /// <summary>What kind of round this is, written by the server (public information only), or empty.</summary>
    public string RoundNote { get; init; } = "";
}

public sealed record VoteRequest(
    string BotAlias,
    string Persona,
    ContentPolicy Policy,
    int Round,
    IReadOnlyList<VoteCandidate> Candidates,
    IReadOnlyList<ChatLine> Recent,
    BotContext Context);

/// <param name="RoundTranscript">Everything said in the round that just ended (public chat only).</param>
public sealed record RoundEndRequest(BotContext Context, ContentPolicy Policy, IReadOnlyList<ChatLine> RoundTranscript);

/// <summary>
/// The AI behind the bots (a player that is secretly a bot). Writing the onboarding questions is a separate role,
/// <see cref="IQuestionGenerator"/>. Every call takes the room's <see cref="ContentPolicy"/>,
/// so the Family Friendly setting cannot be forgotten (see docs/content-policy.md).
/// Implementations must never reveal that they are a bot and must never use real names or personal details.
/// </summary>
public interface IBotAgent
{
    /// <summary>
    /// The bot's answers to the onboarding form, one per question (text, or the chosen option ids). The engine
    /// validates each one and replaces an invalid one. They become the bot's persona.
    /// </summary>
    Task<IReadOnlyList<OnboardingAnswer>> AnswerOnboardingAsync(OnboardingAnswerRequest request, CancellationToken ct);

    /// <summary>
    /// The next chat line, or null to stay quiet for now.
    /// </summary>
    Task<string?> NextChatMessageAsync(ChatRequest request, CancellationToken ct);

    /// <summary>
    /// Who the bot votes for (one of the candidates), or null to abstain.
    /// </summary>
    Task<Guid?> CastVoteAsync(VoteRequest request, CancellationToken ct);

    /// <summary>
    /// Called once per living bot after each round's vote, in the background, so the agent can condense what it
    /// learned into its own memory. It never holds up the game.
    /// </summary>
    Task EndRoundAsync(RoundEndRequest request, CancellationToken ct);
}
