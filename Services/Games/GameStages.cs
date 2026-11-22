using TuringTown.Models;

namespace TuringTown.Services.Games;

/// <summary>
/// The stage plumbing every game shares: writing a new stage to the room and broadcasting it, and pausing or resuming
/// the game. A stage change and a pause are never sent out of order (a client must never see a stage that hides the
/// pause, or the reverse), and a paused game does not move on to its next stage until it resumes.
/// </summary>
internal sealed class GameStages(Room room, IRoomBroadcaster broadcaster, GameClock clock, string gameKey, CancellationToken gameToken)
{
    private readonly SemaphoreSlim _stateSend = new(1, 1);

    public async Task EnterAsync(Stage stage, int round, int step, TimeSpan? duration, object? data)
    {
        while (true)
        {
            await clock.WhenRunning(gameToken);
            await _stateSend.WaitAsync(gameToken);
            try
            {
                if (clock.IsPaused) continue;

                long? endsAt = duration is { } d ? clock.EndsAt(d) : null;
                GameState state = room.SetState(new GameState(gameKey, stage, round, step, endsAt, data));
                await broadcaster.StateChanged(room, state);
                return;
            }
            finally
            {
                _stateSend.Release();
            }
        }
    }

    /// <summary>
    /// Pauses (or resumes) the game. While paused the stage clock stands still. The state carries
    /// <see cref="GameState.PausedAtUtcMs"/> so every screen can show it, and on resume the deadline moves back by
    /// however long the pause lasted.
    /// </summary>
    public async Task<string?> SetPausedAsync(bool paused)
    {
        await _stateSend.WaitAsync(gameToken);
        try
        {
            Stage stage = room.State.Stage;
            if (room.State.GameId != gameKey) return "The game is not running.";

            long now = clock.ToUnixMs(clock.UtcNow);
            GameState state;
            if (paused)
            {
                if (!clock.Pause()) return null;
                room.TryUpdateState(stage, s => s with { PausedAtUtcMs = now }, out state);
            }
            else
            {
                if (!clock.Resume()) return null;
                room.TryUpdateState(stage, s => s.PausedAtUtcMs is { } at
                    ? s with { PausedAtUtcMs = null, EndsAtUtcMs = s.EndsAtUtcMs + (now - at) }
                    : s, out state);
            }

            await broadcaster.StateChanged(room, state);
            return null;
        }
        finally
        {
            _stateSend.Release();
        }
    }
}
