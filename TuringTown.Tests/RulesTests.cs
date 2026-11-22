using TuringTown.Models;
using TuringTown.Services.Ai;
using TuringTown.Services.Games;

namespace TuringTown.Tests;

/// <summary>
/// What players are and are not allowed to do.
/// </summary>
public class RulesTests
{
    [Fact]
    public async Task Chat_is_closed_outside_the_discussion()
    {
        Harness h = new(humans: 2);

        // Still in the tutorial.
        Assert.NotNull(await h.Engine.SendChatAsync(h.Humans[0].Id, "hello"));

        await h.ThroughOnboardingAsync();
        Assert.Null(await h.Engine.SendChatAsync(h.Humans[0].Id, "hello"));

        await h.ToAsync(Stage.Voting, 1);
        Assert.NotNull(await h.Engine.SendChatAsync(h.Humans[0].Id, "too late"));
    }

    [Fact]
    public async Task Chat_is_rate_limited_per_player()
    {
        Harness h = new(humans: 2);
        await h.ThroughOnboardingAsync();
        Guid a = h.Humans[0].Id, b = h.Humans[1].Id;

        Assert.Null(await h.Engine.SendChatAsync(a, "first"));
        Assert.NotNull(await h.Engine.SendChatAsync(a, "second, too fast"));
        // Someone else is not affected.
        Assert.Null(await h.Engine.SendChatAsync(b, "hi"));

        h.Time.Advance(TimeSpan.FromMilliseconds(h.Options.ChatMinIntervalMs));
        Assert.Null(await h.Engine.SendChatAsync(a, "second, after waiting"));
    }

    [Fact]
    public async Task Chat_that_leaks_who_you_are_is_rejected_and_never_broadcast()
    {
        Harness h = new(humans: 2);
        await h.ThroughOnboardingAsync();
        Guid a = h.Humans[0].Id;

        string[] leaks = ["my name is Sam", "mail me sam@example.com", "call 555 123 4567", "see www.example.com"];
        foreach (string leak in leaks)
        {
            Assert.NotNull(await h.Engine.SendChatAsync(a, leak));
            h.Time.Advance(TimeSpan.FromSeconds(2));
        }

        Assert.DoesNotContain(h.Messages, m => m.Kind == "chat");
    }

    [Fact]
    public async Task A_chat_line_shows_the_senders_alias_and_nothing_that_identifies_them()
    {
        Harness h = new(humans: 2);
        await h.ThroughOnboardingAsync();
        Player a = h.Humans[0];

        Assert.Null(await h.Engine.SendChatAsync(a.Id, "  hello    there  "));

        ChatMessage line = Assert.Single(h.Engine.GetTranscript());
        Assert.Equal("hello there", line.Text);
        Assert.Equal(a.Alias, line.Alias);
    }

    [Fact]
    public async Task Bots_chat_through_the_same_rules_and_show_up_like_players()
    {
        Harness h = new(humans: 2, o =>
        {
            o.BotMessagesPerRound = 2;
            o.ChatMaxLength = 20;
        });
        int calls = 0;
        h.Agent.Chat = _ => ++calls == 1 ? "my name is Bot" : calls == 2 ? "an ordinary line" : "this line is far longer than the limit allows";

        await h.ThroughOnboardingAsync();
        await h.UntilAsync(() => h.Engine.GetTranscript().Count >= 1, "a bot line");

        ChatMessage line = Assert.Single(h.Engine.GetTranscript());
        Assert.Equal("an ordinary line", line.Text);
        Assert.Contains(h.Room.GetRoster(), p => p.Id == line.PlayerId && p.Alias == line.Alias);
    }

    [Fact]
    public async Task Voting_rules_are_enforced()
    {
        Harness h = new(humans: 3);
        Guid a = h.Humans[0].Id, b = h.Humans[1].Id;

        // Not in the voting stage yet.
        Assert.NotNull(await h.Engine.CastVoteAsync(a, b));

        await h.ThroughOnboardingAsync();
        await h.ToAsync(Stage.Voting, 1);

        Assert.NotNull(await h.Engine.CastVoteAsync(a, a));
        Assert.NotNull(await h.Engine.CastVoteAsync(a, Guid.NewGuid()));
        // Bots cannot be impersonated: the hub only ever passes the caller's own player id.
        Assert.NotNull(await h.Engine.CastVoteAsync(h.BotId, b));
        Assert.Null(await h.Engine.CastVoteAsync(a, b));
    }

