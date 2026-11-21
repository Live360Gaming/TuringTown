using TuringTown.Models;

namespace TuringTown.Services.Ai;

/// <summary>
/// Runs the real agent but never lets it stall the game: on a timeout or any error the call is answered by the
/// fallback (normally <see cref="ScriptedBotAgent"/>). Cancellation by the caller still propagates.
/// </summary>
public sealed class FallbackBotAgent(IBotAgent primary, IBotAgent fallback, TimeSpan timeout) : IBotAgent
{
    public Task<IReadOnlyList<OnboardingAnswer>> AnswerOnboardingAsync(OnboardingAnswerRequest request, CancellationToken ct) =>
        Run(c => primary.AnswerOnboardingAsync(request, c), c => fallback.AnswerOnboardingAsync(request, c), ct);

    public Task<string?> NextChatMessageAsync(ChatRequest request, CancellationToken ct) =>
        Run(c => primary.NextChatMessageAsync(request, c), c => fallback.NextChatMessageAsync(request, c), ct);

    public Task<Guid?> CastVoteAsync(VoteRequest request, CancellationToken ct) =>
        Run(c => primary.CastVoteAsync(request, c), c => fallback.CastVoteAsync(request, c), ct);

    private async Task<T> Run<T>(Func<CancellationToken, Task<T>> call, Func<CancellationToken, Task<T>> backup, CancellationToken ct)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            return await call(limit.Token);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Timed out or failed; the game keeps moving.
            return await backup(ct);
        }
    }
}
