using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TuringTown.Models;
using TuringTown.Services.Ai;
using TuringTown.Services.Games;
using TuringTown.Services.Storage;

namespace TuringTown.Tests;

/// <summary>
/// The model-backed bot and question generator, driven by a fake model: prompts, settings, structured output, parsing,
/// the output guard, and bot memory.
/// </summary>
public class FoundryAgentTests
{
    // ---------------------------------------------------------------- templates

    [Fact]
    public void Every_prompt_template_loads_and_has_a_version()
    {
        BotPrompts prompts = new();
        string[] names = ["bot-system", "chat-task", "vote-task", "onboarding-task", "round-notes-task", "questions-system", "questions-task", "tone-family", "tone-mature",
            "planner-system", "planner-task", "witty-prompts-system", "witty-prompts-task", "witty-system", "witty-answer-task", "witty-vote-task",
            "small-talk"];

        foreach (string name in names)
            Assert.True(prompts.Get(name).Version >= 1, name);
        Assert.Equal(names.Order(), prompts.All.Select(t => t.Name).Order());
    }

    [Fact]
    public void Rendering_needs_every_placeholder_and_uses_every_value()
    {
        BotPrompts prompts = new();

        Assert.Throws<InvalidOperationException>(() => prompts.Render("tone-family", new Dictionary<string, string> { ["extra"] = "x" }));
        Assert.Throws<InvalidOperationException>(() => prompts.Render("vote-task", new Dictionary<string, string> { ["alias"] = "x" }));
        Assert.Throws<InvalidOperationException>(() => prompts.Get("nope"));
    }

    [Fact]
    public void The_tone_follows_the_family_friendly_setting()
    {
        BotPrompts prompts = new();
        Assert.Contains("TONE: family", prompts.Tone(AiKit.Family));
        Assert.Contains("TONE: mature", prompts.Tone(AiKit.Mature));
        Assert.Contains("safety rules above still apply", prompts.Tone(AiKit.Mature));
    }

    // ---------------------------------------------------------------- the system prompt

    [Fact]
    public async Task The_system_prompt_carries_the_play_alone_rule_and_a_canary_and_is_the_same_for_every_call()
    {
        FakeLlmClient llm = new();
        FoundryBotAgent agent = AiKit.Agent(llm);
        BotContext ctx = AiKit.Ctx();

        await agent.NextChatMessageAsync(AiKit.Chat(ctx), default);
        await agent.CastVoteAsync(AiKit.Vote(ctx), default);
        await agent.EndRoundAsync(new RoundEndRequest(ctx, AiKit.Family, []), default);

        string system = llm.Of("Chat").Single().System;
        Assert.Equal(system, llm.Of("Vote").Single().System);
        Assert.Equal(system, llm.Of("RoundNotes").Single().System);

        Assert.Contains("You have no partner and no team in this room", system);
        Assert.Contains("SMALL TALK", system);
        Assert.Contains("TONE: family", system);
        Assert.Contains(FoundryBotAgent.Canary(ctx.Key.GameId), system);
        // Nothing in it is specific to this bot, so the model can cache it.
        Assert.DoesNotContain(ctx.BotAlias, system);
    }

    [Fact]
    public async Task A_bots_personality_is_consistent_across_onboarding_chat_and_voting()
    {
        FakeLlmClient llm = new();
        FoundryBotAgent agent = AiKit.Agent(llm);
        BotContext ctx = AiKit.Ctx();
        string personality = $"<personality>{PromptJson.Serialize(BotPersonality.For(ctx.Key).Description)}</personality>";

        await agent.AnswerOnboardingAsync(AnswerRequest(ctx), default);
        await agent.NextChatMessageAsync(AiKit.Chat(ctx), default);
        await agent.CastVoteAsync(AiKit.Vote(ctx), default);
        await agent.NextChatMessageAsync(AiKit.Chat(ctx with { Round = 2 }), default);

        foreach (LlmCall call in llm.Calls)
        {
            Assert.Contains(personality, call.User);
            Assert.DoesNotContain(ctx.Key.BotId.ToString(), call.User);
        }
    }