    [Fact]
    public async Task A_player_who_is_voted_out_can_only_watch()
    {
        Harness h = new(humans: 3);
        Player a = h.Humans[0], b = h.Humans[1], c = h.Humans[2];
        h.Agent.Vote = _ => a.Id;

        await h.ThroughOnboardingAsync();
        await h.ToAsync(Stage.Voting, 1);
        await h.VoteAsync(a, b.Id);
        await h.VoteAsync(b, a.Id);
        await h.VoteAsync(c, a.Id);
        await h.ToAsync(Stage.Discussion, 2);

        // Clients must be told who is out: a roster saying so is sent before the reveal.
        var sent = h.Messages.ToList();
        int reveal = sent.FindIndex(m => m.Kind == "state" && m.Json.Contains("\"Reveal\""));
        Assert.Contains(sent.Take(reveal), m => m.Kind == "roster" && m.Json.Contains(a.Id.ToString()) && m.Json.Contains("\"alive\":false"));

        Assert.False(h.Room.GetRoster().First(p => p.Id == a.Id).Alive);
        Assert.NotNull(await h.Engine.SendChatAsync(a.Id, "let me speak"));
        Assert.Null(await h.Engine.SendChatAsync(b.Id, "ok"));

        await h.ToAsync(Stage.Voting, 2);
        Assert.NotNull(await h.Engine.CastVoteAsync(a.Id, b.Id));
        // And they cannot be voted for again.
        Assert.NotNull(await h.Engine.CastVoteAsync(b.Id, a.Id));
    }

    [Fact]
    public async Task Onboarding_answers_are_checked_and_stay_private_during_the_form()
    {
        Harness h = new(humans: 2);
        Assert.Null(h.Engine.TutorialDone());
        await h.ToAsync(Stage.Onboarding);
        Guid a = h.Humans[0].Id;

        Assert.NotNull(await h.Engine.SubmitOnboardingAsync(a, [new OnboardingAnswer(h.Questions[0].Id, "only one", [])]));
        Assert.NotNull(await h.Engine.SubmitOnboardingAsync(a, h.Answers("email me at me@example.com")));
        Assert.Null(await h.Engine.SubmitOnboardingAsync(a, h.Answers("I love pasta")));

        Assert.DoesNotContain(h.Messages, m => m.Json.Contains("I love pasta"));
    }

    [Fact]
    public async Task The_form_closes_early_once_everyone_connected_has_answered()
    {
        Harness h = new(humans: 2);
        Assert.Null(h.Engine.TutorialDone());
        await h.ToAsync(Stage.Onboarding);

        DateTimeOffset before = h.Time.GetUtcNow();
        foreach (Player human in h.Humans)
            Assert.Null(await h.Engine.SubmitOnboardingAsync(human.Id, h.Answers("x")));
        await h.ToAsync(Stage.Discussion, 1);

        Assert.True((h.Time.GetUtcNow() - before).TotalSeconds < h.Options.OnboardingSeconds);
    }

    [Fact]
    public async Task Only_the_tutorial_stage_accepts_tutorial_calls()
    {
        Harness h = new(humans: 2);
        Assert.Null(await h.Engine.TutorialStepAsync(2));
        Assert.Equal(2, h.Room.State.Step);
        Assert.NotNull(await h.Engine.TutorialStepAsync(-1));

        await h.ThroughOnboardingAsync();
        Assert.NotNull(await h.Engine.TutorialStepAsync(3));
        Assert.NotNull(h.Engine.TutorialDone());
        Assert.NotNull(h.Engine.EndResults());
    }

    // ---------------------------------------------------------------- @mentions

