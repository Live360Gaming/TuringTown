using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TuringTown.Models;
using TuringTown.Services.Ai;
using TuringTown.Services.Games;

namespace TuringTown.Tests;

/// <summary>Trivia the tests control: every question's right answer is the first one ("a").</summary>
internal sealed class FixedTrivia(int count) : ITriviaGenerator
{
    public Task<IReadOnlyList<TriviaQuestion>> GenerateAsync(TriviaRequest request, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TriviaQuestion>>(Enumerable.Range(1, count)
            .Select(i => new TriviaQuestion(request.Categories[i % request.Categories.Count].Id, $"Test question number {i}?",
                [$"Right {i}", $"Wrong {i}a", $"Wrong {i}b", $"Wrong {i}c"], 0, TriviaDifficulty.Easy, false))
            .ToArray());
}

/// <summary>A room playing one Shout Out game on a fake clock, recording every broadcast as the JSON a client gets.</summary>
internal sealed class ShoutOutHarness : IRoomBroadcaster
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public Room Room { get; }
    public FakeTimeProvider Time { get; } = new();
    public ShoutOutEngine Engine { get; }
    public Task<GameOutcome> Outcome { get; }
    public List<Player> Humans { get; } = [];
    public List<GameState> States { get; } = [];
    public List<(Stage Stage, string Json)> Messages { get; } = [];
    public List<(int Done, int Total)> ProgressEvents { get; } = [];

    public ShoutOutOptions Options { get; } = new()
    {
        TutorialMaxSeconds = 10,
        CategoriesSeconds = 10,
        Questions = 3,
        DoubleQuestions = 1,
        ReadSeconds = 2,
        BuzzSeconds = 6,
        RebuzzSeconds = 4,
        AnswerSeconds = 4,
        RevealSeconds = 2,
        ResultsSeconds = 5,
        BotCategoryMinDelayMs = 0,
        BotCategoryMaxDelayMs = 0
    };

    /// <param name="random">The bots' dice. The default (0.99) makes them never know an answer and never buzz.</param>
    public ShoutOutHarness(int humans, Action<ShoutOutOptions>? configure = null, ITriviaGenerator? trivia = null, Func<double>? random = null)
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

        Engine = new ShoutOutEngine(Room, trivia ?? new FixedTrivia(10), new AllowAllContentFilter(), this, Time, Options, NullLogger.Instance,
            night: null, random: random ?? (() => 0.99));
        Outcome = Task.Run(Engine.PlayAsync);
    }

    public Stage Stage => Room.State.Stage;
    public IReadOnlyList<PlayerSeat> Seats => Room.GetSeats();
    public TriviaQuestionData Question => (TriviaQuestionData)Room.State.Data!;

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

    public Task ToAsync(Stage stage, int? round = null) =>
        UntilAsync(() => Stage == stage && (round is null || Room.State.Round == round), $"{stage} {round}");

    /// <summary>Through the tutorial and the category vote (every human picks the first category) to question 1.</summary>
    public async Task ToFirstQuestionAsync()
    {
        await ToAsync(Stage.Tutorial);
        Assert.Null(Engine.TutorialDone());
        await ToAsync(Stage.Categories);
        string first = ((CategoriesData)Room.State.Data!).Categories[0].Id;
        foreach (Player human in Humans)
            Assert.Null(await Engine.VoteCategoriesAsync(human.Id, [first]));
        await ToAsync(Stage.Question, 1);
    }

    /// <summary>Moves the clock until buzzing is open on the current question.</summary>
    public Task ToBuzzOpenAsync() =>
        UntilAsync(() => Stage == Stage.Question && Time.GetUtcNow().ToUnixTimeMilliseconds() >= Question.BuzzOpensAtUtcMs, "buzzing open");

    public AnswerRevealData LastReveal() => (AnswerRevealData)States.Last(s => s.Stage == Stage.AnswerReveal).Data!;

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

    public Task RoomChanged(Room room) => Task.CompletedTask;
    public Task Secret(Room room, Guid playerId, SecretInfo secret) => Task.CompletedTask;
    public Task ChatMessage(Room room, ChatMessage message) => Task.CompletedTask;
    public Task Typing(Room room, Guid playerId, bool typing) => Task.CompletedTask;

    public Task Progress(Room room, int done, int total)
    {
        lock (ProgressEvents) ProgressEvents.Add((done, total));
        return Task.CompletedTask;
    }

    public Task GameError(Room room, string message) => Task.CompletedTask;
}

