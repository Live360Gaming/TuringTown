using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TuringTown.Models;
using TuringTown.Services.Ai;
using TuringTown.Services.Games;
using TuringTown.Services.Storage;

namespace TuringTown.Tests;

/// <summary>
/// A bot agent the tests control: what the bots say and who they vote for.
/// </summary>
internal sealed class TestAgent : IBotAgent
{
    /// <summary>Default: abstain, so tests decide the outcome with the humans' votes.</summary>
    public Func<VoteRequest, Guid?> Vote { get; set; } = _ => null;

    public Func<ChatRequest, string?> Chat { get; set; } = _ => null;

    /// <summary>Every request the engine made, so tests can check what a bot was (and was not) told.</summary>
    public List<ChatRequest> ChatRequests { get; } = [];
    public List<VoteRequest> VoteRequests { get; } = [];
    public List<OnboardingAnswerRequest> OnboardingRequests { get; } = [];
    public List<RoundEndRequest> RoundEndRequests { get; } = [];

    /// <summary>Runs inside every end-of-round call. Default: returns at once.</summary>
    public Func<RoundEndRequest, CancellationToken, Task>? RoundEnd { get; set; }

    /// <summary>Default: valid scripted answers. Tests can return bad ones to check the engine replaces them.</summary>
    public Func<OnboardingAnswerRequest, IReadOnlyList<OnboardingAnswer>>? Answer { get; set; }

    public Task<IReadOnlyList<OnboardingAnswer>> AnswerOnboardingAsync(OnboardingAnswerRequest request, CancellationToken ct)
    {
        lock (OnboardingRequests) OnboardingRequests.Add(request);
        return Task.FromResult(Answer?.Invoke(request) ?? request.Questions.Select(q => ScriptedBotAgent.ScriptedAnswer(q, request.HumanAnswers)).ToArray());
    }

    /// <summary>When set, answers chat instead of <see cref="Chat"/> (so a test can make the agent take time).</summary>
    public Func<ChatRequest, CancellationToken, Task<string?>>? ChatAsync { get; set; }

    public Task<string?> NextChatMessageAsync(ChatRequest request, CancellationToken ct)
    {
        lock (ChatRequests) ChatRequests.Add(request);
        return ChatAsync is { } async ? async(request, ct) : Task.FromResult(Chat(request));
    }

    public Task<Guid?> CastVoteAsync(VoteRequest request, CancellationToken ct)
    {
        lock (VoteRequests) VoteRequests.Add(request);
        return Task.FromResult(Vote(request));
    }

    public Task EndRoundAsync(RoundEndRequest request, CancellationToken ct)
    {
        lock (RoundEndRequests) RoundEndRequests.Add(request);
        return RoundEnd?.Invoke(request, ct) ?? Task.CompletedTask;
    }
}

internal static class TestContext
{
    /// <summary>A context for a request a test builds by hand.</summary>
    public static BotContext For(string alias, int botCount = 1) =>
        new(new BotKey(Guid.NewGuid(), Guid.NewGuid()), alias, botCount, [], 0, [], [], [], StyleProfile.Empty);
}

/// <summary>
/// A question generator the tests control. Default: the built-in questions.
/// </summary>
internal sealed class TestQuestionGenerator(IReadOnlyList<OnboardingQuestion>? questions = null) : IQuestionGenerator
{
    public Task<IReadOnlyList<OnboardingQuestion>> GenerateAsync(QuestionRequest request, CancellationToken ct) =>
        Task.FromResult(questions ?? ScriptedBotAgent.BuiltInQuestions(request.Policy, request.Mix));
}

