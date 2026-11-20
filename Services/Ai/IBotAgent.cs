using TuringTown.Models;

namespace TuringTown.Services.Ai;

public readonly record struct ChatLine(string Alias, string Text);

public readonly record struct VoteCandidate(Guid Id, string Alias);

public sealed record QuestionRequest(ContentPolicy Policy, int Count);

/// <param name="HumanAnswerSamples">What the humans wrote, with no names or ids, so the bot can match their style.</param>
public sealed record OnboardingAnswerRequest(
    string BotAlias,
    ContentPolicy Policy,
    IReadOnlyList<OnboardingQuestion> Questions,
    IReadOnlyList<string> HumanAnswerSamples);

public sealed record ChatRequest(
    string BotAlias,
    string Persona,
    ContentPolicy Policy,
    int Round,
    IReadOnlyList<ChatLine> Recent);

public sealed record VoteRequest(
    string BotAlias,
    string Persona,
    ContentPolicy Policy,
    int Round,
    IReadOnlyList<VoteCandidate> Candidates,
    IReadOnlyList<ChatLine> Recent);

/// <summary>
/// The AI behind the bots and the onboarding questions. Every call takes the room's <see cref="ContentPolicy"/>,
/// so the Family Friendly setting cannot be forgotten (see docs/content-policy.md).
/// Implementations must never reveal that they are a bot and must never use real names or personal details.
/// </summary>
public interface IBotAgent
{
    /// <summary>
    /// Questions for the onboarding form. Validated by <see cref="QuestionValidator"/> before use.
    /// </summary>
    Task<IReadOnlyList<OnboardingQuestion>> GenerateOnboardingQuestionsAsync(QuestionRequest request, CancellationToken ct);

    /// <summary>
    /// The bot's answers to the onboarding form, one per question. They become its persona.
    /// </summary>
    Task<IReadOnlyList<string>> AnswerOnboardingAsync(OnboardingAnswerRequest request, CancellationToken ct);

    /// <summary>
    /// The next chat line, or null to stay quiet for now.
    /// </summary>
    Task<string?> NextChatMessageAsync(ChatRequest request, CancellationToken ct);

    /// <summary>
    /// Who the bot votes for (one of the candidates), or null to abstain.
    /// </summary>
    Task<Guid?> CastVoteAsync(VoteRequest request, CancellationToken ct);
}
