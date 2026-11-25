using TuringTown.Models;
using TuringTown.Services.Ai;
using TuringTown.Services.Games;

namespace TuringTown.Tests;

public class GameFlowTests
{
    [Fact]
    public async Task Stages_run_in_order_and_the_timer_advances_them()
    {
        Harness h = new(humans: 2);

        await h.ThroughOnboardingAsync();
        await h.ToAsync(Stage.Voting, 1);
        await h.ToAsync(Stage.Reveal, 1);

        // Collapse repeats (tutorial steps etc.) and compare the order the clients were told about.
        List<Stage> order = h.States.Select(s => s.Stage).Aggregate(new List<Stage>(), (list, s) =>
        {
            if (list.Count == 0 || list[^1] != s) list.Add(s);
            return list;
        });

        Assert.Equal([Stage.Tutorial, Stage.Onboarding, Stage.Discussion, Stage.Voting, Stage.Reveal], order);
    }

    [Fact]
    public async Task Every_timed_stage_carries_a_deadline()
    {
        Harness h = new(humans: 2);
        await h.ThroughOnboardingAsync();

        GameState discussion = h.Room.State;
        Assert.NotNull(discussion.EndsAtUtcMs);
        long expected = h.Time.GetUtcNow().AddSeconds(h.Options.DiscussionSeconds).ToUnixTimeMilliseconds();
        Assert.InRange(discussion.EndsAtUtcMs!.Value, expected - 3000, expected + 3000);
    }

    [Fact]
    public async Task The_tutorial_ends_by_itself_if_the_host_never_finishes_it()
    {
        Harness h = new(humans: 2);
        await h.ToAsync(Stage.Onboarding);
    }

    [Fact]
    public async Task Humans_win_by_voting_out_the_bot()
    {
        Harness h = new(humans: 2);
        await h.ThroughOnboardingAsync();
        await h.ToAsync(Stage.Voting, 1);

        foreach (Player human in h.Humans) await h.VoteAsync(human, h.BotId);
        await h.ToAsync(Stage.Results);

        Assert.Equal("Humans", h.Results().Winner);
        Assert.True(h.LastReveal().WasBot);
    }

    [Fact]
    public async Task Bots_win_when_they_reach_parity_with_the_humans()
    {
        Harness h = new(humans: 2);
        Player a = h.Humans[0], b = h.Humans[1];
        h.Agent.Vote = _ => a.Id;

        await h.ThroughOnboardingAsync();
        await h.ToAsync(Stage.Voting, 1);

        // The bot and B vote A out: 1 bot vs 1 human left.
        await h.VoteAsync(a, b.Id);
        await h.VoteAsync(b, a.Id);
        await h.ToAsync(Stage.Results);

        Assert.Equal("Bots", h.Results().Winner);
    }

    /// <summary>The survey, answered, which leaves the game at the first beat after it.</summary>
    private static async Task<Harness> ShowAsync(Action<TuringTownOptions> configure, int humans = 3, IShowHost? host = null)
    {
        Harness h = new(humans, configure, host: host);
        await h.ThroughOnboardingAsync();
        return h;
    }

    [Fact]
    public async Task The_first_vote_eliminates_nobody_and_only_fills_the_suspicion_meter()
    {
        Harness h = await ShowAsync(o => o.RapidFireSeconds = 30);
        Player a = h.Humans[0], b = h.Humans[1], c = h.Humans[2];
        await h.ToAsync(Stage.Voting, 1);
        await h.VoteAsync(a, c.Id);
        await h.VoteAsync(b, c.Id);
        await h.VoteAsync(c, a.Id);
        await h.ToAsync(Stage.Reveal, 1);

        RevealData reveal = h.LastReveal();
        Assert.False(reveal.Final);
        Assert.Null(reveal.Eliminated);
        Assert.Null(reveal.WasBot);
        Assert.Equal((c.Id, 2, 67), (reveal.Meter[0].PlayerId, reveal.Meter[0].Votes, reveal.Meter[0].Percent));
        Assert.All(h.Room.GetRoster(), p => Assert.True(p.Alive));

        await h.ToAsync(Stage.Discussion, 2);
        Assert.Equal(RoundKind.Final, ((RoundData)h.Room.State.Data!).Kind);
    }