    [Fact]
    public void Bot_personalities_are_stable_and_varied_and_drive_scripted_openers()
    {
        Guid game = Guid.Parse("10000000-0000-0000-0000-000000000000");
        BotContext[] bots = Enumerable.Range(1, 100)
            .Select(i => AiKit.Ctx(game: game, bot: new Guid(i, 0, 0, new byte[8])))
            .ToArray();
        Assert.True(bots.Select(b => BotPersonality.For(b.Key).Description).Distinct().Count() >= 6);

        BotContext first = bots[0];
        BotContext second = bots.First(b => BotPersonality.For(b.Key) != BotPersonality.For(first.Key));
        foreach (BotContext ctx in new[] { first, second })
        {
            BotPersonality personality = BotPersonality.For(ctx.Key);
            Assert.Same(personality, BotPersonality.For(ctx.Key));
            (string? mention, string? body) = ScriptedBotAgent.FreeLine(
                AiKit.Chat(ctx, recent: []), BotVoice.For(ctx.Key.BotId, ctx.Style), new FixedRandom());
            Assert.Null(mention);
            Assert.Contains(body, personality.Openers);
        }
        Assert.NotEqual(BotPersonality.For(first.Key).Openers[0], BotPersonality.For(second.Key).Openers[0]);
    }

    private sealed class FixedRandom : Random
    {
        public override int Next(int maxValue) => maxValue == 100 ? 42 : 0;
    }

    [Fact]
    public async Task No_bot_prompt_tells_a_bot_how_many_bots_there_are_or_that_it_has_a_team()
    {
        FakeLlmClient llm = new();
        FoundryBotAgent agent = AiKit.Agent(llm);
        BotContext ctx = AiKit.Ctx();

        await agent.NextChatMessageAsync(AiKit.Chat(ctx), default);
        await agent.CastVoteAsync(AiKit.Vote(ctx), default);
        await agent.EndRoundAsync(new RoundEndRequest(ctx, AiKit.Family, []), default);

        foreach (LlmCall call in llm.Calls)
        {
            string text = call.System + "\n" + call.User;
            Assert.DoesNotMatch(@"(?i)\bAI player\(s\)|other AI|AI team|your team|teammate|one team", text);
        }
        foreach (string name in new[] { "bot-system", "chat-task", "vote-task", "round-notes-task", "onboarding-task", "witty-system", "witty-answer-task", "witty-vote-task" })
            Assert.DoesNotContain("{{bot_count}}", new BotPrompts().Get(name).Text);
    }

    [Fact]
    public void Every_opening_line_gets_a_small_talk_idea_and_later_lines_only_sometimes()
    {
        BotContext ctx = AiKit.Ctx();
        ChatLine[] quiet = [new("Blue Kiwi", "hi")];
        ChatLine[] busy = [new("Blue Kiwi", "a"), new("Green Pear", "b"), new("Blue Kiwi", "c")];

        Assert.Contains("<idea>", FoundryBotAgent.SmallTalkIdea(AiKit.Chat(ctx, recent: []), "x", 99));
        Assert.Contains("<idea>", FoundryBotAgent.SmallTalkIdea(AiKit.Chat(ctx, recent: quiet), "x", 99));
        // Spoken to while the chat is just starting: still small talk is welcome.
        Assert.Contains("<idea>", FoundryBotAgent.SmallTalkIdea(AiKit.Chat(ctx, recent: quiet, mentions: [quiet[0]]), "x", 99));

        Assert.Equal("", FoundryBotAgent.SmallTalkIdea(AiKit.Chat(ctx, recent: busy), "x", FoundryBotAgent.SmallTalkPercent));
        Assert.Contains("<idea>", FoundryBotAgent.SmallTalkIdea(AiKit.Chat(ctx, recent: busy), "x", FoundryBotAgent.SmallTalkPercent - 1));
        Assert.Equal("", FoundryBotAgent.SmallTalkIdea(AiKit.Chat(ctx, recent: busy, mentions: [busy[0]]), "x", 0));
    }

    [Fact]
    public void Small_talk_ideas_are_many_and_the_pick_is_random()
    {
        BotPrompts prompts = new();
        Assert.True(prompts.SmallTalkIdeas.Count >= 15);
        HashSet<string> seen = [.. Enumerable.Range(0, 200).Select(_ => prompts.RandomSmallTalk())];
        Assert.True(seen.Count >= 10);
    }

