using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TuringTown.Models;
using TuringTown.Services.Ai;
using TuringTown.Services.Games;

namespace TuringTown.Tests;

/// <summary>A Witty Bits bot the tests control. Default: a fixed answer and no vote.</summary>
internal sealed class TestWittyAgent : IWittyBotAgent
{
    public Func<WittyAnswerRequest, string?> Write { get; set; } = r => $"robot joke {Math.Abs(r.Prompt.GetHashCode()) % 1000}";
    public Func<WittyVoteRequest, string?> Vote { get; set; } = _ => null;
    public List<WittyVoteRequest> VoteRequests { get; } = [];

    public Task<string?> WriteAnswerAsync(WittyAnswerRequest request, CancellationToken ct) => Task.FromResult(Write(request));

    public Task<string?> VoteAsync(WittyVoteRequest request, CancellationToken ct)
    {
        lock (VoteRequests) VoteRequests.Add(request);
        return Task.FromResult(Vote(request));
    }
}

/// <summary>A room playing one Witty Bits game on a fake clock, recording every broadcast as the JSON a client gets.</summary>
internal sealed class WittyHarness : IRoomBroadcaster
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public Room Room { get; }
    public FakeTimeProvider Time { get; } = new();
    public TestWittyAgent Agent { get; } = new();
    public WittyBitsEngine Engine { get; }
    public Task<GameOutcome> Outcome { get; }
    public List<Player> Humans { get; } = [];
    public List<GameState> States { get; } = [];
    public List<(Stage Stage, string Json)> Messages { get; } = [];

    public WittyBitsOptions Options { get; } = new()
    {
        TutorialMaxSeconds = 10,
        WritingSeconds = 20,
        LastBitWritingSeconds = 20,
        MatchupSeconds = 10,
        MatchupResultSeconds = 2,
        ResultsSeconds = 5,
        BotAnswerMinDelayMs = 0,
        BotAnswerMaxDelayMs = 0,
        BotVoteMinDelayMs = 0,
        BotVoteMaxDelayMs = 0
    };

    public WittyHarness(int humans, Action<WittyBitsOptions>? configure = null, IContentFilter? contentFilter = null, IWittyPromptGenerator? prompts = null)
    {
        configure?.Invoke(Options);
        Room = new Room(RoomCode.CreateRandomRoomCode(), new GameOptions { MaxPlayers = 6, MinPlayers = 2 });
        for (int i = 0; i < humans; i++)
        {
            Assert.True(Room.TryAddPlayer(out Player? p, out _));
            Humans.Add(p!);
            Room.SetConnected(p!.Id, $"conn-{i}");
            Room.TrySetReady(p.Id, true);
        }
        Assert.Equal(RoomStartError.None, Room.TryStart(GameNight.BotCountFor));

        Engine = new WittyBitsEngine(Room, Agent, prompts ?? new BuiltInWittyPrompts(), contentFilter ?? new AllowAllContentFilter(), this, Time, Options, NullLogger.Instance);
        Outcome = Task.Run(Engine.PlayAsync);
    }

    public Stage Stage => Room.State.Stage;
    public IReadOnlyList<PlayerSeat> Seats => Room.GetSeats();
    public Guid BotId => Seats.First(s => s.IsBot).Id;
    public WritingData Writing => (WritingData)Room.State.Data!;
    public MatchupData Matchup => (MatchupData)Room.State.Data!;

    public async Task UntilAsync(Func<bool> condition, string what)
    {
        for (int i = 0; i < 4000; i++)
        {
            if (condition()) return;
            Time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(1);
        }
        throw new TimeoutException($"Never reached: {what} (stuck in {Room.State.Stage} {Room.State.Round}/{Room.State.Step})");
    }

    public Task ToAsync(Stage stage, int? round = null, int? step = null) =>
        UntilAsync(() => Stage == stage && (round is null || Room.State.Round == round) && (step is null || Room.State.Step == step), $"{stage} {round} {step}");

    public async Task ToWritingAsync(int round = 1)
    {
        if (round == 1)
        {
            await ToAsync(Stage.Tutorial);
            Assert.Null(Engine.TutorialDone());
        }
        await ToAsync(Stage.Writing, round);
    }

    /// <summary>Every human answers all their prompts: <paramref name="text"/> gets the player and prompt.</summary>
    public async Task AnswerAllAsync(Func<Player, WittyAssignment, string?> text)
    {
        foreach (WittyAssignment a in Writing.Assignments)
            foreach (Player human in Humans.Where(h => a.PlayerIds.Contains(h.Id)))
                if (text(human, a) is { } t)
                    Assert.Null(await Engine.SubmitAnswerAsync(human.Id, a.PromptId, t));
    }

    public MatchupResultData LastResult() => (MatchupResultData)States.Last(s => s.Stage == Stage.MatchupResult).Data!;

    public Task StateChanged(Room room, GameState state)
    {
        lock (States) States.Add(state);
        lock (Messages) Messages.Add((state.Stage, JsonSerializer.Serialize(state, Json)));
        return Task.CompletedTask;
    }

    public Task RosterChanged(Room room)
    {
        lock (Messages) Messages.Add((room.State.Stage, JsonSerializer.Serialize(room.GetRoster(), Json)));
        return Task.CompletedTask;
    }

    public Task ChatMessage(Room room, ChatMessage message) => Task.CompletedTask;
    public Task Typing(Room room, Guid playerId, bool typing) => Task.CompletedTask;
    public List<(int Done, int Total)> ProgressEvents { get; } = [];

    public Task Progress(Room room, int done, int total)
    {
        lock (ProgressEvents) ProgressEvents.Add((done, total));
        return Task.CompletedTask;
    }

    public List<string> Errors { get; } = [];

    public Task GameError(Room room, string message)
    {
        lock (Errors) Errors.Add(message);
        return Task.CompletedTask;
    }
}

