using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TuringTown.Models;
using TuringTown.Services.Ai;
using TuringTown.Services.Games;

namespace TuringTown.Tests;

/// <summary>
/// The game night: the AI's plan, playing each game in turn on the same seats, the score, and keeping the team
/// totals secret while a bot is hidden. The games here are stand-ins, so only the night's own rules are tested.
/// </summary>
public class GameNightTests
{
    /// <summary>A game that goes straight to its Results stage and ends with whatever outcome the test gives it.</summary>
    private sealed class FakeEngine(Room room, IRoomBroadcaster broadcaster, string key, Func<Room, GameOutcome> outcome) : IGameEngine
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cts = new();

        public bool EveryoneAliveAtStart { get; private set; }

        public string GameKey => key;

        public async Task<GameOutcome> PlayAsync()
        {
            EveryoneAliveAtStart = room.GetSeats().All(s => s.Alive);
            GameState state = room.SetState(new GameState(key, Stage.Results, 0, 0, null, null));
            await broadcaster.StateChanged(room, state);
            try
            {
                await _done.Task.WaitAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return GameOutcome.Failed;
            }
            return outcome(room);
        }

        public void Cancel() => _cts.Cancel();
        public bool IsPaused => false;
        public Task<string?> SetPausedAsync(bool paused) => Task.FromResult<string?>(null);
        public Task<string?> TutorialStepAsync(int step) => Task.FromResult<string?>(null);
        public string? TutorialDone() => null;

        public string? EndResults()
        {
            _done.TrySetResult();
            return null;
        }