public class ShoutOutFlowTests
{
    [Fact]
    public async Task The_first_buzz_answers_and_a_right_answer_scores()
    {
        ShoutOutHarness h = new(humans: 3);
        await h.ToFirstQuestionAsync();
        await h.ToBuzzOpenAsync();

        Player a = h.Humans[0], b = h.Humans[1];
        Assert.Null(h.Engine.Buzz(a.Id));
        Assert.Equal("Someone beat you to it!", h.Engine.Buzz(b.Id));
        await h.ToAsync(Stage.Buzzed);
        Assert.Equal(a.Id, h.Question.BuzzerId);
        Assert.NotNull(h.Engine.Answer(b.Id, "a")); // not their turn

        Assert.Null(h.Engine.Answer(a.Id, "a"));
        await h.ToAsync(Stage.AnswerReveal);
        AnswerRevealData reveal = h.LastReveal();
        Assert.Equal("a", reveal.CorrectId);
        TriviaAttempt attempt = Assert.Single(reveal.Attempts);
        Assert.True(attempt.Correct);
        Assert.Equal(h.Options.CorrectPoints, attempt.Points);
        Assert.Equal((a.Id, h.Options.CorrectPoints), (reveal.Leaders[0].PlayerId, reveal.Leaders[0].Points));
    }

    [Fact]
    public async Task A_wrong_answer_costs_points_and_reopens_the_question_for_everyone_else()
    {
        ShoutOutHarness h = new(humans: 3);
        await h.ToFirstQuestionAsync();
        await h.ToBuzzOpenAsync();

        Player a = h.Humans[0], b = h.Humans[1];
        Assert.Null(h.Engine.Buzz(a.Id));
        await h.ToAsync(Stage.Buzzed);
        Assert.Null(h.Engine.Answer(a.Id, "b"));

        // Back to the question, open at once, with A out.
        await h.UntilAsync(() => h.Stage == Stage.Question && h.Question.Out.Contains(a.Id), "the question reopened");
        Assert.Equal("You already had a go at this one.", h.Engine.Buzz(a.Id));
        Assert.Null(h.Engine.Buzz(b.Id));
        await h.ToAsync(Stage.Buzzed);
        Assert.Null(h.Engine.Answer(b.Id, "a"));

        await h.ToAsync(Stage.AnswerReveal);
        TriviaAttempt[] attempts = [.. h.LastReveal().Attempts];
        Assert.Equal([(a.Id, false, -h.Options.WrongPoints), (b.Id, true, h.Options.CorrectPoints)],
            attempts.Select(x => (x.PlayerId, x.Correct, x.Points)));
    }

    [Fact]
    public async Task Buzzing_before_the_answers_are_up_locks_you_out_for_a_moment()
    {
        ShoutOutHarness h = new(humans: 2, o => { o.ReadSeconds = 3; o.EarlyBuzzLockoutMs = 5000; });
        await h.ToFirstQuestionAsync();
        Player a = h.Humans[0], b = h.Humans[1];

        Assert.Equal("Too early! Wait for the answers.", h.Engine.Buzz(a.Id));
        await h.ToBuzzOpenAsync();
        // Still locked out (5 s from the early buzz), while someone who waited gets in.
        Assert.Equal("Too early! Wait a moment.", h.Engine.Buzz(a.Id));
        Assert.Null(h.Engine.Buzz(b.Id));
    }