    [Fact]
    public async Task A_mention_is_an_at_sign_and_an_exact_alias_and_nothing_else_counts()
    {
        Harness h = new(humans: 2);
        await h.ThroughOnboardingAsync();
        Player a = h.Humans[0], b = h.Humans[1];
        PlayerSeat bot = h.Room.GetSeats().First(s => s.IsBot);

        // B by exact alias, the bot in lower case, plus yourself, a stranger and a name with no "@".
        string text = $"hey @{b.Alias} and @{bot.Alias.ToLowerInvariant()}, not @{a.Alias} or @Nobody Real or {b.Alias}";
        Assert.Null(await h.Engine.SendChatAsync(a.Id, text));

        ChatMessage message = Assert.Single(h.Engine.GetTranscript());
        Assert.Equal([b.Id, bot.Id], message.Mentions.OrderBy(id => id == bot.Id));
    }

    [Fact]
    public async Task A_message_without_mentions_has_an_empty_list()
    {
        Harness h = new(humans: 2);
        await h.ThroughOnboardingAsync();

        Assert.Null(await h.Engine.SendChatAsync(h.Humans[0].Id, $"I wonder who {h.Humans[1].Alias} is"));

        Assert.Empty(Assert.Single(h.Engine.GetTranscript()).Mentions);
    }

    [Fact]
    public async Task Only_a_few_players_can_be_mentioned_in_one_message()
    {
        Harness h = new(humans: 4);
        await h.ThroughOnboardingAsync();

        string everyone = string.Join(" ", h.Room.GetSeats().Where(s => s.Id != h.Humans[0].Id).Select(s => $"@{s.Alias}"));
        Assert.Null(await h.Engine.SendChatAsync(h.Humans[0].Id, everyone));

        Assert.Equal(h.Options.MaxMentionsPerMessage, Assert.Single(h.Engine.GetTranscript()).Mentions.Count);
    }

    [Fact]
    public async Task A_bot_that_is_mentioned_answers_soon_without_waiting_for_its_turn()
    {
        // Its normal turn is 10 minutes away, so only the mention can make it speak.
        Harness h = new(humans: 2, o =>
        {
            o.BotMessagesPerRound = 2;
            o.BotChatMinDelayMs = 600_000;
            o.BotChatMaxDelayMs = 600_000;
        });
        ChatRequest? seen = null;
        h.Agent.Chat = request =>
        {
            if (request.Mentions.Count == 0) return null;
            seen = request;
            return $"@{request.Mentions[0].Alias} I hear you";
        };

        await h.ThroughOnboardingAsync();
        Player a = h.Humans[0];
        PlayerSeat bot = h.Room.GetSeats().First(s => s.IsBot);

        Assert.Null(await h.Engine.SendChatAsync(a.Id, $"@{bot.Alias} are you real?"));
        await h.UntilAsync(() => h.Engine.GetTranscript().Count >= 2, "the bot's answer");

        ChatMessage answer = h.Engine.GetTranscript()[1];
        Assert.Equal(bot.Id, answer.PlayerId);
        Assert.Equal([a.Id], answer.Mentions);

        // What the bot was shown: the line that mentioned it, and everyone else by alias only (no roles).
        Assert.NotNull(seen);
        Assert.Equal(a.Alias, seen.Mentions[0].Alias);
        Assert.Equal(h.Humans.Select(p => p.Alias).Order(), seen.OtherPlayers.Order());
        Assert.DoesNotContain(bot.Alias, seen.OtherPlayers);
    }

    // ---------------------------------------------------------------- chat filter

    [Theory]
    [InlineData("hello there", ChatCheck.Ok)]
    [InlineData("what names do you like?", ChatCheck.Ok)]
    [InlineData("@Red Apple what do you think?", ChatCheck.Ok)]
    [InlineData("I have 3 cats and 2 dogs", ChatCheck.Ok)]
    [InlineData("", ChatCheck.Empty)]
    [InlineData("   ", ChatCheck.Empty)]
    [InlineData("my real name is Alex", ChatCheck.IntroducesName)]
    [InlineData("call me Dave", ChatCheck.IntroducesName)]
    [InlineData("I'm called Pat", ChatCheck.IntroducesName)]
    [InlineData("write to jo.bloggs+game@mail.co.uk", ChatCheck.ContactInfo)]
    [InlineData("my number is 07700 900 123", ChatCheck.ContactInfo)]
    [InlineData("visit https://example.com/me", ChatCheck.ContactInfo)]
    [InlineData("find me on example.gg", ChatCheck.ContactInfo)]
    public void The_chat_filter_catches_the_obvious_leaks(string text, ChatCheck expected) =>
        Assert.Equal(expected, ChatFilter.Check(text, 200, out _));

