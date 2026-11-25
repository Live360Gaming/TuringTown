using Microsoft.Extensions.Logging;
using TuringTown.Models;
using TuringTown.Services.Ai;

namespace TuringTown.Services.Games;

/// <summary>A game the night can play: how the planner sees it, and how to make its engine.</summary>
public sealed record NightGameEntry(PlannableGame Info, Func<IGameEngine> Create);

/// <summary>
/// Runs one game night for a room: the AI team plans the order (Planning), each game is played in turn with a
/// Scoreboard between them, and the Finale shows whether the humans or the AI scored more. Then the room goes back to
/// the lobby and the bots leave. The same seats play every game. See docs/game-night.md.
///
/// Like an engine, the night is the only source of truth for its own stages and never sends a role before it is
/// public: team totals stay hidden until every bot has been unmasked (a total could point at one bot's points).
/// </summary>
public sealed class GameNight : IDisposable
{
    private readonly Room _room;
    private readonly IReadOnlyList<NightGameEntry> _catalog;
    private readonly IGameNightPlanner _planner;
    private readonly IRoomBroadcaster _broadcaster;
    private readonly GameClock _clock;
    private readonly GameNightOptions _opt;
    private readonly ILogger _logger;
    private readonly Action<GameNight>? _onFinished;
    private readonly NightContext _context;
    private readonly CancellationTokenSource _cts = new();

    // Everything below is guarded by _gate.
    private readonly object _gate = new();
    private IGameEngine? _current;
    private readonly HashSet<Guid> _unmasked = [];
    private readonly Dictionary<Guid, int> _seatPoints = [];
    private int _humans;
    private int _ai;
    private TaskCompletionSource _continue = GameClock.NewSignal();
    private Task? _loop;

    public GameNight(
        Room room,
        IReadOnlyList<NightGameEntry> catalog,
        IGameNightPlanner planner,
        IRoomBroadcaster broadcaster,
        TimeProvider time,
        GameNightOptions options,
        ILogger logger,
        Action<GameNight>? onFinished = null,
        NightContext? context = null)
    {
        _room = room;
        _catalog = catalog;
        _planner = planner;
        _broadcaster = broadcaster;
        _opt = options;
        _clock = new GameClock(time, _cts.Token);
        _logger = logger;
        _onFinished = onFinished;
        _context = context ?? new NightContext();
    }

    /// <summary>What the AI team knows about the night so far (shared by every bot).</summary>
    public NightContext Context => _context;

    /// <summary>
    /// The seats for a night: enough bots for Turing Town (1 for up to 4 humans, 2 for more), and enough for at least
    /// four players, which Witty Bits needs. So 2 humans get 2 bots, 3 or 4 get 1, and 5 or more get 2.
    /// </summary>
    public static int BotCountFor(int humans) => Math.Max(TuringTownEngine.BotCountFor(humans), Math.Max(0, 4 - humans));

    /// <summary>Completes when the night has ended and the room is back in the lobby.</summary>
    public Task Completion => _loop ?? Task.CompletedTask;

    /// <summary>The engine of the game being played, or null during the night's own stages.</summary>
    public IGameEngine? Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    /// <summary>The team totals so far (for tests and logs; never sent while a bot is hidden).</summary>
    public TeamTotals Totals
    {
        get
        {
            lock (_gate)
                return new TeamTotals(_humans, _ai);
        }
    }

    /// <summary>Starts the night. The room must already be out of the lobby (see <see cref="Room.TryStart"/>).</summary>
    public void Start() => _loop = Task.Run(RunAsync);