    [Fact]
    public async Task A_tied_final_vote_is_settled_by_the_suspicion_meter()
    {
        Harness h = await ShowAsync(o => o.RapidFireSeconds = 30, humans: 2);
        Player a = h.Humans[0], b = h.Humans[1];
        await h.ToAsync(Stage.Voting, 1);
        Guid bot = h.BotId;
        await h.VoteAsync(a, bot);
        await h.VoteAsync(b, bot);

        // One vote each in the final: the bot was the most suspected before, so it goes.
        await h.ToAsync(Stage.Voting, 2);
        await h.VoteAsync(a, bot);
        await h.VoteAsync(b, a.Id);
        await h.ToAsync(Stage.Results);

        Assert.Equal(bot, h.LastReveal().Eliminated!.Value.Id);
        Assert.Equal("Humans", h.Results().Winner);
        Assert.Equal(new TeamTotals(300, 0), h.Engine.RoundPointTotals);
        Assert.Null(h.Results().HotSeatWasTheirs);
    }

    [Fact]
    public async Task The_five_answers_are_shown_only_once_everyone_is_done_and_the_bot_answers_too()
    {
        Harness h = await ShowAsync(o => o.RapidFireSeconds = 60, humans: 2);
        await h.ToAsync(Stage.RapidFire, 1);
        RoundData asking = (RoundData)h.Room.State.Data!;
        Assert.Equal(5, asking.Questions!.Count);
        Assert.Null(asking.Answers);

        Assert.NotNull(await h.Engine.SubmitRapidAnswerAsync(h.Humans[0].Id, "q1", "my name is Sam"));
        Assert.NotNull(await h.Engine.SubmitRapidAnswerAsync(h.Humans[0].Id, "q9", "waffles"));
        foreach (Player human in h.Humans)
            for (int i = 1; i <= 5; i++)
                Assert.Null(await h.Engine.SubmitRapidAnswerAsync(human.Id, $"q{i}", $"answer {i} from {human.Id.ToString()[..4]}"));

        await h.ToAsync(Stage.Voting, 1);
        RoundData tap = (RoundData)h.Room.State.Data!;
        Assert.Equal(3, tap.Answers!.Count);
        Assert.All(tap.Answers.Single(a => a.PlayerId == h.BotId).Answers, Assert.NotNull);
    }

    [Fact]
    public async Task The_hot_seat_shows_one_answer_under_the_drawn_name_and_the_truth_only_at_the_results()
    {
        Harness h = await ShowAsync(o => { o.HotTopicSeconds = 30; o.HotSeatSeconds = 5; o.HotSeatSwapPercent = 100; }, humans: 2);
        await h.ToAsync(Stage.HotTopic, 1);
        Assert.Null(((RoundData)h.Room.State.Data!).ShownAnswer);
        Assert.Null(await h.Engine.SubmitHotTopicAsync(h.Humans[0].Id, "purple waffles"));
        Assert.Null(await h.Engine.SubmitHotTopicAsync(h.Humans[1].Id, "grumpy toast"));

        await h.ToAsync(Stage.Discussion, 1);
        RoundData seat = (RoundData)h.Room.State.Data!;
        Assert.Equal(RoundKind.HotSeat, seat.Kind);
        Assert.NotNull(seat.HotSeat);
        Assert.NotNull(seat.ShownAnswer);

        await h.ToAsync(Stage.Voting, 2);
        // Only the drawn answer was ever sent: of the two humans' answers, at most one appears anywhere.
        lock (h.Messages)
            Assert.False(h.Messages.Any(m => m.Json.Contains("purple waffles")) && h.Messages.Any(m => m.Json.Contains("grumpy toast")));
        foreach (Player human in h.Humans) await h.VoteAsync(human, h.BotId);
        await h.ToAsync(Stage.Results);

        Assert.False(h.Results().HotSeatWasTheirs);
    }

    [Fact]
    public async Task A_transition_waits_for_the_screen_and_ends_by_itself()
    {
        Harness h = new(humans: 2, o => o.TransitionMaxSeconds = 600);
        Assert.Null(h.Engine.TutorialDone());
        await h.ToAsync(Stage.Transition);
        Assert.Equal("survey", ((TransitionData)h.Room.State.Data!).Beat);
        Assert.NotNull(h.Engine.TransitionDone("final"));
        Assert.Null(h.Engine.TransitionDone("survey"));
        await h.ToAsync(Stage.Onboarding);

        Harness silent = new(humans: 2, o => o.TransitionMaxSeconds = 3);
        Assert.Null(silent.Engine.TutorialDone());
        await silent.ToAsync(Stage.Onboarding);
    }

    private sealed class BrokenHost : IShowHost
    {
        public Task<RunSheet> PlanAsync(ShowRequest request, CancellationToken ct) =>
            Task.FromResult(new RunSheet(["What is your name?"], "", new Dictionary<string, string>()));

        public Task<string?> TopicAsync(TopicRequest request, CancellationToken ct) => throw new InvalidOperationException("down");
    }

