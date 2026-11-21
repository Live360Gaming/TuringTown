using System.Security.Cryptography;

namespace TuringTown.Services.Games;

/// <summary>
/// Time for a running game: the current time, waits that end early when a signal arrives, and random pauses.
/// Everything goes through the <see cref="TimeProvider"/> (so tests can drive it) and stops when the game's
/// cancellation token fires. Any game with timed stages can use it.
///
/// The game can be paused (<see cref="Pause"/>). While it is paused, time spent in <see cref="WaitAsync"/> and
/// <see cref="Delay"/> does not count: a wait that had 20 s left when the game was paused still has 20 s left when it
/// resumes.
/// </summary>
internal sealed class GameClock(TimeProvider time, CancellationToken gameToken)
{
    private readonly object _pauseLock = new();
    // Completed when a pause starts (replaced on resume), and completed when the pause ends (replaced on pause).
    private TaskCompletionSource _paused = NewSignal();
    private TaskCompletionSource? _resumed;

    public DateTimeOffset UtcNow => time.GetUtcNow();

    public long ToUnixMs(DateTimeOffset moment) => moment.ToUnixTimeMilliseconds();

    /// <summary>A signal that can be completed once and awaited by anyone, without running continuations inline.</summary>
    public static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When a stage lasting <paramref name="duration"/> from now will end, as Unix milliseconds.</summary>
    public long EndsAt(TimeSpan duration) => ToUnixMs(UtcNow.Add(duration));

    public bool IsPaused
    {
        get
        {
            lock (_pauseLock)
                return _resumed is not null;
        }
    }

    /// <summary>Stops the game's time. Returns false if it was already paused.</summary>
    public bool Pause()
    {
        lock (_pauseLock)
        {
            if (_resumed is not null) return false;
            _resumed = NewSignal();
            _paused.TrySetResult();
            return true;
        }
    }

    /// <summary>Starts the game's time again. Returns false if it was not paused.</summary>
    public bool Resume()
    {
        lock (_pauseLock)
        {
            if (_resumed is not { } resumed) return false;
            _resumed = null;
            _paused = NewSignal();
            resumed.TrySetResult();
            return true;
        }
    }

    /// <summary>Completes at once while the game runs, or when it resumes. Throws if <paramref name="ct"/> is cancelled.</summary>
    public Task WhenRunning(CancellationToken ct)
    {
        lock (_pauseLock)
            return _resumed?.Task.WaitAsync(ct) ?? Task.CompletedTask;
    }

    /// <summary>A pause in game time (it stretches while the game is paused). Throws when <paramref name="ct"/> is cancelled.</summary>
    public Task Delay(TimeSpan span, CancellationToken ct) => RunningWaitAsync(span, null, ct);

    /// <summary>
    /// Waits for <paramref name="timeout"/> of game time or until <paramref name="signal"/> completes, whichever is
    /// first. A signal that completes while the game is paused takes effect when it resumes.
    /// Throws if the game is cancelled meanwhile.
    /// </summary>
    public Task WaitAsync(TimeSpan timeout, Task? signal = null) => RunningWaitAsync(timeout, signal, gameToken);

    private async Task RunningWaitAsync(TimeSpan span, Task? signal, CancellationToken ct)
    {
        TimeSpan remaining = span;
        while (true)
        {
            Task? resumed;
            Task paused;
            lock (_pauseLock)
            {
                resumed = _resumed?.Task;
                paused = _paused.Task;
            }

            if (resumed is not null)
            {
                await resumed.WaitAsync(ct);
                continue;
            }

            ct.ThrowIfCancellationRequested();
            if (signal is { IsCompleted: true } || remaining <= TimeSpan.Zero) return;

            DateTimeOffset started = UtcNow;
            using CancellationTokenSource local = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task delay = Task.Delay(remaining, time, local.Token);

            Task first = signal is null
                ? await Task.WhenAny(delay, paused)
                : await Task.WhenAny(delay, paused, signal);

            local.Cancel();
            ct.ThrowIfCancellationRequested();

            try
            {
                await delay;
            }
            catch (OperationCanceledException)
            {
                // We cancelled it ourselves because the signal arrived or the game was paused.
            }

            if (first == delay || first == signal) return;

            // Paused: keep what was left for when the game resumes.
            remaining -= UtcNow - started;
        }
    }

    public static TimeSpan RandomDelay(int minMs, int maxMs) =>
        TimeSpan.FromMilliseconds(maxMs <= minMs ? minMs : RandomNumberGenerator.GetInt32(minMs, maxMs + 1));
}