public class WittyBitsFlowTests
{
    private sealed class RejectContent : IContentFilter
    {
        public Task<bool> IsAllowedAsync(string text, ContentPolicy policy, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class FixedPrompts(IReadOnlyList<WittyPrompt> prompts) : IWittyPromptGenerator
    {
        public Task<IReadOnlyList<WittyPrompt>> GenerateAsync(WittyPromptRequest request, CancellationToken ct) => Task.FromResult(prompts);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task Each_paired_round_gives_every_player_two_prompts_shared_with_two_different_players(int humans)
    {
        WittyHarness h = new(humans);
        int seats = h.Seats.Count;
        Assert.True(seats >= 4);

        await h.ToWritingAsync();
        for (int round = 1; round <= 2; round++)
        {
            if (round > 1) await h.ToAsync(Stage.Writing, round);
            WritingData writing = h.Writing;
            Assert.Equal(round, writing.Round);
            Assert.False(writing.LastBit);
            Assert.Equal(seats, writing.Assignments.Count);
            Assert.All(writing.Assignments, a => Assert.Equal(2, a.PlayerIds.Distinct().Count()));
            Assert.All(h.Seats, s => Assert.Equal(2, writing.Assignments.Count(a => a.PlayerIds.Contains(s.Id))));
            Assert.Equal(seats, writing.Assignments.Select(a => a.Text).Distinct().Count());

            // Nobody answers: the matchups are skipped and the game moves on.
        }

        await h.ToAsync(Stage.Writing, 3);
        WittyAssignment last = Assert.Single(h.Writing.Assignments);
        Assert.True(h.Writing.LastBit);
        Assert.Equal(h.Seats.Select(s => s.Id).Order(), last.PlayerIds.Order());
    }

    [Fact]
    public async Task Writing_ends_early_once_everyone_has_answered_and_the_bots_always_answer()
    {
        WittyHarness h = new(3);
        await h.ToWritingAsync();
        await h.AnswerAllAsync((p, a) => $"{p.Alias} on {a.PromptId}");

        // Ends well before the 20 s limit: the bot answered at once.
        DateTimeOffset start = h.Time.GetUtcNow();
        await h.ToAsync(Stage.Matchup, 1, 0);
        Assert.True(h.Time.GetUtcNow() - start < TimeSpan.FromSeconds(5));
        Assert.Contains(h.ProgressEvents, p => p.Done == p.Total && p.Total == 8);
    }

    [Fact]
    public async Task Answers_are_checked_like_chat_and_only_go_to_your_own_prompts()
    {
        WittyHarness h = new(3);
        await h.ToWritingAsync();
        Player me = h.Humans[0];
        WittyAssignment mine = h.Writing.Assignments.First(a => a.PlayerIds.Contains(me.Id));
        WittyAssignment notMine = h.Writing.Assignments.First(a => !a.PlayerIds.Contains(me.Id));

        Assert.NotNull(await h.Engine.SubmitAnswerAsync(me.Id, notMine.PromptId, "ha"));
        Assert.NotNull(await h.Engine.SubmitAnswerAsync(me.Id, "nope", "ha"));
        Assert.NotNull(await h.Engine.SubmitAnswerAsync(me.Id, mine.PromptId, "   "));
        Assert.NotNull(await h.Engine.SubmitAnswerAsync(me.Id, mine.PromptId, "mail me at me@example.com"));
        Assert.NotNull(await h.Engine.SubmitAnswerAsync(me.Id, mine.PromptId, new string('x', h.Options.AnswerMaxLength + 1)));
        Assert.NotNull(await h.Engine.SubmitAnswerAsync(h.BotId, mine.PromptId, "ha"));
        Assert.Null(await h.Engine.SubmitAnswerAsync(me.Id, mine.PromptId, "  a   fine\tanswer "));
        Assert.Null(await h.Engine.SubmitAnswerAsync(me.Id, mine.PromptId, "a better answer"));

        // No answer is ever in the Writing state.
        Assert.DoesNotContain(h.Messages, m => m.Json.Contains("a better answer") || m.Json.Contains("fine answer"));
    }

    [Fact]
    public async Task Authors_sit_their_matchup_out_and_everyone_else_votes_once_for_a_written_answer()
    {
        WittyHarness h = new(3);
        await h.ToWritingAsync();
        await h.AnswerAllAsync((p, a) => $"{p.Alias} on {a.PromptId}");
        await h.ToAsync(Stage.Matchup, 1, 0);

        MatchupData m = h.Matchup;
        Assert.Equal(2, m.Answers.Count);
        Assert.Equal(2, m.Authors.Count);
        // No author is attached to an answer before the vote ends.
        Assert.DoesNotContain(h.Messages.Last().Json, h.Seats.Where(s => m.Authors.Contains(s.Id)).Select(s => s.Alias).First());

        Player? author = h.Humans.FirstOrDefault(p => m.Authors.Contains(p.Id));
        if (author is not null) Assert.NotNull(await h.Engine.CastVoteAsync(author.Id, "a"));

        Player voter = h.Humans.First(p => !m.Authors.Contains(p.Id));
        Assert.NotNull(await h.Engine.CastVoteAsync(voter.Id, "z"));
        Assert.Null(await h.Engine.CastVoteAsync(voter.Id, "a"));
        Assert.Null(await h.Engine.CastVoteAsync(voter.Id, "b"));
    }

    [Fact]
    public async Task Points_are_shared_by_vote_a_sweep_earns_a_bonus_and_round_two_is_worth_double()
    {
        WittyHarness h = new(3);
        await h.ToWritingAsync();
        await h.AnswerAllAsync((p, a) => $"{p.Alias} on {a.PromptId}");
        h.Agent.Vote = r => r.Answers[0].Id;

        // Round 1, first matchup: every voter picks answer "a".
        await h.ToAsync(Stage.Matchup, 1, 0);
        MatchupData m = h.Matchup;
        foreach (Player voter in h.Humans.Where(p => !m.Authors.Contains(p.Id)))
            Assert.Null(await h.Engine.CastVoteAsync(voter.Id, "a"));
        await h.ToAsync(Stage.MatchupResult, 1, 0);

        MatchupResultData result = h.LastResult();
        WittyEntryResult a = result.Answers.Single(x => x.Id == "a");
        WittyEntryResult b = result.Answers.Single(x => x.Id == "b");
        Assert.Equal(2, a.Votes);
        Assert.True(a.Sweep);
        Assert.Equal(1000 + 250, a.Points);
        Assert.Equal(0, b.Points);
        Assert.Contains(m.Authors, id => id == a.AuthorId);
        Assert.Equal(h.Seats.Single(s => s.Id == a.AuthorId).Alias, a.Alias);

        // Round 2: a split vote shares the points, doubled.
        await h.ToAsync(Stage.Writing, 2);
        await h.AnswerAllAsync((p, x) => $"{p.Alias} again on {x.PromptId}");
        await h.ToAsync(Stage.Matchup, 2, 0);
        m = h.Matchup;
        Player[] voters = h.Humans.Where(p => !m.Authors.Contains(p.Id)).ToArray();
        bool botVotes = h.Seats.Any(s => s.IsBot && !m.Authors.Contains(s.Id));
        if (voters.Length + (botVotes ? 1 : 0) == 2)
        {
            // Two votes: one each way (the bot always picks the first answer it is offered).
            if (botVotes)
            {
                await h.UntilAsync(() => { lock (h.Agent.VoteRequests) return h.Agent.VoteRequests.Count(r => r.Prompt == m.Prompt) > 0; }, "the bot's vote");
                string botPick;
                lock (h.Agent.VoteRequests) botPick = h.Agent.VoteRequests.Last(r => r.Prompt == m.Prompt).Answers[0].Id;
                Assert.Null(await h.Engine.CastVoteAsync(voters[0].Id, botPick == "a" ? "b" : "a"));
            }
            else
            {
                Assert.Null(await h.Engine.CastVoteAsync(voters[0].Id, "a"));
                Assert.Null(await h.Engine.CastVoteAsync(voters[1].Id, "b"));
            }

            await h.ToAsync(Stage.MatchupResult, 2, 0);
            Assert.All(h.LastResult().Answers, x => Assert.Equal((1, 1000, false), (x.Votes, x.Points, x.Sweep)));
        }
    }

    [Fact]
    public async Task A_lone_answer_wins_by_default_without_a_vote()
    {
        WittyHarness h = new(3);
        await h.ToWritingAsync();
        // Only the bot answers, so every matchup it is in has a lone answer.
        h.Time.Advance(TimeSpan.FromSeconds(21));
        await h.ToAsync(Stage.MatchupResult, 1);

        MatchupResultData result = h.LastResult();
        WittyEntryResult lone = result.Answers.Single(x => !x.Missing);
        Assert.Equal(h.BotId, lone.AuthorId);
        Assert.Equal(500, lone.Points);
        Assert.Equal("No answer", result.Answers.Single(x => x.Missing).Text);
        Assert.DoesNotContain(h.States, s => s.Stage == Stage.Matchup && s.Round == 1 && s.Step == result.Index);
    }

    [Fact]
    public async Task A_bad_bot_answer_is_replaced_by_a_scripted_one_that_passes_the_same_checks()
    {
        WittyHarness h = new(3);
        h.Agent.Write = _ => "visit www.example.com";
        await h.ToWritingAsync();
        await h.AnswerAllAsync((p, a) => $"{p.Alias} on {a.PromptId}");
        await h.ToAsync(Stage.MatchupResult, 1, 0);
        await h.UntilAsync(() => h.States.Any(s => s.Stage == Stage.MatchupResult && ((MatchupResultData)s.Data!).Answers.Any(x => x.AuthorId == h.BotId)), "the bot's answer");

        WittyEntryResult bot = h.States.Where(s => s.Stage == Stage.MatchupResult).SelectMany(s => ((MatchupResultData)s.Data!).Answers).First(x => x.AuthorId == h.BotId);
        Assert.False(bot.Missing);
        Assert.DoesNotContain("example", bot.Text);
        Assert.Equal(ChatCheck.Ok, ChatFilter.Check(bot.Text, h.Options.AnswerMaxLength, out _));
    }

    [Fact]
    public async Task In_the_last_bit_everyone_votes_but_never_for_their_own_answer_and_each_vote_scores()
    {
        WittyHarness h = new(3, o => o.PairRounds = 1);
        await h.ToWritingAsync();
        h.Time.Advance(TimeSpan.FromSeconds(21));

        await h.ToAsync(Stage.Writing, 2);
        Assert.True(h.Writing.LastBit);
        await h.AnswerAllAsync((p, _) => $"last bit from {p.Alias}");
        await h.ToAsync(Stage.Matchup, 2, 0);

        MatchupData m = h.Matchup;
        Assert.True(m.LastBit);
        Assert.Equal(4, m.Answers.Count);
        Player me = h.Humans[0];
        string own = m.Answers.Single(x => x.Text == $"last bit from {me.Alias}").Id;
        string other = m.Answers.First(x => x.Text == $"last bit from {h.Humans[1].Alias}").Id;
        Assert.NotNull(await h.Engine.CastVoteAsync(me.Id, own));
        Assert.Null(await h.Engine.CastVoteAsync(me.Id, other));

        await h.ToAsync(Stage.MatchupResult, 2, 0);
        WittyEntryResult got = h.LastResult().Answers.Single(x => x.Id == other);
        Assert.Equal(got.Votes * 400, got.Points);
        Assert.True(got.Votes >= 1);
    }

    [Fact]
    public async Task The_game_ends_with_everyones_points_and_returns_them_as_the_outcome()
    {
        WittyHarness h = new(3, o => o.PairRounds = 1);
        h.Agent.Vote = r => r.Answers[0].Id;
        await h.ToWritingAsync();
        await h.AnswerAllAsync((p, a) => $"{p.Alias} on {a.PromptId}");
        await h.ToAsync(Stage.Writing, 2);
        await h.AnswerAllAsync((p, _) => $"last from {p.Alias}");
        await h.ToAsync(Stage.Results);

        WittyResultsData results = (WittyResultsData)h.Room.State.Data!;
        Assert.Equal(4, results.Standings.Count);
        Assert.Equal(results.Standings.Select(s => s.Points).OrderDescending(), results.Standings.Select(s => s.Points));

        Assert.NotNull(h.Engine.TutorialDone());
        Assert.Null(h.Engine.EndResults());
        GameOutcome outcome = await h.Outcome.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(outcome.Completed);
        Assert.Null(outcome.Winner);
        Assert.Empty(outcome.Unmasked);
        Assert.All(results.Standings, s => Assert.Equal(s.Points, outcome.SeatPoints.GetValueOrDefault(s.PlayerId)));

        // Nothing Witty Bits sent says who is a bot.
        Assert.All(h.Messages, m => Assert.DoesNotContain("isBot", m.Json, StringComparison.OrdinalIgnoreCase));
        Assert.All(h.Messages, m => Assert.DoesNotContain("wasBot", m.Json, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_player_who_drops_is_not_waited_for()
    {
        WittyHarness h = new(3);
        await h.ToWritingAsync();
        Player gone = h.Humans[2];
        h.Room.SetDisconnected(gone.Id, "conn-2");
        await h.Engine.NotifyPresenceChangedAsync();
        await h.AnswerAllAsync((p, a) => p == gone ? null : $"{p.Alias} on {a.PromptId}");

        DateTimeOffset start = h.Time.GetUtcNow();
        await h.UntilAsync(() => h.Stage is Stage.Matchup or Stage.MatchupResult, "the matchups");
        Assert.True(h.Time.GetUtcNow() - start < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Pausing_stops_the_clock_and_refuses_answers()
    {
        WittyHarness h = new(3);
        await h.ToWritingAsync();
        Assert.Null(await h.Engine.SetPausedAsync(true));
        Assert.NotNull(h.Room.State.PausedAtUtcMs);

        WittyAssignment mine = h.Writing.Assignments.First(a => a.PlayerIds.Contains(h.Humans[0].Id));
        Assert.NotNull(await h.Engine.SubmitAnswerAsync(h.Humans[0].Id, mine.PromptId, "ha"));

        h.Time.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(20);
        Assert.Equal(Stage.Writing, h.Stage);

        Assert.Null(await h.Engine.SetPausedAsync(false));
        Assert.Null(h.Room.State.PausedAtUtcMs);
    }

    [Fact]
    public async Task Bad_generated_prompts_are_replaced_by_built_in_ones()
    {
        WittyHarness h = new(3, prompts: new FixedPrompts([new WittyPrompt("What is your full name?", false)]));
        await h.ToWritingAsync();
        Assert.All(h.Writing.Assignments, a => Assert.DoesNotContain("full name", a.Text));
    }

    [Fact]
    public async Task When_no_prompt_passes_the_content_filter_the_game_reports_an_error()
    {
        WittyHarness h = new(3, contentFilter: new RejectContent());
        await h.ToAsync(Stage.Tutorial);
        Assert.Null(h.Engine.TutorialDone());
        GameOutcome outcome = await h.Outcome.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(outcome.Completed);
        Assert.Single(h.Errors);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_built_in_prompts_pass_validation(bool familyFriendly)
    {
        ContentPolicy policy = new(familyFriendly);
        int count = BuiltInWittyPrompts.Count(policy);
        Assert.True(count >= 21, "enough prompts for a room of ten");
        Assert.Null(WittyPromptValidator.Problem(BuiltInWittyPrompts.Pick(policy, count), policy, count));
    }

    [Fact]
    public void The_prompt_validator_rejects_bad_sets()
    {
        ContentPolicy family = new(true);
        Assert.NotNull(WittyPromptValidator.Problem(null, family, 1));
        Assert.NotNull(WittyPromptValidator.Problem([new WittyPrompt("A fine prompt here", false)], family, 2));
        Assert.NotNull(WittyPromptValidator.Problem([new WittyPrompt("A fine prompt here", false), new WittyPrompt("a fine prompt here", false)], family, 2));
        Assert.NotNull(WittyPromptValidator.Problem([new WittyPrompt("Short", false)], family, 1));
        Assert.NotNull(WittyPromptValidator.Problem([new WittyPrompt("Where do you live, roughly?", false)], family, 1));
        Assert.NotNull(WittyPromptValidator.Problem([new WittyPrompt("A cheeky prompt for adults", true)], family, 1));
        Assert.Null(WittyPromptValidator.Problem([new WittyPrompt("A cheeky prompt for adults", true)], new ContentPolicy(false), 1));
    }
}