    [Fact]
    public async Task Nobody_buzzing_reveals_the_answer_with_no_points_and_the_last_question_is_worth_double()
    {
        ShoutOutHarness h = new(humans: 2, o => o.Questions = 2);
        await h.ToFirstQuestionAsync();
        await h.ToAsync(Stage.AnswerReveal, 1);
        Assert.Empty(h.LastReveal().Attempts);

        await h.ToAsync(Stage.Question, 2);
        Assert.True(h.Question.Double);
        await h.ToBuzzOpenAsync();
        Assert.Null(h.Engine.Buzz(h.Humans[0].Id));
        await h.ToAsync(Stage.Buzzed);
        Assert.Null(h.Engine.Answer(h.Humans[0].Id, "a"));
        await h.ToAsync(Stage.AnswerReveal, 2);
        Assert.Equal(2 * h.Options.CorrectPoints, Assert.Single(h.LastReveal().Attempts).Points);

        await h.ToAsync(Stage.Results);
        Assert.Null(h.Engine.EndResults());
        GameOutcome outcome = await h.Outcome;
        Assert.True(outcome.Completed);
        Assert.Null(outcome.Winner);
        Assert.Equal(2 * h.Options.CorrectPoints, outcome.SeatPoints[h.Humans[0].Id]);
    }

    [Fact]
    public async Task The_category_vote_counts_the_bots_too_and_checks_the_picks()
    {
        ShoutOutHarness h = new(humans: 2);
        await h.ToAsync(Stage.Tutorial);
        Assert.Null(h.Engine.TutorialDone());
        await h.ToAsync(Stage.Categories);
        CategoriesData data = (CategoriesData)h.Room.State.Data!;
        Assert.Equal(h.Options.CategoryChoices, data.Categories.Count);

        Player a = h.Humans[0];
        Assert.NotNull(await h.Engine.VoteCategoriesAsync(a.Id, ["not-a-category"]));
        Assert.NotNull(await h.Engine.VoteCategoriesAsync(a.Id, data.Categories.Take(h.Options.MaxCategoryPicks + 1).Select(c => c.Id).ToArray()));
        Assert.NotNull(await h.Engine.VoteCategoriesAsync(a.Id, []));
        Assert.Null(await h.Engine.VoteCategoriesAsync(a.Id, [data.Categories[0].Id]));

        // Two humans and two bots: the total is every seat, so it says nothing about how many are bots.
        await h.UntilAsync(() => h.ProgressEvents.Any(p => p.Done == 3), "both bots voted");
        Assert.All(h.ProgressEvents, p => Assert.Equal(h.Seats.Count, p.Total));
    }

    [Fact]
    public async Task Bots_play_too_but_nothing_sent_says_which_seat_is_a_bot()
    {
        // Dice that always come up low: every bot knows every answer and buzzes as soon as it may.
        ShoutOutHarness h = new(humans: 2, o => o.Questions = 2, random: () => 0.0);
        await h.ToFirstQuestionAsync();
        await h.ToAsync(Stage.Results);

        Guid[] bots = h.Seats.Where(s => s.IsBot).Select(s => s.Id).ToArray();
        Assert.Contains(h.States.Where(s => s.Stage == Stage.AnswerReveal).SelectMany(s => ((AnswerRevealData)s.Data!).Attempts),
            a => bots.Contains(a.PlayerId) && a.Correct);
        Assert.All(h.Messages, m =>
        {
            Assert.DoesNotMatch(new Regex(@"(?i)\b(?:is|was)?bots?\b"), m.Json);
            Assert.DoesNotMatch(new Regex(@"(?i)\broles?\b"), m.Json);
        });
        Assert.Empty(h.Engine.GetUnmasked());
    }

