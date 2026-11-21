using TuringTown.Models;
using TuringTown.Services.Ai;
using TuringTown.Services.Games;

namespace TuringTown.Tests;

/// <summary>
/// The public answer board (everyone's onboarding answers, shown from the first Discussion on) and the scripted bot's
/// behaviour around it: answers that blend in without copying, chat that does not repeat itself, votes that follow the room.
/// </summary>
public class AnswerBoardTests
{
    private static readonly ContentPolicy Family = new(true);

    // ---------------------------------------------------------------- the board

    [Fact]
    public async Task The_board_is_private_during_the_form_and_public_from_the_discussion()
    {
        Harness h = new(humans: 2);
        Assert.Null(h.Engine.TutorialDone());
        await h.ToAsync(Stage.Onboarding);

        Assert.Null(await h.Engine.SubmitOnboardingAsync(h.Humans[0].Id, h.Answers("I love pasta")));
        // Not yet: nothing broadcast during the form carries an answer.
        Assert.DoesNotContain(h.Messages, m => m.Json.Contains("I love pasta"));

        Assert.Null(await h.Engine.SubmitOnboardingAsync(h.Humans[1].Id, h.Answers("tacos forever")));
        await h.ToAsync(Stage.Discussion, 1);

        Assert.DoesNotContain(h.Messages, m => m.StageAtSend == Stage.Onboarding && m.Json.Contains("I love pasta"));
        DiscussionData data = (DiscussionData)h.Room.State.Data!;
        BoardQuestion text = data.Board.Single(q => q.Kind == QuestionKind.Text);
        Assert.Equal("I love pasta", text.Answers.Single(a => a.PlayerId == h.Humans[0].Id).Answer);
        Assert.Equal("tacos forever", text.Answers.Single(a => a.PlayerId == h.Humans[1].Id).Answer);
    }

    [Fact]
    public async Task The_board_holds_every_seat_sorted_by_alias_with_no_role()
    {
        Harness h = new(humans: 5);
        await h.ThroughOnboardingAsync();

        DiscussionData data = (DiscussionData)h.Room.State.Data!;
        string[] seats = h.Room.GetSeats().Select(s => s.Alias).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(h.Options.Mix.Total, data.Board.Count);
        foreach (BoardQuestion q in data.Board)
        {
            // Humans and bots alike, in alias order, so neither the order nor a gap gives a bot away.
            Assert.Equal(seats, q.Answers.Select(a => a.Alias).ToArray());
            // A bot always has an answer: the scripted fallback fills any gap.
            foreach (Guid bot in h.BotIds)
                Assert.NotEqual("", q.Answers.Single(a => a.PlayerId == bot).Answer);
        }

        // A choice answer is written as the option texts, the same way for everyone.
        BoardQuestion single = data.Board.First(q => q.Kind == QuestionKind.SingleChoice);
        Assert.All(single.Answers, a => Assert.DoesNotContain(",", a.Answer));

        // The same board in every round.
        await h.ToAsync(Stage.Voting, 1);
        foreach (Player human in h.Humans.Take(3)) await h.VoteAsync(human, h.Humans[4].Id);
        await h.ToAsync(Stage.Discussion, 2);
        Assert.Equal(data.Board.Count, ((DiscussionData)h.Room.State.Data!).Board.Count);
    }

    [Fact]
    public async Task A_bot_answer_that_copies_a_human_is_replaced()
    {
        Harness h = new(humans: 2);
        // The bot parrots the first human's text answer word for word.
        h.Agent.Answer = r => r.Questions
            .Select(q => q.Kind == QuestionKind.Text
                ? new OnboardingAnswer(q.Id, r.HumanAnswers.First(a => a.QuestionId == q.Id).Text, [])
                : ScriptedBotAgent.ScriptedAnswer(q, r.HumanAnswers))
            .ToArray();

        Assert.Null(h.Engine.TutorialDone());
        await h.ToAsync(Stage.Onboarding);
        Assert.Null(await h.Engine.SubmitOnboardingAsync(h.Humans[0].Id, h.Answers("Cold pizza is the best breakfast")));
        Assert.Null(await h.Engine.SubmitOnboardingAsync(h.Humans[1].Id, h.Answers("sleeping in")));
        await h.ToAsync(Stage.Discussion, 1);

        BoardQuestion text = ((DiscussionData)h.Room.State.Data!).Board.Single(q => q.Kind == QuestionKind.Text);
        string botAnswer = text.Answers.Single(a => a.PlayerId == h.BotId).Answer;
        Assert.NotEqual("", botAnswer);
        Assert.False(OnboardingAnswerValidator.IsNearCopy(botAnswer, ["Cold pizza is the best breakfast"]));
    }