    [Fact]
    public void The_chat_filter_enforces_the_length_limit_and_tidies_whitespace()
    {
        Assert.Equal(ChatCheck.TooLong, ChatFilter.Check(new string('a', 11), 10, out _));
        Assert.Equal(ChatCheck.Ok, ChatFilter.Check("a\tb\n c", 10, out string cleaned));
        Assert.Equal("ab c", cleaned);
    }

    // ---------------------------------------------------------------- onboarding questions

    [Fact]
    public void Questions_must_not_ask_for_personal_details()
    {
        ContentPolicy policy = new(FamilyFriendly: true);
        OnboardingQuestion Q(string id, string text, bool mature = false) => new(id, text, mature);

        Assert.True(QuestionValidator.IsValid([Q("a", "What is your favourite snack?"), Q("b", "Pick a superpower.")], policy, 2));
        Assert.False(QuestionValidator.IsValid([Q("a", "What is your name?")], policy, 1));
        Assert.False(QuestionValidator.IsValid([Q("a", "How old are you?")], policy, 1));
        Assert.False(QuestionValidator.IsValid([Q("a", "Where do you live?")], policy, 1));
        Assert.False(QuestionValidator.IsValid([Q("a", "What is your email?")], policy, 1));
    }

    [Fact]
    public void Questions_must_match_the_expected_shape_and_the_content_policy()
    {
        OnboardingQuestion Q(string id, string text, bool mature = false) => new(id, text, mature);
        ContentPolicy family = new(true), mature = new(false);

        Assert.False(QuestionValidator.IsValid(null, family, 1));
        Assert.False(QuestionValidator.IsValid([Q("a", "Fine?")], family, 2));
        Assert.False(QuestionValidator.IsValid([Q("a", "Same id?"), Q("a", "Same id again?")], family, 2));
        Assert.False(QuestionValidator.IsValid([Q("a", "")], family, 1));
        Assert.False(QuestionValidator.IsValid([Q("a", new string('x', QuestionValidator.MaxTextLength + 1))], family, 1));

        // A family room never accepts a question flagged mature; a mature room may.
        Assert.False(QuestionValidator.IsValid([Q("a", "Spicy one?", mature: true)], family, 1));
        Assert.True(QuestionValidator.IsValid([Q("a", "Spicy one?", mature: true)], mature, 1));
    }

    [Fact]
    public void The_built_in_questions_always_pass_validation()
    {
        foreach (bool familyFriendly in new[] { true, false })
        {
            ContentPolicy policy = new(familyFriendly);
            for (int i = 0; i < 25; i++)
            {
                IReadOnlyList<OnboardingQuestion> questions = ScriptedBotAgent.BuiltInQuestions(policy, new QuestionMix(1, 1, 1));
                Assert.True(QuestionValidator.IsValid(questions, policy, new QuestionMix(1, 1, 1)));
            }
        }
    }

    [Fact]
    public async Task Bad_questions_from_the_agent_are_replaced_by_built_in_ones()
    {
        // Three text questions: one asks for a name, and the mix is wrong as well. Either way the built-in set is used.
        Harness h = new(humans: 2, questions: new TestQuestionGenerator(
            [new OnboardingQuestion("q1", "What is your name?", false), new("q2", "x", false), new("q3", "y", false)]));

        Assert.Null(h.Engine.TutorialDone());
        await h.ToAsync(Stage.Onboarding);

        OnboardingData data = (OnboardingData)h.Room.State.Data!;
        Assert.DoesNotContain(data.Questions, q => q.Text.Contains("name"));
        Assert.True(QuestionValidator.IsValid(data.Questions.ToArray(), new ContentPolicy(true), h.Options.Mix));
    }
}
