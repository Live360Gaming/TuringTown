using System.Security.Cryptography;
using TuringTown.Models;

namespace TuringTown.Services.Ai;

/// <summary>Asks the show host to plan one game of Turing Town.</summary>
public sealed record ShowRequest(ContentPolicy Policy);

/// <summary>
/// The content of one game, written up front: the five public questions, the hot topic used if none is written live,
/// and a short on-screen line per beat (keyed by <see cref="ShowBeats"/>; <c>{alias}</c> in the hot seat line is the
/// player drawn).
/// </summary>
public sealed record RunSheet(IReadOnlyList<string> Questions, string Topic, IReadOnlyDictionary<string, string> Lines);

/// <param name="Answers">What the room answered to the five questions, by alias. Untrusted player text.</param>
public sealed record TopicRequest(ContentPolicy Policy, IReadOnlyList<ChatLine> Answers);

/// <summary>The beats of a game, as the screens and the host name them.</summary>
public static class ShowBeats
{
    public const string Survey = "survey", Rapid = "rapid", Tap = "tap", Meter = "meter", Topic = "topic",
        HotSeat = "hotseat", Answer = "answer", Discussion = "discussion", Final = "final", Verdict = "verdict";

    public static readonly string[] All = [Survey, Rapid, Tap, Meter, Topic, HotSeat, Answer, Discussion, Final, Verdict];
}

/// <summary>
/// The AI that runs the show. It is <b>not a player</b>: it is never told a role, an onboarding answer or how a game is
/// decided. It only writes content; the server keeps the timings, the draw, the votes and the winner. The engine checks
/// everything it writes and falls back to the built-in content.
/// </summary>
public interface IShowHost
{
    Task<RunSheet> PlanAsync(ShowRequest request, CancellationToken ct);

    /// <summary>A hot topic that follows from what the room just answered, or null to keep the run sheet's.</summary>
    Task<string?> TopicAsync(TopicRequest request, CancellationToken ct);
}

/// <summary>The offline host: hand-written, family-safe content, picked at random.</summary>
public sealed class ScriptedShowHost : IShowHost
{
    private static readonly string[] Topics =
    [
        "What is your most controversial snack opinion?", "What would your friends say you overreact to?",
        "What did you pretend to understand recently?", "What is your worst excuse for being late?",
        "What tiny thing can ruin your whole day?"
    ];

    private static readonly IReadOnlyDictionary<string, string> Lines = new Dictionary<string, string>
    {
        [ShowBeats.Survey] = "Take your time. Nobody is peeking.",
        [ShowBeats.Rapid] = "Quick answers. Your first thought is usually the funniest.",
        [ShowBeats.Tap] = "Trust your gut.",
        [ShowBeats.Meter] = "Interesting. Very interesting.",
        [ShowBeats.Topic] = "Make it count.",
        [ShowBeats.HotSeat] = "{alias}, come on down.",
        [ShowBeats.Answer] = "{alias}, care to explain?",
        [ShowBeats.Discussion] = "Say it now or regret it later.",
        [ShowBeats.Final] = "No take-backs.",
        [ShowBeats.Verdict] = "Hold your breath."
    };

    public Task<RunSheet> PlanAsync(ShowRequest request, CancellationToken ct) =>
        Task.FromResult(Plan());

    public static RunSheet Plan() => new(
        Games.QuickFirePrompts.All.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).Take(5).Select(p => p.Text).ToArray(),
        Topics[RandomNumberGenerator.GetInt32(Topics.Length)],
        Lines);

    public Task<string?> TopicAsync(TopicRequest request, CancellationToken ct) => Task.FromResult<string?>(null);
}

/// <summary>Runs the real host but never lets it stall a game: on a timeout or error the scripted host answers.</summary>
public sealed class FallbackShowHost(IShowHost primary, TimeSpan planTimeout, TimeSpan topicTimeout, ILogger? logger = null) : IShowHost
{
    private readonly ILogger _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public Task<RunSheet> PlanAsync(ShowRequest request, CancellationToken ct) =>
        Guard(t => primary.PlanAsync(request, t), ScriptedShowHost.Plan, planTimeout, "show_plan", ct);

    public Task<string?> TopicAsync(TopicRequest request, CancellationToken ct) =>
        Guard(t => primary.TopicAsync(request, t), () => null, topicTimeout, "show_topic", ct);

    private async Task<T> Guard<T>(Func<CancellationToken, Task<T>> call, Func<T> fallback, TimeSpan timeout, string kind, CancellationToken ct)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            // WaitAsync as well, so a host that ignores its token still cannot stall the game.
            return await call(limit.Token).WaitAsync(limit.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            AiMetrics.Fallbacks.Add(1, new KeyValuePair<string, object?>("kind", kind));
            _logger.LogWarning(ex, "The AI show host failed ({Kind}), using the built-in content", kind);
            return fallback();
        }
    }
}