    [Fact]
    public async Task Unusable_content_from_the_host_is_replaced_and_the_game_goes_on()
    {
        Harness h = await ShowAsync(o => { o.RapidFireSeconds = 5; o.HotTopicSeconds = 5; o.HotSeatSeconds = 5; }, host: new BrokenHost());
        await h.ToAsync(Stage.RapidFire, 1);
        Assert.Equal(5, ((RoundData)h.Room.State.Data!).Questions!.Count);
        await h.ToAsync(Stage.HotTopic, 2);
        Assert.False(string.IsNullOrEmpty(((RoundData)h.Room.State.Data!).Topic));
    }

    [Fact]
    public void Suspicion_weights_the_draw_but_everyone_can_be_picked()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        Dictionary<Guid, int> votes = new() { [b] = 2 };

        // Weights 1 and 5: the first roll is a, the other five are b.
        int[] rolls = Enumerable.Range(0, 6).ToArray();
        Assert.Equal([a, b, b, b, b, b], rolls.Select(r => HotSeatDraw.Pick([a, b], votes, _ => r)!.Value));
    }

    [Theory]
    [InlineData(74, false)]
    [InlineData(75, true)]
    public void The_shown_answer_is_swapped_at_the_set_rate(int roll, bool theirs)
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        Dictionary<Guid, string> answers = new() { [a] = "purple waffles", [b] = "grumpy toast" };

        (string? answer, bool? was) = HotSeatDraw.Answer(a, answers, 75, n => n == 100 ? roll : 0);

        Assert.Equal(theirs, was);
        Assert.Equal(theirs ? "purple waffles" : "grumpy toast", answer);
    }

    [Fact]
    public void Missing_answers_still_give_the_hot_seat_something_sensible()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();

        Assert.Equal(("grumpy toast", false), HotSeatDraw.Answer(a, new Dictionary<Guid, string> { [b] = "grumpy toast" }, 0, _ => 0));
        Assert.Equal(("grumpy toast", true), HotSeatDraw.Answer(b, new Dictionary<Guid, string> { [b] = "grumpy toast" }, 100, _ => 0));
        Assert.Equal((null, null), HotSeatDraw.Answer(a, new Dictionary<Guid, string>(), 75, _ => 0));
    }

    [Fact]
    public async Task The_bots_win_when_the_final_vote_is_tied_and_the_meter_cannot_settle_it()
    {
        Harness h = new(humans: 3);
        Player a = h.Humans[0], b = h.Humans[1], c = h.Humans[2];

        await h.ThroughOnboardingAsync();
        await h.ToAsync(Stage.Voting, 1);

        // A three-way tie with an empty meter: nobody is out, so no bot was caught.
        await h.VoteAsync(a, b.Id);
        await h.VoteAsync(b, c.Id);
        await h.VoteAsync(c, a.Id);
        await h.ToAsync(Stage.Results);

        Assert.Equal("Bots", h.Results().Winner);
    }

    [Fact]
    public async Task The_vote_ends_early_once_everyone_has_voted_and_does_not_wait_for_a_player_who_left()
    {
        Harness h = new(humans: 3);
        await h.ThroughOnboardingAsync();
        await h.ToAsync(Stage.Voting, 1);

        // C drops out; only A and B are waited for.
        h.Room.SetDisconnected(h.Humans[2].Id, "conn-2");
        await h.Engine.NotifyPresenceChangedAsync();

        DateTimeOffset before = h.Time.GetUtcNow();
        await h.VoteAsync(h.Humans[0], h.BotId);
        await h.VoteAsync(h.Humans[1], h.BotId);
        await h.ToAsync(Stage.Reveal, 1);

        Assert.True((h.Time.GetUtcNow() - before).TotalSeconds < h.Options.VotingSeconds);
    }

    [Fact]
    public async Task After_the_results_the_room_returns_to_the_lobby_without_the_bots()
    {
        Harness h = new(humans: 2);
        await h.ThroughOnboardingAsync();
        await h.ToAsync(Stage.Voting, 1);
        foreach (Player human in h.Humans) await h.VoteAsync(human, h.BotId);
        await h.ToAsync(Stage.Results);

        Assert.Null(h.Engine.EndResults());
        await h.Engine.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(Stage.Lobby, h.Room.State.Stage);
        Assert.Equal(2, h.Room.GetRoster().Count);
        Assert.All(h.Room.GetRoster(), p => Assert.True(p.Alive));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(6, 2)]
    public void Bot_count_follows_the_number_of_humans(int humans, int bots) =>
        Assert.Equal(bots, TuringTownEngine.BotCountFor(humans));
}