        public Task NotifyPresenceChangedAsync() => Task.CompletedTask;
        public IReadOnlyList<ChatMessage> GetTranscript() => [];
        public IReadOnlyList<Guid> GetUnmasked() => [];
        public void Dispose() => _cts.Dispose();
    }

    private sealed class FixedPlanner(Func<GameNightPlan> plan) : IGameNightPlanner
    {
        public Task<GameNightPlan> PlanAsync(GameNightPlanRequest request, CancellationToken ct) => Task.FromResult(plan());
    }

    /// <summary>A room with <c>humans</c> players and a night of games "a" and "b" on a fake clock.</summary>
    private sealed class NightHarness : IRoomBroadcaster
    {
        public Room Room { get; }
        public FakeTimeProvider Time { get; } = new();
        public GameNight Night { get; }
        public List<GameState> States { get; } = [];
        public Dictionary<string, FakeEngine> Engines { get; } = [];

        public NightHarness(int humans, IGameNightPlanner planner, Func<Room, GameOutcome> a, Func<Room, GameOutcome> b)
        {
            Room = new Room(RoomCode.CreateRandomRoomCode(), new GameOptions { MaxPlayers = 6, MinPlayers = 2 });
            for (int i = 0; i < humans; i++)
            {
                Assert.True(Room.TryAddPlayer(out Player? p, out _));
                Room.SetConnected(p!.Id, $"conn-{i}");
                Room.TrySetReady(p.Id, true);
            }
            Assert.Equal(RoomStartError.None, Room.TryStart(GameNight.BotCountFor));

            NightGameEntry Entry(string id, Func<Room, GameOutcome> outcome) => new(new PlannableGame(id, id.ToUpperInvariant(), "a game"), () =>
            {
                FakeEngine engine = new(Room, this, id, outcome);
                lock (Engines) Engines[id] = engine;
                return engine;
            });

            GameNightOptions options = new() { PlanningMaxSeconds = 5, PlanRevealSeconds = 1, ScoreboardSeconds = 30, FinaleSeconds = 30, TuringTownWinPoints = 1000 };
            Night = new GameNight(Room, [Entry("a", a), Entry("b", b)], planner, this, Time, options, NullLogger.Instance);
            Night.Start();
        }

        public IReadOnlyList<PlayerSeat> Seats => Room.GetSeats();

        public async Task UntilAsync(Func<bool> condition, string what)
        {
            for (int i = 0; i < 4000; i++)
            {
                if (condition()) return;
                Time.Advance(TimeSpan.FromMilliseconds(250));
                await Task.Delay(1);
            }
            throw new TimeoutException($"Never reached: {what} (stuck in {Room.State.GameId} {Room.State.Stage})");
        }

        public Task AtAsync(string gameId, Stage stage) => UntilAsync(() => Room.State.GameId == gameId && Room.State.Stage == stage, $"{gameId} {stage}");

        /// <summary>Ends the current game's Results (or the Scoreboard / Finale) the way the host's button does.</summary>
        public void Continue() => Assert.Null(Night.Continue());

        public async Task PlayThroughAsync(string first, string second)
        {
            await AtAsync(first, Stage.Results);
            Continue();
            await AtAsync(GameIds.Night, Stage.Scoreboard);
            Continue();
            await AtAsync(second, Stage.Results);
            Continue();
            await AtAsync(GameIds.Night, Stage.Finale);
        }

        public T Last<T>(Stage stage)
        {
            lock (States) return (T)States.Last(s => s.Stage == stage && s.GameId == GameIds.Night).Data!;
        }

        public Task StateChanged(Room room, GameState state)
        {
            lock (States) States.Add(state);
            return Task.CompletedTask;
        }

        public Task RosterChanged(Room room) => Task.CompletedTask;
        public Task ChatMessage(Room room, ChatMessage message) => Task.CompletedTask;
        public Task Typing(Room room, Guid playerId, bool typing) => Task.CompletedTask;
        public Task Progress(Room room, int done, int total) => Task.CompletedTask;
        public Task GameError(Room room, string message) => Task.CompletedTask;
    }

    private static GameOutcome NoPoints(Room _) => new(true, null, new Dictionary<Guid, int>(), []);

    private static IGameNightPlanner Plan(params string[] order) => new FixedPlanner(() => new GameNightPlan(order, "We have a plan."));

    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(6, 2)]
    public void A_night_seats_enough_bots_for_turing_town_and_at_least_four_players(int humans, int bots)
    {
        Assert.Equal(bots, GameNight.BotCountFor(humans));
    }

    [Fact]
    public async Task The_games_are_played_in_the_planners_order_and_the_night_ends_in_the_lobby_without_the_bots()
    {
        NightHarness h = new(2, Plan("b", "a"), NoPoints, NoPoints);

        await h.UntilAsync(() => h.States.Any(s => s.Stage == Stage.Planning && ((PlanningData)s.Data!).Order.Count == 2), "the plan");
        PlanningData plan = h.Last<PlanningData>(Stage.Planning);
        Assert.Equal(["b", "a"], plan.Order.Select(g => g.GameId));
        Assert.Equal("We have a plan.", plan.Line);
        Assert.Equal(4, h.Seats.Count);

        // Only the night's own stages and a game's Results can be continued.
        Assert.NotNull(h.Night.Continue());

        await h.PlayThroughAsync("b", "a");
        Assert.Equal(["a"], h.Last<ScoreboardData>(Stage.Scoreboard).Next is { } next ? [next.GameId] : Array.Empty<string>());

        h.Continue();
        await h.Night.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Stage.Lobby, h.Room.State.Stage);
        Assert.Equal(GameIds.Night, h.Room.State.GameId);
        Assert.Equal(2, h.Seats.Count);
        Assert.DoesNotContain(h.Seats, s => s.IsBot);
    }

    [Theory]
    [InlineData("a", "a")]
    [InlineData("a", "zzz")]
    [InlineData("a")]
    public async Task A_plan_that_does_not_play_every_game_once_is_replaced_by_a_random_order(params string[] order)
    {
        NightHarness h = new(3, Plan(order), NoPoints, NoPoints);

        await h.UntilAsync(() => h.States.Any(s => s.Stage == Stage.Planning && ((PlanningData)s.Data!).Order.Count > 0), "the plan");
        string[] played = h.Last<PlanningData>(Stage.Planning).Order.Select(g => g.GameId).ToArray();
        Assert.Equal(["a", "b"], played.Order());

        await h.PlayThroughAsync(played[0], played[1]);
    }

    [Fact]
    public async Task A_planner_that_fails_still_gets_a_night()
    {
        NightHarness h = new(3, new FixedPlanner(() => throw new InvalidOperationException("down")), NoPoints, NoPoints);
        await h.UntilAsync(() => h.States.Any(s => s.Stage == Stage.Planning && ((PlanningData)s.Data!).Order.Count == 2), "the fallback plan");
    }

    [Fact]
    public async Task A_player_voted_out_in_one_game_plays_the_next()
    {
        NightHarness h = new(3, Plan("a", "b"), room =>
        {
            room.Eliminate(room.GetSeats().First(s => !s.IsBot).Id);
            return NoPoints(room);
        }, NoPoints);

        await h.AtAsync("a", Stage.Results);
        h.Continue();
        await h.AtAsync(GameIds.Night, Stage.Scoreboard);
        Assert.Contains(h.Seats, s => !s.Alive);

        h.Continue();
        await h.AtAsync("b", Stage.Results);
        Assert.True(h.Engines["b"].EveryoneAliveAtStart);
    }

    [Fact]
    public async Task Points_games_count_the_average_per_seat_and_a_team_win_adds_the_flat_bonus()
    {
        // 2 humans and 2 bots. Game "a" is a team game the bots win; game "b" gives points per seat.
        NightHarness h = new(2, Plan("a", "b"),
            room => new GameOutcome(true, "Bots", new Dictionary<Guid, int>(), room.GetSeats().Where(s => s.IsBot).Select(s => s.Id).ToArray()),
            room =>
            {
                PlayerSeat[] humans = room.GetSeats().Where(s => !s.IsBot).ToArray();
                PlayerSeat[] bots = room.GetSeats().Where(s => s.IsBot).ToArray();
                return new GameOutcome(true, null, new Dictionary<Guid, int>
                {
                    [humans[0].Id] = 300, [humans[1].Id] = 100, [bots[0].Id] = 500, [bots[1].Id] = 0
                }, []);
            });

        await h.PlayThroughAsync("a", "b");

        FinaleData finale = h.Last<FinaleData>(Stage.Finale);
        Assert.Equal(new TeamTotals(200, 1250), finale.Teams);
        Assert.Equal("AI", finale.Winner);
        Assert.Equal(4, finale.Roles.Count);
        Assert.Equal(2, finale.Roles.Count(r => r.WasBot));
        Assert.Equal([500, 300, 100, 0], finale.Standings.Select(s => s.Points));
    }

    [Fact]
    public async Task The_team_totals_stay_hidden_until_every_bot_has_been_unmasked()
    {
        // A points game first: nobody has been unmasked, so a team total could point at the bot.
        NightHarness hidden = new(3, Plan("a", "b"),
            room => new GameOutcome(true, null, room.GetSeats().ToDictionary(s => s.Id, s => s.IsBot ? 700 : 100), []), NoPoints);
        await hidden.AtAsync("a", Stage.Results);
        hidden.Continue();
        await hidden.AtAsync(GameIds.Night, Stage.Scoreboard);
        ScoreboardData board = hidden.Last<ScoreboardData>(Stage.Scoreboard);
        Assert.Null(board.Teams);
        Assert.All(board.Standings, s => Assert.NotEqual(Guid.Empty, s.PlayerId));

        // A game that unmasked every bot first: the totals can be shown.
        NightHarness shown = new(3, Plan("a", "b"),
            room => new GameOutcome(true, "Humans", new Dictionary<Guid, int>(), room.GetSeats().Where(s => s.IsBot).Select(s => s.Id).ToArray()), NoPoints);
        await shown.AtAsync("a", Stage.Results);
        shown.Continue();
        await shown.AtAsync(GameIds.Night, Stage.Scoreboard);
        Assert.Equal(new TeamTotals(1000, 0), shown.Last<ScoreboardData>(Stage.Scoreboard).Teams);
        Assert.Equal(shown.Seats.Where(s => s.IsBot).Select(s => s.Id).Order(), shown.Night.GetUnmasked().Order());
    }

    [Fact]
    public async Task A_game_that_fails_ends_the_night()
    {
        NightHarness h = new(3, Plan("a", "b"), _ => GameOutcome.Failed, NoPoints);

        await h.AtAsync("a", Stage.Results);
        h.Continue();
        await h.Night.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(Stage.Lobby, h.Room.State.Stage);
        Assert.False(h.Engines.ContainsKey("b"));
        Assert.DoesNotContain(h.States, s => s.Stage is Stage.Scoreboard or Stage.Finale);
    }

    [Fact]
    public async Task Closing_the_room_stops_the_game_being_played()
    {
        NightHarness h = new(3, Plan("a", "b"), NoPoints, NoPoints);
        await h.AtAsync("a", Stage.Results);

        h.Night.Cancel();
        await h.Night.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Stage.Lobby, h.Room.State.Stage);
        Assert.False(h.Engines.ContainsKey("b"));
    }
}
