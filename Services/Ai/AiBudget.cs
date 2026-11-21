using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TuringTown.Services.Ai;

/// <summary>
/// Counters for tuning the bots. They carry the kind of call, the outcome and token counts, never a prompt, a reply or
/// a role, and nothing here reaches a client.
/// </summary>
public static class AiMetrics
{
    public const string MeterName = "TuringTown.Ai";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> Calls = Meter.CreateCounter<long>("ai.calls");
    public static readonly Histogram<double> LatencyMs = Meter.CreateHistogram<double>("ai.latency_ms", "ms");
    public static readonly Counter<long> Tokens = Meter.CreateCounter<long>("ai.tokens");
    public static readonly Counter<long> Fallbacks = Meter.CreateCounter<long>("ai.fallbacks");
    public static readonly Counter<long> GuardRejects = Meter.CreateCounter<long>("ai.guard_rejects");
    public static readonly Counter<long> QuestionFailures = Meter.CreateCounter<long>("questions.validation_failures");
}

/// <summary>
/// The spending and abuse limits for the real AI, shared by every game: a cap on calls in flight, calls per game, tokens
/// per day, games on the AI at once, and a circuit breaker after repeated failures. Over a limit a call throws
/// <see cref="AiUnavailableException"/> (and the caller's fallback answers), and a new game starts on the scripted bots.
/// </summary>
public sealed class AiBudget
{
    private sealed class Scope
    {
        public int Calls;
        public DateTimeOffset Last;
    }

    private sealed class Lease(AiBudget owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) owner.ReleaseGame();
        }
    }

    private readonly AiLimits _limits;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _concurrency;
    private readonly object _lock = new();
    private readonly Dictionary<string, Scope> _scopes = [];
    private DateOnly _day;
    private long _tokensToday;
    private int _games;
    private int _failures;
    private DateTimeOffset _openUntil;

    public AiBudget(IOptions<AiOptions> options, TimeProvider time)
    {
        _limits = options.Value.Limits;
        _time = time;
        _concurrency = new SemaphoreSlim(_limits.MaxConcurrentCalls);
        _day = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
    }

    public long TokensToday
    {
        get
        {
            lock (_lock)
            {
                RollDay();
                return _tokensToday;
            }
        }
    }

    public bool CircuitOpen
    {
        get
        {
            lock (_lock)
                return _time.GetUtcNow() < _openUntil;
        }
    }

    /// <summary>
    /// A new game may use the real AI only while there is room: not too many AI games at once, tokens left today, and the
    /// circuit closed. The lease must be disposed when the game ends.
    /// </summary>
    public bool TryReserveGame(out IDisposable? lease)
    {
        lock (_lock)
        {
            RollDay();
            if (_games >= _limits.MaxAiGames || _tokensToday >= _limits.DailyTokenBudget || _time.GetUtcNow() < _openUntil)
            {
                lease = null;
                return false;
            }

            _games++;
            lease = new Lease(this);
            return true;
        }
    }

    private void ReleaseGame()
    {
        lock (_lock)
            _games = Math.Max(0, _games - 1);
    }

    /// <summary>
    /// Takes a place for one call, or throws <see cref="AiUnavailableException"/> when a limit is reached. Dispose the result
    /// when the call is over.
    /// </summary>
    public async Task<IDisposable> BeginCallAsync(string? scope, CancellationToken ct)
    {
        lock (_lock)
        {
            RollDay();
            DateTimeOffset now = _time.GetUtcNow();
            if (now < _openUntil) throw new AiUnavailableException("the circuit breaker is open");
            if (_tokensToday >= _limits.DailyTokenBudget) throw new AiUnavailableException("the daily token budget is used up");

            if (scope is not null)
            {
                if (!_scopes.TryGetValue(scope, out Scope? s)) _scopes[scope] = s = new Scope();
                if (s.Calls >= _limits.MaxCallsPerGame) throw new AiUnavailableException("this game used its call limit");
                s.Calls++;
                s.Last = now;
                if (_scopes.Count > 2000) PruneScopes(now);
            }
        }

        await _concurrency.WaitAsync(ct);
        return new Release(_concurrency);
    }

    private sealed class Release(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) semaphore.Release();
        }
    }

    public void RecordUsage(long tokens)
    {
        lock (_lock)
        {
            RollDay();
            _tokensToday += Math.Max(0, tokens);
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
            _failures = 0;
    }

    public void RecordFailure()
    {
        lock (_lock)
        {
            if (++_failures >= _limits.CircuitFailures)
            {
                _openUntil = _time.GetUtcNow().AddSeconds(_limits.CircuitOpenSeconds);
                _failures = 0;
            }
        }
    }

    private void RollDay()
    {
        DateOnly today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        if (today == _day) return;
        _day = today;
        _tokensToday = 0;
    }

    private void PruneScopes(DateTimeOffset now)
    {
        foreach (string key in _scopes.Where(kv => now - kv.Value.Last > TimeSpan.FromHours(2)).Select(kv => kv.Key).ToArray())
            _scopes.Remove(key);
    }
}

