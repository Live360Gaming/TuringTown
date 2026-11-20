using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TuringTown.Models;
using TuringTown.Services.Ai;
using TuringTown.Services.Games;

namespace TuringTown.Tests;

/// <summary>
/// A bot agent the tests control: what the bots say and who they vote for.
/// </summary>
internal sealed class TestAgent : IBotAgent
{
    /// <summary>Default: abstain, so tests decide the outcome with the humans' votes.</summary>
    public Func<VoteRequest, Guid?> Vote { get; set; } = _ => null;

    public Func<ChatRequest, string?> Chat { get; set; } = _ => null;

    public IReadOnlyList<OnboardingQuestion>? Questions { get; set; }

    public Task<IReadOnlyList<OnboardingQuestion>> GenerateOnboardingQuestionsAsync(QuestionRequest request, CancellationToken ct) =>
        Task.FromResult(Questions ?? ScriptedBotAgent.BuiltInQuestions(request.Policy, request.Count));

    public Task<IReadOnlyList<string>> AnswerOnboardingAsync(OnboardingAnswerRequest request, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(request.Questions.Select(_ => "an answer").ToArray());

    public Task<string?> NextChatMessageAsync(ChatRequest request, CancellationToken ct) => Task.FromResult(Chat(request));

    public Task<Guid?> CastVoteAsync(VoteRequest request, CancellationToken ct) => Task.FromResult(Vote(request));
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
        BotVoteMinDelayMs = 0,
        BotVoteMaxDelayMs = 0,
        ChatMinIntervalMs = 1000
    };

    public List<Player> Humans { get; } = [];
    public List<GameState> States { get; } = [];
    public List<Sent> Messages { get; } = [];

    public Harness(int humans, Action<TuringTownOptions>? configure = null, GameOptions? gameOptions = null, IBotAgent? agent = null)
    {
        configure?.Invoke(Options);
        Room = new Room(RoomCode.CreateRandomRoomCode(), gameOptions ?? new GameOptions { MaxPlayers = 6, MinPlayers = 2 });

        for (int i = 0; i < humans; i++)
        {
            Assert.True(Room.TryAddPlayer(out Player? p, out _));
            Humans.Add(p!);
            Room.SetConnected(p!.Id, $"conn-{i}");
        }

        Assert.Equal(RoomStartError.None, Room.TryStart(TuringTownEngine.BotCountFor));
        Engine = new TuringTownEngine(Room, agent ?? Agent, new AllowAllContentFilter(), this, Time, Options, NullLogger.Instance);
        Engine.Start();
    }

    public Stage Stage => Room.State.Stage;

    public Guid BotId => Room.GetSeats().First(s => s.IsBot).Id;

    public IEnumerable<Guid> BotIds => Room.GetSeats().Where(s => s.IsBot).Select(s => s.Id);

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
            Assert.Null(await Engine.SubmitOnboardingAsync(human.Id, Enumerable.Repeat<string?>("an answer", Options.QuestionCount).ToArray()));
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
