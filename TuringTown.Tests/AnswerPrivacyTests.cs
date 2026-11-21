using TuringTown.Models;
using TuringTown.Services.Ai;
using TuringTown.Services.Games;

namespace TuringTown.Tests;

/// <summary>
/// Onboarding answers are private for the whole game: no client ever sees another player's answers, and the bots
/// only ever talk about their own. Also the scripted bot's behaviour: answers that blend in without copying, chat
/// that does not repeat itself, votes that follow the room.
/// </summary>
public class AnswerPrivacyTests
{
    private static readonly ContentPolicy Family = new(true);

    // ---------------------------------------------------------------- privacy

    [Fact]
    public async Task Answers_are_never_broadcast_during_the_game()
    {
        Harness h = new(humans: 2);
        Assert.Null(h.Engine.TutorialDone());
        await h.ToAsync(Stage.Onboarding);

        Assert.Null(await h.Engine.SubmitOnboardingAsync(h.Humans[0].Id, h.Answers("I love pasta")));
        Assert.Null(await h.Engine.SubmitOnboardingAsync(h.Humans[1].Id, h.Answers("tacos forever")));
        string botAnswer = await h.BotTextAnswerAsync();

        // Through the Discussion and into the vote: no state, roster or chat carries anyone's answer.
        lock (h.States)
        {
            Assert.Contains(h.States, s => s.Stage == Stage.Discussion);
            Assert.All(h.States.Where(s => s.Stage == Stage.Discussion), s => Assert.Null(s.Data));
        }
        lock (h.Messages)
        {
            Assert.DoesNotContain(h.Messages, m => m.Json.Contains("I love pasta"));
            Assert.DoesNotContain(h.Messages, m => m.Json.Contains("tacos forever"));
            Assert.DoesNotContain(h.Messages, m => m.Json.Contains(botAnswer));
        }
    }

    [Fact]
    public async Task A_bot_is_never_handed_another_players_answers()
    {
        Harness h = new(humans: 2);
        Assert.Null(h.Engine.TutorialDone());
        await h.ToAsync(Stage.Onboarding);
        Assert.Null(await h.Engine.SubmitOnboardingAsync(h.Humans[0].Id, h.Answers("I love pasta")));
        Assert.Null(await h.Engine.SubmitOnboardingAsync(h.Humans[1].Id, h.Answers("tacos forever")));
        await h.BotTextAnswerAsync();

        // During the game the bot's context holds only its own persona: no human's answer, by alias or otherwise.
        VoteRequest request;
        lock (h.Agent.VoteRequests) request = h.Agent.VoteRequests.First();
        string text = System.Text.Json.JsonSerializer.Serialize(request.Context);
        Assert.DoesNotContain("I love pasta", text);
        Assert.DoesNotContain("tacos forever", text);
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
        string botAnswer = await h.BotTextAnswerAsync();
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
    public async Task Scripted_chat_brings_up_its_own_answers()
    {
        ScriptedBotAgent agent = new();
        BotContext ctx = TestContext.For("Red Apple") with
        {
            AlivePlayers = ["Blue Kiwi"],
            Persona = [new PersonaAnswer("Pineapple on pizza is...", QuestionKind.SingleChoice, null, ["A crime"])]
        };

        bool mentioned = false;
        for (int i = 0; i < 300 && !mentioned; i++)
        {
            string? line = await agent.NextChatMessageAsync(new ChatRequest("Red Apple", "", Family, 1, [], [], ["Blue Kiwi"], ctx), default);
            mentioned = line?.Contains("a crime", StringComparison.OrdinalIgnoreCase) == true;
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

    [Fact]
    public async Task A_scripted_bot_answers_a_matching_question_with_its_own_answer()
    {
        ScriptedBotAgent agent = new();
        BotContext ctx = TestContext.For("Red Apple") with
        {
            AlivePlayers = ["Blue Kiwi"],
            Persona = [new PersonaAnswer("What snack could you eat forever?", QuestionKind.Text, "salty popcorn", [])]
        };
        ChatLine question = new("Blue Kiwi", "@Red Apple what snack did you pick?");
        string? reply = await agent.NextChatMessageAsync(
            new ChatRequest("Red Apple", "", Family, 1, [question], [question], ctx.AlivePlayers, ctx), default);

        Assert.NotNull(reply);
        Assert.True(reply.Contains("popcorn", StringComparison.OrdinalIgnoreCase)
                    || reply.Contains("salty", StringComparison.OrdinalIgnoreCase));
    }
}