    [Fact]
    public void A_bot_misses_guesses_and_stays_quiet_like_a_person()
    {
        ShoutOutOptions options = new();
        ShoutOutBotPolicy policy = new(options);
        Guid bot = Guid.NewGuid();
        Assert.InRange(policy.KnowledgeOf(bot), options.BotKnowledgeMin, options.BotKnowledgeMax);

        ShoutOutBotPolicy.BuzzPlan[] plans = Enumerable.Range(0, 2000).Select(_ => policy.PlanBuzz(bot, TriviaDifficulty.Medium)).ToArray();
        Assert.Contains(plans, p => p.BuzzAfter is null);       // sometimes stays quiet
        Assert.Contains(plans, p => !p.Knows);                  // does not know everything
        Assert.Contains(plans, p => !p.Knows && p.BuzzAfter is not null); // sometimes guesses
        Assert.All(plans.Where(p => p.BuzzAfter is not null), p => Assert.True(p.BuzzAfter >= TimeSpan.FromSeconds(1.2)));

        int wrong = Enumerable.Range(0, 2000).Count(_ => policy.PickAnswer(knows: false, correct: 2, count: 4) != 2);
        Assert.InRange(wrong, 1200, 1800); // a guess is right about a quarter of the time (plus chance)
        Assert.Equal(2, policy.PickAnswer(knows: true, correct: 2, count: 4));
    }

    [Fact]
    public void Bots_slow_down_when_the_humans_are_slow()
    {
        ShoutOutBotPolicy policy = new(new ShoutOutOptions(), () => 0.0);
        TimeSpan quick = policy.PlanBuzz(Guid.NewGuid(), TriviaDifficulty.Easy).BuzzAfter!.Value;
        for (int i = 0; i < 5; i++) policy.RecordHumanBuzz(TimeSpan.FromSeconds(6));
        TimeSpan slow = policy.PlanBuzz(Guid.NewGuid(), TriviaDifficulty.Easy).BuzzAfter!.Value;
        Assert.True(slow > quick * 1.5, $"{slow} vs {quick}");
    }

    [Fact]
    public void The_chosen_categories_come_first()
    {
        TriviaQuestion Q(string category, int i) => new(category, $"Question {category} {i}?", ["a1", "b1", "c1", "d1"], 0, TriviaDifficulty.Easy, false);
        TriviaQuestion[] all = [.. Enumerable.Range(0, 5).Select(i => Q("music", i)), .. Enumerable.Range(0, 5).Select(i => Q("food", i))];
        IReadOnlyList<TriviaQuestion> picked = ShoutOutEngine.Order(all, ["food"], 4);
        Assert.Equal(4, picked.Count);
        Assert.All(picked, q => Assert.Equal("food", q.CategoryId));
    }
}

public class TriviaTests
{
    private static readonly ContentPolicy Family = new(FamilyFriendly: true);

    private static TriviaQuestion Good(string text = "Which planet is the Red Planet?") =>
        new("science", text, ["Mars", "Venus", "Jupiter", "Mercury"], 0, TriviaDifficulty.Easy, false);

    [Fact]
    public void The_built_in_bank_is_all_valid_and_covers_every_category()
    {
        Assert.All(BuiltInTrivia.Bank, q => Assert.Null(TriviaValidator.Problem(q, Family)));
        Assert.All(TriviaCategories.All, c => Assert.True(BuiltInTrivia.Bank.Count(q => q.CategoryId == c.Id) >= 4, c.Id));
        Assert.Equal(BuiltInTrivia.Bank.Length, BuiltInTrivia.Bank.Select(q => q.Text).Distinct().Count());
    }

    [Fact]
    public void Shuffling_keeps_the_right_answer_right()
    {
        for (int i = 0; i < 50; i++)
        {
            TriviaQuestion q = BuiltInTrivia.Shuffle(Good());
            Assert.Equal("Mars", q.Options[q.Correct]);
        }
    }