    public void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }

        Current?.Cancel();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    // ---------------------------------------------------------------- the night

    private async Task RunAsync()
    {
        try
        {
            await _broadcaster.RosterChanged(_room);

            IReadOnlyList<NightGameEntry> order = await PlanAsync();
            List<NightGame> played = [];

            for (int i = 0; i < order.Count; i++)
            {
                NightGameEntry entry = order[i];
                if (!await PlayAsync(entry)) return;
                played.Add(ToNightGame(entry));

                if (i < order.Count - 1)
                    await ScoreboardAsync(played, ToNightGame(order[i + 1]));
            }

            await FinaleAsync();
        }
        catch (OperationCanceledException)
        {
            // The room was closed.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Game night in room {Room} failed", _room.Code);
        }
        finally
        {
            await EndAsync();
        }
    }

    private static NightGame ToNightGame(NightGameEntry entry) => new(entry.Info.GameId, entry.Info.Title);

    /// <summary>The AI team chooses the order. A missing, slow or invalid plan is replaced by a random order.</summary>
    private async Task<IReadOnlyList<NightGameEntry>> PlanAsync()
    {
        TimeSpan max = TimeSpan.FromSeconds(_opt.PlanningMaxSeconds);
        await EnterStageAsync(Stage.Planning, TimeSpan.FromSeconds(_opt.PlanningMaxSeconds + _opt.PlanRevealSeconds), new PlanningData([], null));

        IReadOnlyList<PlannableGame> games = _catalog.Select(c => c.Info).ToArray();
        IReadOnlyList<PlayerSeat> seats = _room.GetSeats();
        GameNightPlanRequest request = new(games, seats.Select(s => s.Alias).ToArray(), seats.Count(s => s.IsBot), _room.ContentPolicy);

        GameNightPlan? plan = null;
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(max);
            plan = await _planner.PlanAsync(request, timeout.Token).WaitAsync(max, _cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_cts.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Room {Room}: the AI could not plan the night", _room.Code);
        }

        if (plan is null || !plan.IsValidFor(games))
        {
            if (plan is not null) _logger.LogWarning("Room {Room}: the AI's plan was not a valid order, using a random one", _room.Code);
            plan = ScriptedGameNightPlanner.RandomPlan(games);
        }

        NightGameEntry[] order = plan.Order.Select(id => _catalog.First(c => c.Info.GameId == id)).ToArray();
        string? line = PlanLine(plan.Line);
        _context.SetPlan(order.Select(e => e.Info.Title).ToArray());
        _context.SetScore(Standings(), null);

        TimeSpan reveal = TimeSpan.FromSeconds(_opt.PlanRevealSeconds);
        await EnterStageAsync(Stage.Planning, reveal, new PlanningData(order.Select(ToNightGame).ToArray(), line));
        await _clock.WaitAsync(reveal);
        return order;
    }

    /// <summary>The planner's line is shown on every screen, so it is kept short and single-line.</summary>
    private static string? PlanLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        string clean = PromptText.Sanitize(line.ReplaceLineEndings(" "), 140).Trim();
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>Plays one game. Returns false if it did not finish (an error or the room closed): the night then ends.</summary>
    private async Task<bool> PlayAsync(NightGameEntry entry)
    {
        _room.ReviveAll();
        _context.StartGame(entry.Info.Title);

        IGameEngine engine = entry.Create();
        lock (_gate)
            _current = engine;

        GameOutcome outcome;
        try
        {
            outcome = await engine.PlayAsync();
        }
        finally
        {
            lock (_gate)
            {
                _current = null;
                foreach (Guid id in engine.GetUnmasked())
                    _unmasked.Add(id);
            }

            engine.Dispose();
        }

        _cts.Token.ThrowIfCancellationRequested();
        if (!outcome.Completed) return false;

        AddScore(outcome);
        return true;
    }

    private void AddScore(GameOutcome outcome)
    {
        IReadOnlyList<PlayerSeat> seats = _room.GetSeats();
        TeamTotals points = Score(outcome, seats, _opt);
        lock (_gate)
        {
            _humans += points.Humans;
            _ai += points.Ai;
            foreach ((Guid id, int p) in outcome.SeatPoints)
                _seatPoints[id] = _seatPoints.GetValueOrDefault(id) + p;
            foreach (Guid id in outcome.Unmasked)
                _unmasked.Add(id);
        }

        // The bots see the score the room sees: standings always, team totals only once they are public.
        _context.SetScore(Standings(), PublicTotals());
    }

    /// <summary>
    /// What one game adds to each team. A team game gives its winner the flat bonus. A points game gives each team the
    /// <b>average</b> points of its seats, so a big human table does not beat one bot just by having more players.
    /// </summary>
    internal static TeamTotals Score(GameOutcome outcome, IReadOnlyList<PlayerSeat> seats, GameNightOptions options)
    {
        int humans = 0, ai = 0;

        if (outcome.Winner == "Humans") humans += options.TuringTownWinPoints;
        else if (outcome.Winner == "Bots") ai += options.TuringTownWinPoints;

        if (outcome.SeatPoints.Count > 0)
        {
            humans += Average(seats.Where(s => !s.IsBot));
            ai += Average(seats.Where(s => s.IsBot));
        }

        if (outcome.TeamPoints is { } direct)
        {
            humans += direct.Humans;
            ai += direct.Ai;
        }

        return new TeamTotals(humans, ai);

        int Average(IEnumerable<PlayerSeat> team)
        {
            int[] points = team.Select(s => outcome.SeatPoints.GetValueOrDefault(s.Id)).ToArray();
            return points.Length == 0 ? 0 : (int)Math.Round(points.Average(), MidpointRounding.AwayFromZero);
        }
    }

    private IReadOnlyList<Standing> Standings()
    {
        IReadOnlyList<PlayerSeat> seats = _room.GetSeats();
        lock (_gate)
            return seats.Select(s => new Standing(s.Id, s.Alias, _seatPoints.GetValueOrDefault(s.Id)))
                .OrderByDescending(s => s.Points).ThenBy(s => s.Alias, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The team totals, or null while any bot is still hidden.</summary>
    private TeamTotals? PublicTotals()
    {
        IReadOnlyList<PlayerSeat> seats = _room.GetSeats();
        lock (_gate)
            return seats.Where(s => s.IsBot).All(s => _unmasked.Contains(s.Id)) ? new TeamTotals(_humans, _ai) : null;
    }

    private async Task ScoreboardAsync(IReadOnlyList<NightGame> played, NightGame next)
    {
        await _broadcaster.RosterChanged(_room);
        TimeSpan duration = TimeSpan.FromSeconds(_opt.ScoreboardSeconds);
        Task signal = NewContinueSignal();
        await EnterStageAsync(Stage.Scoreboard, duration, new ScoreboardData(played.ToArray(), next, Standings(), PublicTotals()));
        await _clock.WaitAsync(duration, signal);
    }

    private async Task FinaleAsync()
    {
        IReadOnlyList<PlayerSeat> seats = _room.GetSeats();
        RoleReveal[] roles = seats.Select(s => new RoleReveal(s.Id, s.Alias, s.IsBot)).ToArray();
        TeamTotals totals;
        lock (_gate)
        {
            foreach (RoleReveal role in roles.Where(r => r.WasBot))
                _unmasked.Add(role.PlayerId);
            totals = new TeamTotals(_humans, _ai);
        }

        string winner = totals.Humans > totals.Ai ? "Humans" : totals.Ai > totals.Humans ? "AI" : "Tie";

        TimeSpan duration = TimeSpan.FromSeconds(_opt.FinaleSeconds);
        Task signal = NewContinueSignal();
        string goodbye = NarrationManifest.PickGoodbye(_room.LastGoodbye);
        _room.LastGoodbye = goodbye;
        await EnterStageAsync(Stage.Finale, duration, new FinaleData(winner, totals, Standings(), roles) { Goodbye = goodbye });
        await _clock.WaitAsync(duration, signal);
    }

    private async Task EndAsync()
    {
        try
        {
            _room.ResetToLobby();
            await _broadcaster.StateChanged(_room, _room.State);
            await _broadcaster.RosterChanged(_room);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reset room {Room}", _room.Code);
        }

        _onFinished?.Invoke(this);
    }

    private async Task EnterStageAsync(Stage stage, TimeSpan duration, object data)
    {
        _cts.Token.ThrowIfCancellationRequested();
        GameState state = _room.SetState(new GameState(GameIds.Night, stage, 0, 0, _clock.EndsAt(duration), data));
        await _broadcaster.StateChanged(_room, state);
    }

    private Task NewContinueSignal()
    {
        lock (_gate)
        {
            _continue = GameClock.NewSignal();
            return _continue.Task;
        }
    }

    // ---------------------------------------------------------------- calls from the hub (return an error message, or null)

    /// <summary>
    /// The host or leader moves on: past a game's Results, past the Scoreboard to the next game, or from the Finale
    /// back to the lobby.
    /// </summary>
    public string? Continue()
    {
        if (Current is { } engine && _room.State.Stage == Stage.Results) return engine.EndResults();

        Stage stage = _room.State.Stage;
        if (stage is not (Stage.Scoreboard or Stage.Finale)) return "There is nothing to continue right now.";

        lock (_gate)
            _continue.TrySetResult();
        return null;
    }

    public IReadOnlyList<ChatMessage> GetTranscript() => Current?.GetTranscript() ?? [];

    /// <summary>Bots revealed so far tonight, in any game. A refreshed screen keeps drawing them as robots.</summary>
    public IReadOnlyList<Guid> GetUnmasked()
    {
        IGameEngine? current;
        HashSet<Guid> all;
        lock (_gate)
        {
            current = _current;
            all = [.._unmasked];
        }

        if (current is not null) all.UnionWith(current.GetUnmasked());
        return all.ToArray();
    }

    public Task NotifyPresenceChangedAsync() => Current?.NotifyPresenceChangedAsync() ?? Task.CompletedTask;
}