/// <summary>
/// Puts the limits and the metrics around any <see cref="ILlmClient"/>: the budget and the circuit breaker, a timeout per
/// call, and counters for calls, latency and tokens. Prompts and replies are only logged at Debug.
/// </summary>
public sealed class GuardedLlmClient(ILlmClient inner, AiBudget budget, ILogger<GuardedLlmClient> logger) : ILlmClient
{
    public async Task<LlmResult> CompleteAsync(LlmCall call, CancellationToken ct)
    {
        using IDisposable permit = await Counted(call, () => budget.BeginCallAsync(call.Scope, ct));

        long started = Stopwatch.GetTimestamp();
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(call.Timeout);

        try
        {
            LlmResult result = await inner.CompleteAsync(call, limit.Token);
            budget.RecordUsage((long)result.InputTokens + result.OutputTokens);
            budget.RecordSuccess();

            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            AiMetrics.Calls.Add(1, new KeyValuePair<string, object?>("kind", call.Kind), new KeyValuePair<string, object?>("outcome", "ok"));
            AiMetrics.LatencyMs.Record(ms, new KeyValuePair<string, object?>("kind", call.Kind));
            AiMetrics.Tokens.Add(result.InputTokens, new KeyValuePair<string, object?>("kind", call.Kind), new KeyValuePair<string, object?>("type", "input"));
            AiMetrics.Tokens.Add(result.OutputTokens, new KeyValuePair<string, object?>("kind", call.Kind), new KeyValuePair<string, object?>("type", "output"));

            logger.LogDebug("AI {Kind} call took {Ms:0} ms ({In} in, {Cached} cached, {Out} out, {Reasoning} reasoning)",
                call.Kind, ms, result.InputTokens, result.CachedTokens, result.OutputTokens, result.ReasoningTokens);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller gave up (the stage ended). That says nothing about the model.
            Outcome(call, "cancelled");
            throw;
        }
        catch (OperationCanceledException)
        {
            budget.RecordFailure();
            Outcome(call, "timeout");
            throw new TimeoutException($"The AI {call.Kind} call took longer than {call.Timeout.TotalMilliseconds:0} ms.");
        }
        catch (LlmBlockedException)
        {
            // The model's safety filter said no. That is not an outage, so it does not count towards the breaker.
            Outcome(call, "blocked");
            throw;
        }
        catch (Exception ex)
        {
            budget.RecordFailure();
            Outcome(call, "error");
            logger.LogWarning("AI {Kind} call failed: {Type}", call.Kind, ex.GetType().Name);
            throw;
        }
    }

    private async Task<T> Counted<T>(LlmCall call, Func<Task<T>> begin)
    {
        try
        {
            return await begin();
        }
        catch (AiUnavailableException)
        {
            Outcome(call, "refused");
            throw;
        }
    }

    private static void Outcome(LlmCall call, string outcome) =>
        AiMetrics.Calls.Add(1, new KeyValuePair<string, object?>("kind", call.Kind), new KeyValuePair<string, object?>("outcome", outcome));
}