    [Fact]
    public async Task The_opening_chat_prompt_carries_the_idea_as_quoted_data()
    {
        FakeLlmClient llm = new();
        BotContext ctx = AiKit.Ctx();
        await AiKit.Agent(llm).NextChatMessageAsync(AiKit.Chat(ctx, recent: []), default);

        string user = llm.Of("Chat").Single().User;
        Assert.Matches("<idea>\"[^\"]+\"</idea>", user);
    }

    [Fact]
    public void The_canary_differs_per_game_and_is_stable_within_one()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        Assert.Equal(FoundryBotAgent.Canary(a), FoundryBotAgent.Canary(a));
        Assert.NotEqual(FoundryBotAgent.Canary(a), FoundryBotAgent.Canary(b));
    }

    [Fact]
    public async Task A_mature_room_gets_the_mature_tone()
    {
        FakeLlmClient llm = new();
        BotContext ctx = AiKit.Ctx();
        await AiKit.Agent(llm).NextChatMessageAsync(AiKit.Chat(ctx, policy: AiKit.Mature), default);

        Assert.Contains("TONE: mature", llm.Of("Chat").Single().System);
    }

    // ---------------------------------------------------------------- chat

    [Fact]
    public async Task A_chat_call_uses_the_configured_effort_limits_and_timeout_and_asks_for_plain_text()
    {
        FakeLlmClient llm = new();
        BotContext ctx = AiKit.Ctx();
        FoundryBotAgent agent = AiKit.Agent(llm, configure: o => o.Calls.Chat = new AiCallOptions { ReasoningEffort = "high", MaxOutputTokens = 77, TimeoutMs = 1234 });

        await agent.NextChatMessageAsync(AiKit.Chat(ctx), default);

        LlmCall call = llm.Of("Chat").Single();
        Assert.Null(call.Schema);
        Assert.Equal("high", call.ReasoningEffort);
        Assert.Equal(77, call.MaxOutputTokens);
        Assert.Equal(TimeSpan.FromMilliseconds(1234), call.Timeout);
        Assert.Equal(ctx.Key.GameId.ToString("N"), call.Scope);
    }

    [Fact]
    public async Task The_chat_prompt_has_the_alias_the_persona_the_other_players_and_the_chat_as_quoted_data()
    {
        FakeLlmClient llm = new();
        BotContext ctx = AiKit.Ctx();
        await AiKit.Agent(llm).NextChatMessageAsync(AiKit.Chat(ctx), default);

        string user = llm.Of("Chat").Single().User;
        Assert.Contains("You are Red Apple", user);
        Assert.Contains("salty popcorn", user);
        Assert.Contains("[\"Blue Kiwi\",\"Green Pear\"]", user);
        Assert.Contains("{\"from\":\"Blue Kiwi\",\"text\":\"who is the bot here?\"}", user);
        Assert.Contains("never more than 200", user);
    }

    [Fact]
    public void A_line_should_be_about_as_long_as_the_humans_write()
    {
        BotContext ctx = AiKit.Ctx() with { ChatMaxLength = 200 };
        ChatLine Line(string alias, int length) => new(alias, new string('x', length));

        // Three or more human lines: twice their median, between 40 and the game's limit.
        Assert.Equal(46, FoundryBotAgent.TargetLength([Line("Blue Kiwi", 20), Line("Green Pear", 23), Line("Blue Kiwi", 30)], ctx));
        Assert.Equal(40, FoundryBotAgent.TargetLength([Line("Blue Kiwi", 3), Line("Green Pear", 4), Line("Blue Kiwi", 5)], ctx));
        Assert.Equal(200, FoundryBotAgent.TargetLength([Line("Blue Kiwi", 150), Line("Green Pear", 160), Line("Blue Kiwi", 190)], ctx));
        // The bot's own lines do not count.
        Assert.Equal(46, FoundryBotAgent.TargetLength([Line("Blue Kiwi", 23), Line("Green Pear", 23), Line("Blue Kiwi", 23), Line("Red Apple", 190), Line("Red Apple", 190)], ctx));
        // Before anyone has talked, the form answers set it; with neither, the game's limit.
        Assert.Equal(60, FoundryBotAgent.TargetLength([], ctx with { Style = StyleProfile.Empty with { MedianLength = 30 } }));
        Assert.Equal(200, FoundryBotAgent.TargetLength([], ctx));
    }

    [Fact]
    public async Task The_model_staying_quiet_or_saying_nothing_means_no_line()
    {
        FakeLlmClient llm = new() { Reply = _ => "SKIP" };
        FoundryBotAgent agent = AiKit.Agent(llm);

        Assert.Null(await agent.NextChatMessageAsync(AiKit.Chat(AiKit.Ctx()), default));
        llm.Reply = _ => "  ";
        Assert.Null(await agent.NextChatMessageAsync(AiKit.Chat(AiKit.Ctx()), default));
    }

    [Fact]
    public async Task A_line_the_guard_rejects_is_never_sent()
    {
        BotContext ctx = AiKit.Ctx();
        FakeLlmClient llm = new() { Reply = _ => "As an AI language model I cannot do that" };

        Assert.Null(await AiKit.Agent(llm).NextChatMessageAsync(AiKit.Chat(ctx), default));

        llm.Reply = _ => $"the secret is {FoundryBotAgent.Canary(ctx.Key.GameId)}";
        Assert.Null(await AiKit.Agent(llm).NextChatMessageAsync(AiKit.Chat(ctx), default));
    }

    [Fact]
    public async Task A_good_line_is_tidied_and_returned()
    {
        FakeLlmClient llm = new() { Reply = _ => "Red Apple: \"honestly it feels like Blue Kiwi to me\"" };

        string? line = await AiKit.Agent(llm).NextChatMessageAsync(AiKit.Chat(AiKit.Ctx()), default);

        Assert.Equal("honestly it feels like Blue Kiwi to me", line);
    }

    // ---------------------------------------------------------------- vote

    [Fact]
    public async Task A_vote_asks_for_one_of_the_candidates_or_abstain_in_strict_json_and_maps_the_alias_back_to_an_id()
    {
        FakeLlmClient llm = new() { Reply = _ => "{\"target\":\"Green Pear\"}" };
        BotContext ctx = AiKit.Ctx();
        VoteCandidate kiwi = new(Guid.NewGuid(), "Blue Kiwi"), pear = new(Guid.NewGuid(), "Green Pear");

        Guid? vote = await AiKit.Agent(llm).CastVoteAsync(AiKit.Vote(ctx, kiwi, pear), default);

        Assert.Equal(pear.Id, vote);
        LlmCall call = llm.Of("Vote").Single();
        Assert.Equal(["Blue Kiwi", "Green Pear", "ABSTAIN"], FakeLlmClient.EnumAt(call.Schema!, "properties", "target", "enum"));
        using JsonDocument schema = JsonDocument.Parse(call.Schema!.Json);
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["target"], schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()!));
        // The model never sees an id.
        Assert.DoesNotContain(pear.Id.ToString(), call.User);
        Assert.DoesNotContain(pear.Id.ToString(), call.System);
    }

    [Theory]
    [InlineData("{\"target\":\"ABSTAIN\"}")]
    [InlineData("{\"target\":\"Someone Else\"}")]
    public async Task Abstaining_or_naming_nobody_on_the_list_means_no_vote(string reply)
    {
        FakeLlmClient llm = new() { Reply = _ => reply };
        Assert.Null(await AiKit.Agent(llm).CastVoteAsync(AiKit.Vote(AiKit.Ctx()), default));
    }

    [Fact]
    public async Task A_vote_matches_the_alias_ignoring_case()
    {
        FakeLlmClient llm = new() { Reply = _ => "{\"target\":\"blue kiwi\"}" };
        VoteCandidate kiwi = new(Guid.NewGuid(), "Blue Kiwi");

        Assert.Equal(kiwi.Id, await AiKit.Agent(llm).CastVoteAsync(AiKit.Vote(AiKit.Ctx(), kiwi), default));
    }

    [Fact]
    public async Task A_reply_that_is_not_json_throws_so_the_fallback_answers()
    {
        FakeLlmClient llm = new() { Reply = _ => "I vote for Blue Kiwi" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => AiKit.Agent(llm).CastVoteAsync(AiKit.Vote(AiKit.Ctx()), default));
    }

    // ---------------------------------------------------------------- the onboarding form

    private static readonly OnboardingQuestion[] Form = [.. ScriptedBotAgent.BuiltInQuestions(AiKit.Family, new QuestionMix(1, 1, 1))];

    private static OnboardingAnswerRequest AnswerRequest(BotContext ctx) => new(ctx.BotAlias, AiKit.Family, Form, [], ctx);

    [Fact]
    public async Task The_onboarding_schema_only_allows_the_questions_and_options_on_this_form()
    {
        FakeLlmClient llm = new();
        await AiKit.Agent(llm).AnswerOnboardingAsync(AnswerRequest(AiKit.Ctx()), default);

        LlmCall call = llm.Of("Onboarding").Single();
        Assert.Equal(Form.Select(q => q.Id).Order(), FakeLlmClient.EnumAt(call.Schema!, "properties", "answers", "items", "properties", "questionId", "enum").Order());

        string[] allowed = FakeLlmClient.EnumAt(call.Schema!, "properties", "answers", "items", "properties", "optionIds", "items", "enum");
        Assert.Equal(Form.SelectMany(q => q.Options).Select(o => o.Id).Distinct().Order(), allowed.Order());
        // The question text and options are in the prompt, so the model can answer them.
        Assert.All(Form, q => Assert.Contains(q.Id, call.User));
    }

    [Fact]
    public async Task The_models_onboarding_answers_are_parsed_and_checked_by_the_engines_validator()
    {
        OnboardingQuestion text = Form.First(q => q.Kind == QuestionKind.Text), single = Form.First(q => q.Kind == QuestionKind.SingleChoice), multi = Form.First(q => q.Kind == QuestionKind.MultiChoice);
        FakeLlmClient llm = new()
        {
            Reply = _ => JsonSerializer.Serialize(new
            {
                answers = new object[]
                {
                    new { questionId = text.Id, text = "pizza obviously", optionIds = Array.Empty<string>() },
                    new { questionId = single.Id, text = "", optionIds = new[] { "a" } },
                    new { questionId = multi.Id, text = "", optionIds = new[] { "a", "b" } }
                }
            })
        };

        IReadOnlyList<OnboardingAnswer> answers = await AiKit.Agent(llm).AnswerOnboardingAsync(AnswerRequest(AiKit.Ctx()), default);

        Assert.Equal(3, answers.Count);
        Assert.Null(OnboardingAnswerValidator.Validate(Form, [.. answers], 300, out _));
    }

    [Fact]
    public async Task An_onboarding_answer_that_admits_to_being_an_ai_is_left_out_so_the_engine_replaces_it()
    {
        OnboardingQuestion text = Form.First(q => q.Kind == QuestionKind.Text);
        FakeLlmClient llm = new()
        {
            Reply = _ => JsonSerializer.Serialize(new { answers = new[] { new { questionId = text.Id, text = "As an AI language model I do not eat", optionIds = Array.Empty<string>() } } })
        };

        Assert.Empty(await AiKit.Agent(llm).AnswerOnboardingAsync(AnswerRequest(AiKit.Ctx()), default));
    }

    // ---------------------------------------------------------------- notes between rounds

    private static string Notes(string summary, params (string Alias, double Score)[] suspicions) => JsonSerializer.Serialize(new
    {
        summary,
        suspicions = suspicions.Select(s => new { alias = s.Alias, score = s.Score, why = "went quiet" }),
        claims = new[] { "I like popcorn" }
    });

    [Fact]
    public async Task The_end_of_round_notes_are_saved_cleaned_and_bounded_under_the_bots_own_key()
    {
        InMemoryBotMemoryStore store = new();
        FakeLlmClient llm = new() { Reply = _ => Notes("Blue Kiwi\u200B kept\naccusing me", ("Blue Kiwi", 3.5), ("Nobody Here", 0.9), ("Green Pear", -2)) };
        BotContext ctx = AiKit.Ctx();

        await AiKit.Agent(llm, store).EndRoundAsync(new RoundEndRequest(ctx, AiKit.Family, [new ChatLine("Blue Kiwi", "hi")]), default);

        BotMemory saved = (await store.LoadAsync(ctx.Key, default))!;
        Assert.Equal("Blue Kiwi kept accusing me", saved.Summary);
        // A player who is not in the game is dropped, and scores are kept between 0 and 1.
        Assert.Equal([("Blue Kiwi", 1.0), ("Green Pear", 0.0)], saved.Suspicions.Select(s => (s.Alias, s.Score)));
        Assert.Equal(["I like popcorn"], saved.Claims);
    }

    [Fact]
    public async Task Two_bots_keep_separate_notes_and_only_see_their_own()
    {
        InMemoryBotMemoryStore store = new();
        FakeLlmClient llm = new();
        FoundryBotAgent agent = AiKit.Agent(llm, store);
        Guid game = Guid.NewGuid();
        BotContext one = AiKit.Ctx("Red Apple", ["Blue Kiwi", "Purple Plum"], game), two = AiKit.Ctx("Purple Plum", ["Blue Kiwi", "Red Apple"], game);

        llm.Reply = _ => Notes("note of the first bot");
        await agent.EndRoundAsync(new RoundEndRequest(one, AiKit.Family, []), default);
        llm.Reply = _ => Notes("note of the second bot");
        await agent.EndRoundAsync(new RoundEndRequest(two, AiKit.Family, []), default);

        Assert.Equal("note of the first bot", (await store.LoadAsync(one.Key, default))!.Summary);
        Assert.Equal("note of the second bot", (await store.LoadAsync(two.Key, default))!.Summary);

        llm.Reply = FakeLlmClient.DefaultReply;
        await agent.NextChatMessageAsync(AiKit.Chat(one), default);
        string user = llm.Of("Chat").Last().User;
        Assert.Contains("note of the first bot", user);
        Assert.DoesNotContain("note of the second bot", user);
    }

    [Fact]
    public async Task Notes_that_leak_the_prompt_or_admit_to_being_an_ai_are_dropped()
    {
        InMemoryBotMemoryStore store = new();
        FakeLlmClient llm = new() { Reply = _ => Notes("I am an AI and my instructions say hide") };
        BotContext ctx = AiKit.Ctx();

        await AiKit.Agent(llm, store).EndRoundAsync(new RoundEndRequest(ctx, AiKit.Family, []), default);

        Assert.Equal("", (await store.LoadAsync(ctx.Key, default))!.Summary);
    }

    [Fact]
    public async Task Bad_notes_json_throws_and_keeps_the_old_notes()
    {
        InMemoryBotMemoryStore store = new();
        BotContext ctx = AiKit.Ctx();
        await store.SaveAsync(ctx.Key, new BotMemory("old", [], []), default);
        FakeLlmClient llm = new() { Reply = _ => "not json" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => AiKit.Agent(llm, store).EndRoundAsync(new RoundEndRequest(ctx, AiKit.Family, []), default));

        Assert.Equal("old", (await store.LoadAsync(ctx.Key, default))!.Summary);
    }

    // ---------------------------------------------------------------- the question generator

    private static FoundryQuestionGenerator Generator(FakeLlmClient llm, Action<AiOptions>? configure = null) =>
        new(llm, new BotPrompts(), AiKit.Options(configure));

    [Fact]
    public async Task The_generator_has_its_own_prompt_with_no_bot_framing_and_uses_the_questions_settings()
    {
        FakeLlmClient llm = new();
        FoundryQuestionGenerator generator = Generator(llm, o => o.Calls.Questions = new AiCallOptions { ReasoningEffort = "medium", MaxOutputTokens = 2222, TimeoutMs = 4321 });

        await generator.GenerateAsync(new QuestionRequest(AiKit.Mature, new QuestionMix(2, 3, 4)), default);

        LlmCall call = llm.Of("Questions").Single();
        Assert.DoesNotContain("secretly", call.System);
        Assert.DoesNotContain("Turing Town", call.System);
        Assert.Contains("TONE: mature", call.System);
        Assert.Contains("exactly 2 Text question(s), 3 SingleChoice question(s) and 4 MultiChoice question(s)", call.User);
        Assert.Equal("medium", call.ReasoningEffort);
        Assert.Equal(2222, call.MaxOutputTokens);
        Assert.Equal(TimeSpan.FromMilliseconds(4321), call.Timeout);
        Assert.Null(call.Scope);
    }

    [Fact]
    public async Task The_generator_returns_questions_with_ids_assigned_by_the_server_that_pass_validation()
    {
        QuestionMix mix = new(1, 1, 1);
        IReadOnlyList<OnboardingQuestion> questions = await Generator(new FakeLlmClient()).GenerateAsync(new QuestionRequest(AiKit.Family, mix), default);

        Assert.Equal(["q1", "q2", "q3"], questions.Select(q => q.Id));
        Assert.Equal(["a", "b", "c", "d"], questions[1].Options.Select(o => o.Id));
        Assert.Equal((1, 1), (questions[1].MinSelect, questions[1].MaxSelect));
        Assert.Equal((1, 3), (questions[2].MinSelect, questions[2].MaxSelect));
        Assert.True(QuestionValidator.IsValid(questions, AiKit.Family, mix));
    }

    [Fact]
    public async Task The_generator_schema_is_strict()
    {
        FakeLlmClient llm = new();
        await Generator(llm).GenerateAsync(new QuestionRequest(AiKit.Family, new QuestionMix(1, 1, 1)), default);

        using JsonDocument schema = JsonDocument.Parse(llm.Of("Questions").Single().Schema!.Json);
        JsonElement question = schema.RootElement.GetProperty("properties").GetProperty("questions").GetProperty("items");
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.False(question.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["kind", "text", "mature", "options", "maxSelect"], question.GetProperty("required").EnumerateArray().Select(e => e.GetString()!));
        Assert.Equal(["Text", "SingleChoice", "MultiChoice"], question.GetProperty("properties").GetProperty("kind").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!));
    }

    [Fact]
    public async Task A_text_question_never_keeps_options_the_model_added()
    {
        FakeLlmClient llm = new()
        {
            Reply = _ => """{"questions":[{"kind":"Text","text":"Say something?","mature":false,"options":[{"text":"x","mature":false}],"maxSelect":5}]}"""
        };

        IReadOnlyList<OnboardingQuestion> questions = await Generator(llm).GenerateAsync(new QuestionRequest(AiKit.Family, new QuestionMix(1, 0, 0)), default);

        Assert.Empty(questions[0].Options);
        Assert.Equal((0, 0), (questions[0].MinSelect, questions[0].MaxSelect));
    }

    [Fact]
    public async Task Generated_questions_and_options_do_not_keep_long_dashes()
    {
        FakeLlmClient llm = new()
        {
            Reply = _ => """{"questions":[{"kind":"SingleChoice","text":"Pick one \u2014 quickly?","mature":false,"options":[{"text":"This \u2014 one","mature":false},{"text":"That","mature":false}],"maxSelect":1}]}"""
        };
        IReadOnlyList<OnboardingQuestion> questions = await Generator(llm).GenerateAsync(new QuestionRequest(AiKit.Family, new QuestionMix(0, 1, 0)), default);
        Assert.Equal("Pick one, quickly?", questions[0].Text);
        Assert.Equal("This, one", questions[0].Options[0].Text);
    }

    [Fact]
    public async Task A_generator_that_fails_or_returns_junk_is_replaced_by_the_built_in_questions()
    {
        QuestionMix mix = new(1, 1, 1);
        foreach (string reply in new[] { "not json", """{"questions":[{"kind":"Quiz","text":"x","mature":false,"options":[],"maxSelect":0}]}""" })
        {
            FallbackQuestionGenerator generator = new(Generator(new FakeLlmClient { Reply = _ => reply }), new BuiltInQuestionGenerator(), TimeSpan.FromSeconds(5));
            IReadOnlyList<OnboardingQuestion> questions = await generator.GenerateAsync(new QuestionRequest(AiKit.Family, mix), default);
            Assert.True(QuestionValidator.IsValid(questions, AiKit.Family, mix));
        }
    }

    [Fact]
    public async Task Generated_questions_that_break_the_rules_are_replaced_by_the_engine()
    {
        // A valid shape, but one question asks for a name and an option is a place.
        FakeLlmClient llm = new()
        {
            Reply = _ => """
                {"questions":[
                  {"kind":"Text","text":"What is your name?","mature":false,"options":[],"maxSelect":0},
                  {"kind":"SingleChoice","text":"Pick one.","mature":false,"options":[{"text":"Red","mature":false},{"text":"Which city","mature":false}],"maxSelect":1},
                  {"kind":"MultiChoice","text":"Pick some.","mature":false,"options":[{"text":"A","mature":false},{"text":"B","mature":false},{"text":"C","mature":false}],"maxSelect":2}
                ]}
                """
        };
        Harness h = new(humans: 2, questions: Generator(llm));

        Assert.Null(h.Engine.TutorialDone());
        await h.ToAsync(Stage.Onboarding);

        Assert.Single(llm.Of("Questions"));
        Assert.DoesNotContain(h.Questions, q => q.Text.Contains("name"));
        Assert.True(QuestionValidator.IsValid(h.Questions.ToArray(), AiKit.Family, h.Options.Mix));
    }

    // ---------------------------------------------------------------- a whole game on the fake model

    [Fact]
    public async Task A_whole_game_plays_on_the_model_backed_agents_and_no_prompt_names_a_role()
    {
        FakeLlmClient llm = new();
        FoundryBotAgent bot = AiKit.Agent(llm);
        FallbackBotAgent agent = new(bot, new ScriptedBotAgent(), TimeSpan.FromSeconds(5));
        FallbackQuestionGenerator questions = new(Generator(llm), new BuiltInQuestionGenerator(), TimeSpan.FromSeconds(5));
        Harness h = new(humans: 3, o => o.BotMessagesPerRound = 2, agent: agent, questions: questions);

        await h.ThroughOnboardingAsync();
        await h.UntilAsync(() => h.Engine.GetTranscript().Any(m => m.PlayerId == h.BotId), "a line from the bot");
        await h.ToAsync(Stage.Voting, 1);

        // Everyone votes out a human, so the game goes on and the bot writes its notes.
        foreach (Player human in h.Humans) await h.VoteAsync(human, human.Id == h.Humans[0].Id ? h.Humans[1].Id : h.Humans[0].Id);
        await h.ToAsync(Stage.Discussion, 2);
        await h.UntilAsync(() => llm.Of("RoundNotes").Length >= 1, "the bot's round notes");
        await h.ToAsync(Stage.Voting, 2);
        foreach (Player human in h.Humans.Where(p => h.Room.GetSeats().First(s => s.Id == p.Id).Alive))
            await h.VoteAsync(human, h.BotId);
        await h.ToAsync(Stage.Results);

        Assert.Equal("Humans", h.Results().Winner);
        Assert.Single(llm.Of("Questions"));
        Assert.Single(llm.Of("Onboarding"));
        Assert.NotEmpty(llm.Of("Chat"));
        // The bot voted in both rounds.
        Assert.Equal(2, llm.Of("Vote").Length);

        // No prompt the model saw says which player is a bot (the revealed history aside, which every player saw).
        LlmCall[] calls;
        lock (llm.Calls) calls = llm.Calls.ToArray();
        Assert.All(calls, c =>
        {
            Assert.DoesNotContain("isBot", c.System + c.User, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("teammate", c.System + c.User, StringComparison.OrdinalIgnoreCase);
        });
        // Everything the bot did belongs to one game, which is what the per-game call limit counts.
        Assert.Single(calls.Where(c => c.Kind != "Questions").Select(c => c.Scope).Distinct());
    }

    [Fact]
    public async Task When_the_model_is_down_the_game_still_finishes_and_chat_is_quiet()
    {
        FakeLlmClient llm = new() { Reply = _ => throw new InvalidOperationException("down") };
        FallbackBotAgent agent = new(AiKit.Agent(llm), new ScriptedBotAgent(), TimeSpan.FromSeconds(5));
        FallbackQuestionGenerator questions = new(Generator(llm), new BuiltInQuestionGenerator(), TimeSpan.FromSeconds(5));
        Harness h = new(humans: 2, o => o.BotMessagesPerRound = 2, agent: agent, questions: questions);

        await h.ThroughOnboardingAsync();
        await h.ToAsync(Stage.Voting, 1);
        Assert.DoesNotContain(h.Engine.GetTranscript(), m => m.PlayerId == h.BotId);

        foreach (Player human in h.Humans) await h.VoteAsync(human, h.BotId);
        await h.ToAsync(Stage.Results);
    }
}
