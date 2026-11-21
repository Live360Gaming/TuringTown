using System.Security.Cryptography;

namespace TuringTown.Services.Games;

/// <summary>
/// Time for a running game: the current time, waits that end early when a signal arrives, and random pauses.
/// Everything goes through the <see cref="TimeProvider"/> (so tests can drive it) and stops when the game's
/// cancellation token fires. Any game with timed stages can use it.
/// </summary>
internal sealed class GameClock(TimeProvider time, CancellationToken gameToken)
{
    public DateTimeOffset UtcNow => time.GetUtcNow();

    public long ToUnixMs(DateTimeOffset moment) => moment.ToUnixTimeMilliseconds();

    /// <summary>A signal that can be completed once and awaited by anyone, without running continuations inline.</summary>
    public static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When a stage lasting <paramref name="duration"/> from now will end, as Unix milliseconds.</summary>
    public long EndsAt(TimeSpan duration) => ToUnixMs(UtcNow.Add(duration));

    /// <summary>A pause that stops early when <paramref name="ct"/> is cancelled (it throws then).</summary>
    public Task Delay(TimeSpan span, CancellationToken ct) => Task.Delay(span, time, ct);

    /// <summary>
    /// Waits for <paramref name="timeout"/> or until <paramref name="signal"/> completes, whichever is first.
    /// Throws if the game is cancelled meanwhile.
    /// </summary>
    public async Task WaitAsync(TimeSpan timeout, Task? signal = null)
    {
        using CancellationTokenSource local = CancellationTokenSource.CreateLinkedTokenSource(gameToken);
        Task delay = Task.Delay(timeout, time, local.Token);

        await Task.WhenAny(signal ?? delay, delay);

        local.Cancel();
        gameToken.ThrowIfCancellationRequested();

        try
        {
            await delay;
        }
        catch (OperationCanceledException)
        {
            // We cancelled it ourselves because the signal arrived first.
        }
    }

    public static TimeSpan RandomDelay(int minMs, int maxMs) =>
        TimeSpan.FromMilliseconds(maxMs <= minMs ? minMs : RandomNumberGenerator.GetInt32(minMs, maxMs + 1));
}
