using TuringTown.Models;

namespace TuringTown.Services.Ai;

/// <summary>
/// Writes the onboarding form. It is not a player: it has no persona and knows nothing about the bots or the game in
/// progress, only the tone (<see cref="ContentPolicy"/>) and the mix of question kinds. The engine validates the result
/// with <see cref="QuestionValidator"/> and uses the built-in set if it is not valid.
/// </summary>
public interface IQuestionGenerator
{
    Task<IReadOnlyList<OnboardingQuestion>> GenerateAsync(QuestionRequest request, CancellationToken ct);
}

/// <summary>
/// The offline generator: hand-written questions of every kind, picked at random.
/// </summary>
public sealed class BuiltInQuestionGenerator : IQuestionGenerator
{
    public Task<IReadOnlyList<OnboardingQuestion>> GenerateAsync(QuestionRequest request, CancellationToken ct) =>
        Task.FromResult(ScriptedBotAgent.BuiltInQuestions(request.Policy, request.Mix));
}

/// <summary>
/// Runs the real generator but never lets it stall the game: on a timeout or any error the call is answered by the
/// fallback. Cancellation by the caller still propagates.
/// </summary>
public sealed class FallbackQuestionGenerator(IQuestionGenerator primary, IQuestionGenerator fallback, TimeSpan timeout) : IQuestionGenerator
{
    public async Task<IReadOnlyList<OnboardingQuestion>> GenerateAsync(QuestionRequest request, CancellationToken ct)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            return await primary.GenerateAsync(request, limit.Token);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            AiMetrics.Fallbacks.Add(1, new KeyValuePair<string, object?>("kind", "questions"));
            return await fallback.GenerateAsync(request, ct);
        }
    }
}
