using TuringTown.Models;

namespace TuringTown.Services.Ai;

/// <summary>How long each kind of call may take before the fallback answers instead.</summary>
public sealed record AgentTimeouts(TimeSpan Chat, TimeSpan Vote, TimeSpan Onboarding, TimeSpan RoundNotes)
{
    public static AgentTimeouts All(TimeSpan timeout) => new(timeout, timeout, timeout, timeout);
}

/// <summary>
/// Runs the real agent but never lets it stall the game: on a timeout or any error the call is answered by a safe
/// fallback for that kind of call. Cancellation by the caller still propagates.
/// <list type="bullet">
/// <item>Vote and onboarding answers: the fallback agent (normally <see cref="ScriptedBotAgent"/>).</item>
/// <item>Chat: <b>silence</b>. A canned line in the middle of an AI conversation is itself a tell, and a quiet turn
/// costs nothing.</item>
/// <item>End-of-round notes: nothing (the bot keeps its old notes).</item>
/// </list>
/// </summary>
public sealed class FallbackBotAgent(IBotAgent primary, IBotAgent fallback, AgentTimeouts timeouts) : IBotAgent
{
    public FallbackBotAgent(IBotAgent primary, IBotAgent fallback, TimeSpan timeout)
        : this(primary, fallback, AgentTimeouts.All(timeout))
    {
    }

    public Task<IReadOnlyList<OnboardingAnswer>> AnswerOnboardingAsync(OnboardingAnswerRequest request, CancellationToken ct) =>
        Run("onboarding", timeouts.Onboarding, c => primary.AnswerOnboardingAsync(request, c), c => fallback.AnswerOnboardingAsync(request, c), ct);

    public Task<string?> NextChatMessageAsync(ChatRequest request, CancellationToken ct) =>
        Run("chat", timeouts.Chat, c => primary.NextChatMessageAsync(request, c), _ => Task.FromResult<string?>(null), ct);

    public Task<Guid?> CastVoteAsync(VoteRequest request, CancellationToken ct) =>
        Run("vote", timeouts.Vote, c => primary.CastVoteAsync(request, c), c => fallback.CastVoteAsync(request, c), ct);

    public Task EndRoundAsync(RoundEndRequest request, CancellationToken ct) =>
        Run<object?>("round_notes", timeouts.RoundNotes,
            async c =>
            {
                await primary.EndRoundAsync(request, c);
                return null;
            },
            _ => Task.FromResult<object?>(null),
            ct);

    private static async Task<T> Run<T>(string kind, TimeSpan timeout, Func<CancellationToken, Task<T>> call, Func<CancellationToken, Task<T>> backup, CancellationToken ct)
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
            AiMetrics.Fallbacks.Add(1, new KeyValuePair<string, object?>("kind", kind));
            return await backup(ct);
        }
    }
}