    [Theory]
    [InlineData("Cold pizza is the best", "cold pizza is the best!!", true)]
    [InlineData("cold pizza is the best breakfast", "Cold pizza is the best", true)]
    [InlineData("pizza", "Pizza.", true)]
    [InlineData("pizza", "pizza and tacos", false)]
    [InlineData("tacos are underrated honestly", "Cold pizza is the best", false)]
    [InlineData("", "anything", false)]
    public void Near_copies_are_spotted(string bot, string human, bool copy) =>
        Assert.Equal(copy, OnboardingAnswerValidator.IsNearCopy(bot, [human]));

    // ---------------------------------------------------------------- the scripted bot

    [Fact]
    public void A_scripted_text_answer_never_copies_a_human()
    {
        IReadOnlyList<OnboardingQuestion> form = ScriptedBotAgent.BuiltInQuestions(Family, new QuestionMix(1, 0, 0));
        OnboardingQuestion q = form[0];

        for (int i = 0; i < 50; i++)
        {
            // A human who happened to write one of the bot's own lines.
            OnboardingAnswer first = ScriptedBotAgent.ScriptedAnswer(q, [], Guid.NewGuid());
            OnboardingAnswer second = ScriptedBotAgent.ScriptedAnswer(q, [first], Guid.NewGuid());
            Assert.False(OnboardingAnswerValidator.IsNearCopy(second.Text, [first.Text]));
        }
    }

    [Fact]
    public async Task Scripted_chat_does_not_repeat_its_own_recent_lines()
    {
        ScriptedBotAgent agent = new();
        BotContext ctx = TestContext.For("Red Apple") with { AlivePlayers = ["Blue Kiwi", "Green Pear"] };
        List<string> said = [];

        for (int i = 0; i < 60; i++)
        {
            BotContext now = ctx with { OwnLines = said.TakeLast(6).Select(t => new ChatLine("Red Apple", t)).ToArray() };
            string? line = await agent.NextChatMessageAsync(
                new ChatRequest("Red Apple", "", Family, 1, [], [], now.AlivePlayers, now), default);
            if (line is null) continue;

            Assert.DoesNotContain(line, said.TakeLast(6));
            said.Add(line);
        }

        // And it does not sound like one fixed script.
        Assert.True(said.Distinct().Count() >= 20, $"only {said.Distinct().Count()} different lines");
    }

    [Fact]
    public async Task Scripted_chat_brings_up_the_answer_board()
    {
        ScriptedBotAgent agent = new();
        BoardQuestion q = new("Pineapple on pizza is...", QuestionKind.SingleChoice,
            [new BoardAnswer(Guid.NewGuid(), "Blue Kiwi", "Genius"), new BoardAnswer(Guid.NewGuid(), "Red Apple", "A crime")]);
        BotContext ctx = TestContext.For("Red Apple") with { AlivePlayers = ["Blue Kiwi"], Board = [q] };

        bool mentioned = false;
        for (int i = 0; i < 200 && !mentioned; i++)
        {
            string? line = await agent.NextChatMessageAsync(new ChatRequest("Red Apple", "", Family, 1, [], [], ["Blue Kiwi"], ctx), default);
            mentioned = line?.Contains("genius", StringComparison.OrdinalIgnoreCase) == true;
        }
        Assert.True(mentioned);
    }

    [Fact]
    public async Task Scripted_votes_follow_the_room()
    {
        ScriptedBotAgent agent = new();
        Guid suspect = Guid.NewGuid();
        VoteCandidate[] candidates = [new(suspect, "Blue Kiwi"), new(Guid.NewGuid(), "Green Pear"), new(Guid.NewGuid(), "Gold Fig")];
        ChatLine[] chat =
        [
            new("Green Pear", "@Blue Kiwi you are so sus"),
            new("Gold Fig", "yeah @Blue Kiwi is the bot"),
            new("Green Pear", "vote @Blue Kiwi"),
        ];

        int hits = 0;
        for (int i = 0; i < 300; i++)
        {
            Guid? vote = await agent.CastVoteAsync(new VoteRequest("Red Apple", "", Family, 1, candidates, chat, TestContext.For("Red Apple")), default);
            if (vote == suspect) hits++;
        }

        // Uniform would be about a third. The accused player is the clear favourite, but not a certainty.
        Assert.InRange(hits, 180, 299);
    }

    [Fact]
    public async Task A_scripted_bot_that_is_accused_defends_itself()
    {
        ScriptedBotAgent agent = new();
        BotContext ctx = TestContext.For("Red Apple") with { AlivePlayers = ["Blue Kiwi", "Green Pear"] };
        ChatLine accusation = new("Blue Kiwi", "@Red Apple you are the bot");

        for (int i = 0; i < 30; i++)
        {
            string? line = await agent.NextChatMessageAsync(
                new ChatRequest("Red Apple", "", Family, 1, [accusation], [accusation], ctx.AlivePlayers, ctx), default);
            // Spoken to, it always answers.
            Assert.False(string.IsNullOrWhiteSpace(line));
        }
    }
}
