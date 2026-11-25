using TuringTown.Models;
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

    [Fact]
    public async Task An_early_vote_eliminates_nobody_and_only_fills_the_suspicion_meter()
    {
        Harness h = new(humans: 3, o => o.MaxRounds = 2);
        Player a = h.Humans[0], b = h.Humans[1], c = h.Humans[2];

        await h.ThroughOnboardingAsync();
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
        Harness h = new(humans: 2, o => o.MaxRounds = 2);
        Player a = h.Humans[0], b = h.Humans[1];
        Guid bot = Guid.Empty;

        await h.ThroughOnboardingAsync();
        await h.ToAsync(Stage.Voting, 1);
        bot = h.BotId;
        await h.VoteAsync(a, bot);
        await h.VoteAsync(b, bot);

        // One vote each in the Final: the bot was the most suspected before, so it goes.
        await h.ToAsync(Stage.Voting, 2);
        await h.VoteAsync(a, bot);
        await h.VoteAsync(b, a.Id);
        await h.ToAsync(Stage.Results);

        Assert.Equal(bot, h.LastReveal().Eliminated!.Value.Id);
        Assert.Equal("Humans", h.Results().Winner);
    }

    [Fact]
    public async Task Quick_Fire_hides_the_answers_until_the_chat_opens_and_the_bot_answers_too()
    {
        Harness h = new(humans: 2, o => { o.MaxRounds = 2; o.QuickFireSeconds = 10; });
        Assert.Null(h.Engine.TutorialDone());
        await h.ToAsync(Stage.Onboarding);
        foreach (Player human in h.Humans) Assert.Null(await h.Engine.SubmitOnboardingAsync(human.Id, h.Answers()));
        await h.ToAsync(Stage.QuickFire, 1);

        Assert.False(string.IsNullOrEmpty(((RoundData)h.Room.State.Data!).Prompt));
        Assert.NotNull(await h.Engine.SubmitQuickFireAsync(h.Humans[0].Id, "my name is Sam"));
        Assert.Null(await h.Engine.SubmitQuickFireAsync(h.Humans[0].Id, "purple waffles"));
        Assert.Empty(h.Engine.GetTranscript());
        Assert.Null(await h.Engine.SubmitQuickFireAsync(h.Humans[1].Id, "grumpy toast"));

        await h.ToAsync(Stage.Discussion, 1);
        await h.UntilAsync(() => h.Engine.GetTranscript().Count == 3, "all three answers in the chat");
        Assert.Contains(h.Engine.GetTranscript(), m => m.PlayerId == h.BotId);
        Assert.Contains(h.Engine.GetTranscript(), m => m.Text == "purple waffles");
    }

    [Fact]
    public async Task The_hot_seat_is_the_most_suspected_player()
    {
        Harness h = new(humans: 3, o => { o.MaxRounds = 3; o.QuickFireSeconds = 10; });
        Player a = h.Humans[0], b = h.Humans[1], c = h.Humans[2];

        Assert.Null(h.Engine.TutorialDone());
        await h.ToAsync(Stage.Onboarding);
        foreach (Player human in h.Humans) Assert.Null(await h.Engine.SubmitOnboardingAsync(human.Id, h.Answers()));
        await h.ToAsync(Stage.Voting, 1);
        await h.VoteAsync(a, c.Id);
        await h.VoteAsync(b, c.Id);
        await h.VoteAsync(c, a.Id);
        await h.ToAsync(Stage.Discussion, 2);

        RoundData data = (RoundData)h.Room.State.Data!;
        Assert.Equal(RoundKind.HotSeat, data.Kind);
        Assert.Equal(c.Id, data.HotSeat!.Value.Id);
    }

    [Fact]
    public async Task The_bots_win_when_the_final_vote_is_tied_and_the_meter_cannot_settle_it()
    {
        Harness h = new(humans: 3, o => o.MaxRounds = 1);
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
    public async Task Each_round_is_worth_double_and_goes_to_the_side_that_gained_from_the_vote()
    {
        Harness h = new(humans: 3, o => o.MaxRounds = 3);
        Player a = h.Humans[0], b = h.Humans[1], c = h.Humans[2];

        await h.ThroughOnboardingAsync();
        await h.ToAsync(Stage.Voting, 1);

        // Round 1: a human is the top suspect, so the AI team earns 100.
        await h.VoteAsync(a, c.Id);
        await h.VoteAsync(b, c.Id);
        await h.VoteAsync(c, a.Id);
        await h.ToAsync(Stage.Voting, 2);
        Assert.Equal(new TeamTotals(0, 100), h.Engine.RoundPointTotals);

        // Round 2: a tie earns nobody anything.
        await h.VoteAsync(a, b.Id);
        await h.VoteAsync(b, a.Id);
        await h.ToAsync(Stage.Voting, 3);
        Assert.Equal(new TeamTotals(0, 100), h.Engine.RoundPointTotals);

        // Round 3: the bot is voted out, so the humans earn 400.
        await h.VoteAsync(a, h.BotId);
        await h.VoteAsync(b, h.BotId);
        await h.ToAsync(Stage.Results);
        Assert.Equal(new TeamTotals(400, 100), h.Engine.RoundPointTotals);
        Assert.Equal("Humans", h.Results().Winner);
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