/// <summary>
/// A room with a running engine on a fake clock. It records everything the engine broadcasts as JSON, exactly
/// as SignalR would send it, so tests can check what clients could see.
/// </summary>
internal sealed class Harness : IRoomBroadcaster
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public record Sent(string Kind, Stage StageAtSend, string Json);

    public Room Room { get; }
    public FakeTimeProvider Time { get; } = new();
    public TestAgent Agent { get; } = new();
    public InMemoryBotMemoryStore Memory { get; } = new();
    public InMemoryGameArchive Archive { get; } = new();
    public TuringTownEngine Engine { get; }
    public TuringTownOptions Options { get; } = new()
    {
        TutorialMaxSeconds = 10,
        OnboardingSeconds = 10,
        DiscussionSeconds = 10,
        VotingSeconds = 10,
        RevealSeconds = 2,
        ResultsSeconds = 5,
        BotMessagesPerRound = 0,
        BotChatMinDelayMs = 0,
        BotChatMaxDelayMs = 0,
        BotMentionReplyMinDelayMs = 0,
        BotMentionReplyMaxDelayMs = 0,
        BotVoteMinDelayMs = 0,
        BotVoteMaxDelayMs = 0,
        ChatMinIntervalMs = 1000,
        BotTypingMsPerChar = 0,
        BotMentionIgnorePercent = 0
    };

    public List<Player> Humans { get; } = [];
    public List<GameState> States { get; } = [];
    public List<Sent> Messages { get; } = [];

    public Harness(int humans, Action<TuringTownOptions>? configure = null, GameOptions? gameOptions = null, IBotAgent? agent = null, IQuestionGenerator? questions = null, IGameArchive? archive = null)
    {
        configure?.Invoke(Options);
        Room = new Room(RoomCode.CreateRandomRoomCode(), gameOptions ?? new GameOptions { MaxPlayers = 6, MinPlayers = 2 });

        for (int i = 0; i < humans; i++)
        {
            Assert.True(Room.TryAddPlayer(out Player? p, out _));
            Humans.Add(p!);
            Room.SetConnected(p!.Id, $"conn-{i}");
            Assert.Equal(ReadyError.None, Room.TrySetReady(p.Id, true));
        }

        Assert.Equal(RoomStartError.None, Room.TryStart(TuringTownEngine.BotCountFor));
        Engine = new TuringTownEngine(Room, agent ?? Agent, questions ?? new TestQuestionGenerator(), Memory, archive ?? Archive, new AllowAllContentFilter(), this, Time, Options, NullLogger.Instance);
        Engine.Start();
    }

    public Stage Stage => Room.State.Stage;

    public Guid BotId => Room.GetSeats().First(s => s.IsBot).Id;

    public IEnumerable<Guid> BotIds => Room.GetSeats().Where(s => s.IsBot).Select(s => s.Id);

    /// <summary>The questions of the Onboarding stage (call once it has started).</summary>
    public IReadOnlyList<OnboardingQuestion> Questions => ((OnboardingData)Room.State.Data!).Questions;

    /// <summary>
    /// A valid answer to every question: <paramref name="text"/> for a text question, the first option(s) otherwise.
    /// </summary>
    public static OnboardingAnswer[] AnswersFor(IReadOnlyList<OnboardingQuestion> questions, string text = "an answer") =>
        questions.Select(q => q.Kind == QuestionKind.Text
            ? new OnboardingAnswer(q.Id, text, [])
            : new OnboardingAnswer(q.Id, "", q.Options.Take(q.MinSelect).Select(o => o.Id).ToArray())).ToArray();

    public OnboardingAnswer[] Answers(string text = "an answer") => AnswersFor(Questions, text);

    /// <summary>
    /// Moves the fake clock forward until the condition holds. The engine runs on other threads, so this
    /// alternates small time steps with real pauses to let it catch up.
    /// </summary>
    public async Task UntilAsync(Func<bool> condition, string what)
    {
        for (int i = 0; i < 4000; i++)
        {
            if (condition()) return;
            Time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(1);
        }
        throw new TimeoutException($"Never reached: {what} (stuck in {Stage})");
    }

    public Task ToAsync(Stage stage, int? round = null) =>
        UntilAsync(() => Stage == stage && (round is null || Room.State.Round == round), $"{stage} {round}");

    /// <summary>
    /// Waits (without moving the clock) for the engine's background work to settle.
    /// </summary>
    public async Task SettleAsync() => await Task.Delay(50);

    /// <summary>
    /// Ends the tutorial and has every human submit the form, which leaves the game at the start of Discussion.
    /// </summary>
    public async Task ThroughOnboardingAsync()
    {
        Assert.Null(Engine.TutorialDone());
        await ToAsync(Stage.Onboarding);
        foreach (Player human in Humans)
            Assert.Null(await Engine.SubmitOnboardingAsync(human.Id, Answers()));
        await ToAsync(Stage.Discussion, 1);
    }

    public async Task VoteAsync(Player voter, Guid target) => Assert.Null(await Engine.CastVoteAsync(voter.Id, target));

    public RevealData LastReveal() => (RevealData)States.Last(s => s.Stage == Stage.Reveal).Data!;

    public ResultsData Results() => (ResultsData)States.Last(s => s.Stage == Stage.Results).Data!;

    private void Record(string kind, object payload)
    {
        lock (Messages)
            Messages.Add(new Sent(kind, Room.State.Stage, JsonSerializer.Serialize(payload, Json)));
    }

    public Task StateChanged(Room room, GameState state)
    {
        lock (States) States.Add(state);
        Record("state", state);
        return Task.CompletedTask;
    }

    public Task RosterChanged(Room room)
    {
        Record("roster", room.GetRoster());
        return Task.CompletedTask;
    }

    public Task ChatMessage(Room room, ChatMessage message)
    {
        Record("chat", message);
        return Task.CompletedTask;
    }

    public Task Progress(Room room, int done, int total)
    {
        Record("progress", new { done, total });
        return Task.CompletedTask;
    }
}