    [Fact]
    public void Bad_questions_are_rejected()
    {
        Assert.Null(TriviaValidator.Problem(Good(), Family));
        Assert.NotNull(TriviaValidator.Problem(Good() with { Options = ["Mars", "Mars", "Venus", "Pluto"] }, Family));
        Assert.NotNull(TriviaValidator.Problem(Good() with { Options = ["Mars", "Venus", "Pluto"] }, Family));
        Assert.NotNull(TriviaValidator.Problem(Good() with { Correct = 4 }, Family));
        Assert.NotNull(TriviaValidator.Problem(Good() with { CategoryId = "nope" }, Family));
        Assert.NotNull(TriviaValidator.Problem(Good() with { Mature = true }, Family));
        Assert.NotNull(TriviaValidator.Problem(Good("How old are you?"), Family));
        Assert.NotNull(TriviaValidator.Problem(Good() with { Era = "the future" }, Family));
    }

    [Fact]
    public void The_built_in_bank_prefers_the_room_s_categories_and_decades()
    {
        // The bank has two 1990s video game questions (and no timeless ones), so those two come first.
        TriviaRequest request = new([new TriviaCategory("games", "Video games")], [new EraCount("1990s", 3)], 2, Family);
        IReadOnlyList<TriviaQuestion> picked = BuiltInTrivia.Pick(request, 2);
        Assert.All(picked, q => Assert.Equal("games", q.CategoryId));
        Assert.All(picked, q => Assert.True(q.Era is null or "1990s", q.Text));
    }

    private sealed class Broken : ITriviaGenerator
    {
        public Task<IReadOnlyList<TriviaQuestion>> GenerateAsync(TriviaRequest request, CancellationToken ct) => throw new InvalidOperationException("down");
    }

    private sealed class Few(params TriviaQuestion[] questions) : ITriviaGenerator
    {
        public Task<IReadOnlyList<TriviaQuestion>> GenerateAsync(TriviaRequest request, CancellationToken ct) => Task.FromResult<IReadOnlyList<TriviaQuestion>>(questions);
    }

    [Fact]
    public async Task The_fallback_fills_in_from_the_bank()
    {
        TriviaRequest request = new(TriviaCategories.All, [], 6, Family);

        IReadOnlyList<TriviaQuestion> fromBroken = await new FallbackTriviaGenerator(new Broken(), TimeSpan.FromSeconds(5), NullLogger.Instance).GenerateAsync(request, default);
        Assert.Equal(6, fromBroken.Count);

        TriviaQuestion mine = Good("A question the AI wrote for this room?");
        TriviaQuestion bad = Good() with { Correct = 9 };
        IReadOnlyList<TriviaQuestion> mixed = await new FallbackTriviaGenerator(new Few(mine, bad), TimeSpan.FromSeconds(5), NullLogger.Instance).GenerateAsync(request, default);
        Assert.Equal(6, mixed.Count);
        Assert.Contains(mine, mixed);
        Assert.DoesNotContain(bad, mixed);
        Assert.All(mixed, q => Assert.True(TriviaValidator.IsValid(q, Family)));
    }

    [Fact]
    public void The_model_s_trivia_is_parsed_and_only_questions_it_answers_the_same_way_cold_are_kept()
    {
        const string json = """
            {"questions":[
              {"category":"music","question":"Which band sang Hey Jude?","answers":["Queen","The Beatles","ABBA","Blur"],"correct":2,"difficulty":"Easy","era":"1960s","mature":false},
              {"category":"science","question":"How many legs does a spider have?","answers":["Six","Eight","Ten","Four"],"correct":2,"difficulty":"Easy","era":"none","mature":false}
            ]}
            """;
        IReadOnlyList<TriviaQuestion> parsed = FoundryTriviaGenerator.Parse(json);
        Assert.Equal(["The Beatles", "Eight"], parsed.Select(q => q.Options[q.Correct]));
        Assert.Equal(["1960s", null], parsed.Select(q => q.Era));

        // The cold answer agrees on the first and not on the second, so only the first is kept.
        IReadOnlyList<TriviaQuestion> kept = FoundryTriviaGenerator.Agreed(parsed, """{"answers":[{"number":1,"pick":2},{"number":2,"pick":1}]}""");
        Assert.Equal("Which band sang Hey Jude?", Assert.Single(kept).Text);
    }
}
