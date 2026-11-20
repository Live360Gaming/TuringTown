using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using TuringTown.Models;
using TuringTown.Services.Ai;

namespace TuringTown.Services.Games;

/// <summary>
/// Runs one Turing Town game for a room: Tutorial, Onboarding, then rounds of Discussion, Voting and Reveal until
/// a side wins, then Results. The server is the only source of truth; clients just render the state and send
/// their answers, chat lines and votes.
///
/// Roles live only in the room's seats and never leave the server before the reveal.
/// Lock order is always engine gate first, then the room's lock (the room never calls back into the engine).
/// Broadcasts are never sent while a lock is held.
/// </summary>
public sealed class TuringTownEngine : IDisposable
{
    private readonly Room _room;
    private readonly IBotAgent _agent;
    private readonly IContentFilter _contentFilter;
    private readonly IRoomBroadcaster _broadcaster;
    private readonly TimeProvider _time;
    private readonly TuringTownOptions _opt;
    private readonly ILogger _logger;
    private readonly Action<TuringTownEngine>? _onFinished;
    private readonly CancellationTokenSource _cts = new();

    // Everything below is guarded by _gate.
    private readonly object _gate = new();
    private readonly List<ChatMessage> _transcript = [];
    private readonly Dictionary<Guid, string[]> _answers = [];
    private readonly Dictionary<Guid, string> _personas = [];
    private readonly Dictionary<Guid, Guid?> _votes = [];
    private readonly Dictionary<Guid, DateTimeOffset> _lastChat = [];
    private readonly HashSet<Guid> _unmasked = [];
    private IReadOnlyList<OnboardingQuestion> _questions = [];
    private TaskCompletionSource _stageDone = NewSignal();

    private readonly TaskCompletionSource _tutorialDone = NewSignal();
    private readonly TaskCompletionSource _resultsDone = NewSignal();
    private Task? _loop;

    public TuringTownEngine(
        Room room,
        IBotAgent agent,
        IContentFilter contentFilter,
        IRoomBroadcaster broadcaster,
        TimeProvider time,
        TuringTownOptions options,
        ILogger logger,
        Action<TuringTownEngine>? onFinished = null)
    {
        _room = room;
        _agent = agent;
        _contentFilter = contentFilter;
        _broadcaster = broadcaster;
        _time = time;
        _opt = options;
        _logger = logger;
        _onFinished = onFinished;
    }

    /// <summary>
    /// 1 bot for 2-4 humans, 2 bots for 5 or more.
    /// </summary>
    public static int BotCountFor(int humans) => humans <= 4 ? 1 : 2;

    /// <summary>
    /// Completes when the game has ended and the room is back in the lobby.
    /// </summary>
    public Task Completion => _loop ?? Task.CompletedTask;

    /// <summary>
    /// Starts the game loop. The room must already be out of the lobby (see <see cref="Room.TryStart"/>).
    /// </summary>
    public void Start() => _loop = Task.Run(RunAsync);

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---------------------------------------------------------------- the game loop

    private async Task RunAsync()
    {
        try
        {
            await _broadcaster.RosterChanged(_room);

            // Question generation runs during the tutorial so the AI's latency is hidden.
            Task<IReadOnlyList<OnboardingQuestion>> questions = GenerateQuestionsAsync();

            await RunTutorialAsync();
            await RunOnboardingAsync(await questions);

            int round = 0;
            string? winner;
            do
            {
                round++;
                await RunDiscussionAsync(round);
                await RunVotingAndRevealAsync(round);
                winner = CheckWinner(round);
            } while (winner is null);

            await RunResultsAsync(winner);
        }
        catch (OperationCanceledException)
        {
            // The engine was disposed.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Game in room {Room} failed", _room.Code);
        }
        finally
        {
            await EndAsync();
        }
    }

    private async Task EndAsync()
    {
        try
        {
            _room.ResetToLobby();
            await _broadcaster.StateChanged(_room, _room.State);
            await _broadcaster.RosterChanged(_room);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reset room {Room}", _room.Code);
        }

        _onFinished?.Invoke(this);
    }

    private async Task RunTutorialAsync()
    {
        TimeSpan max = TimeSpan.FromSeconds(_opt.TutorialMaxSeconds);
        await EnterStageAsync(Stage.Tutorial, 0, 0, max, null);
        await WaitAsync(max, _tutorialDone.Task);
    }

    private async Task RunOnboardingAsync(IReadOnlyList<OnboardingQuestion> questions)
    {
        TimeSpan duration = TimeSpan.FromSeconds(_opt.OnboardingSeconds);
        lock (_gate)
        {
            _questions = questions;
            _answers.Clear();
            _stageDone = NewSignal();
        }

        await EnterStageAsync(Stage.Onboarding, 0, 0, duration, new OnboardingData(questions));
        await UpdateProgressAsync();
        await WaitAsync(duration, CurrentSignal());

        // The bots answer last, so they can match how the humans actually wrote.
        await BotOnboardingAsync(questions);
    }

    private async Task RunDiscussionAsync(int round)
    {
        TimeSpan duration = TimeSpan.FromSeconds(_opt.DiscussionSeconds);
        await EnterStageAsync(Stage.Discussion, round, 0, duration, null);

        using CancellationTokenSource stage = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        Task[] bots = BotSeats().Select(b => BotChatLoopAsync(b, round, stage.Token)).ToArray();

        await WaitAsync(duration);

        stage.Cancel();
        await Task.WhenAll(bots);
    }

    private async Task RunVotingAndRevealAsync(int round)
    {
        TimeSpan duration = TimeSpan.FromSeconds(_opt.VotingSeconds);
        lock (_gate)
        {
            _votes.Clear();
            _stageDone = NewSignal();
        }

        await EnterStageAsync(Stage.Voting, round, 0, duration, null);

        using CancellationTokenSource stage = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        Task[] bots = BotSeats().Select(b => BotVoteAsync(b, round, stage.Token)).ToArray();

        await UpdateProgressAsync();
        await WaitAsync(duration, CurrentSignal());

        stage.Cancel();
        await Task.WhenAll(bots);

        RevealData reveal = TallyVotes();

        // Tell everyone who is out before the reveal, so no phone offers a dead player to vote for next round
        // and the voted-out player's own screen switches to watching.
        await _broadcaster.RosterChanged(_room);

        // From here on the bot is public knowledge, announced by the Reveal state itself.
        if (reveal is {WasBot: true, Eliminated: { } found})
        {
            lock (_gate)
                _unmasked.Add(found.Id);
        }

        TimeSpan revealTime = TimeSpan.FromSeconds(_opt.RevealSeconds);
        await EnterStageAsync(Stage.Reveal, round, 0, revealTime, reveal);
        await WaitAsync(revealTime);
    }

    private async Task RunResultsAsync(string winner)
    {
        IReadOnlyList<RoleReveal> roles = _room.GetSeats().Select(s => new RoleReveal(s.Id, s.Alias, s.IsBot)).ToArray();
        lock (_gate)
        {
            foreach (RoleReveal role in roles.Where(r => r.WasBot))
                _unmasked.Add(role.PlayerId);
        }

        TimeSpan duration = TimeSpan.FromSeconds(_opt.ResultsSeconds);
        await EnterStageAsync(Stage.Results, 0, 0, duration, new ResultsData(winner, roles));
        await WaitAsync(duration, _resultsDone.Task);
    }

    /// <summary>
    /// Humans win when no bot is left. Bots win when they are at least as many as the humans, or when the
    /// round limit passes with a bot still hiding (so a run of tied votes cannot stall the game forever).
    /// </summary>
    private string? CheckWinner(int round)
    {
        IReadOnlyList<PlayerSeat> seats = _room.GetSeats();
        int bots = seats.Count(s => s.IsBot && s.Alive);
        int humans = seats.Count(s => !s.IsBot && s.Alive);

        if (bots == 0) return "Humans";
        if (bots >= humans || round >= _opt.MaxRounds) return "Bots";
        return null;
    }

    // ---------------------------------------------------------------- stage helpers

    private async Task EnterStageAsync(Stage stage, int round, int step, TimeSpan? duration, object? data)
    {
        long? endsAt = duration is { } d ? _time.GetUtcNow().Add(d).ToUnixTimeMilliseconds() : null;
        GameState state = _room.SetState(new GameState(Room.GameId, stage, round, step, endsAt, data));
        await _broadcaster.StateChanged(_room, state);
    }

    private Task CurrentSignal()
    {
        lock (_gate)
            return _stageDone.Task;
    }

    /// <summary>
    /// Waits for <paramref name="timeout"/> or until <paramref name="signal"/> completes, whichever is first.
    /// Throws if the engine is disposed meanwhile.
    /// </summary>
    private async Task WaitAsync(TimeSpan timeout, Task? signal = null)
    {
        using CancellationTokenSource local = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        Task delay = Task.Delay(timeout, _time, local.Token);

        await Task.WhenAny(signal ?? delay, delay);

        local.Cancel();
        _cts.Token.ThrowIfCancellationRequested();

        try
        {
            await delay;
        }
        catch (OperationCanceledException)
        {
            // We cancelled it ourselves because the signal arrived first.
        }
    }

    // ---------------------------------------------------------------- progress

    private (Stage Stage, int Done, int Total) ComputeProgress()
    {
        Stage stage = _room.State.Stage;
        IReadOnlyList<PlayerSeat> seats = _room.GetSeats();

        switch (stage)
        {
            case Stage.Onboarding:
            {
                PlayerSeat[] required = seats.Where(s => !s.IsBot && s.Connected).ToArray();
                return (stage, required.Count(s => _answers.ContainsKey(s.Id)), required.Length);
            }
            case Stage.Voting:
            {
                // Bots count too, otherwise the vote could end before they have voted.
                PlayerSeat[] required = seats.Where(s => s.Alive && (s.IsBot || s.Connected)).ToArray();
                return (stage, required.Count(s => _votes.ContainsKey(s.Id)), required.Length);
            }
            default:
                return (stage, 0, 0);
        }
    }

    /// <summary>
    /// Tells the room how far along the stage is, and ends the stage early once everyone required is done.
    /// A player who has dropped is not waited for.
    /// </summary>
    private async Task UpdateProgressAsync()
    {
        (Stage Stage, int Done, int Total) progress;
        TaskCompletionSource signal;
        lock (_gate)
        {
            progress = ComputeProgress();
            signal = _stageDone;
        }

        if (progress.Stage is not (Stage.Onboarding or Stage.Voting)) return;

        await _broadcaster.Progress(_room, progress.Done, progress.Total);
        if (progress.Done >= progress.Total) signal.TrySetResult();
    }

    /// <summary>
    /// Call when someone connects or drops, so a vote or form is not held up by a player who left.
    /// </summary>
    public Task NotifyPresenceChangedAsync() => UpdateProgressAsync();

    // ---------------------------------------------------------------- calls from the hub (return an error message, or null)

    public IReadOnlyList<ChatMessage> GetTranscript()
    {
        lock (_gate)
            return _transcript.ToArray();
    }

    /// <summary>
    /// Bots that have already been revealed (voted out and announced, or the game is over). A client that joins
    /// late uses this to show them as robots. Never includes a bot whose reveal has not happened yet.
    /// </summary>
    public IReadOnlyList<Guid> GetUnmasked()
    {
        lock (_gate)
            return _unmasked.ToArray();
    }

    public async Task<string?> TutorialStepAsync(int step)
    {
        if (step is < 0 or > 100) return "That tutorial step does not exist.";

        if (!_room.TryUpdateState(Stage.Tutorial, s => s with { Step = step }, out GameState state))
            return "The tutorial is not running.";

        await _broadcaster.StateChanged(_room, state);
        return null;
    }

    public string? TutorialDone()
    {
        if (_room.State.Stage != Stage.Tutorial) return "The tutorial is not running.";
        _tutorialDone.TrySetResult();
        return null;
    }

    public string? ReturnToLobby()
    {
        if (_room.State.Stage != Stage.Results) return "The game is not over yet.";
        _resultsDone.TrySetResult();
        return null;
    }

    public async Task<string?> SubmitOnboardingAsync(Guid playerId, IReadOnlyList<string?>? answers)
    {
        if (!_room.TryGetSeat(playerId, out PlayerSeat seat) || seat.IsBot) return "Unknown player.";
        if (_room.State.Stage != Stage.Onboarding) return "The form is closed.";

        IReadOnlyList<OnboardingQuestion> questions;
        lock (_gate)
            questions = _questions;

        if (answers is null || answers.Count != questions.Count) return "Please answer every question.";

        string[] cleaned = new string[questions.Count];
        for (int i = 0; i < cleaned.Length; i++)
        {
            ChatCheck check = ChatFilter.Check(answers[i], _opt.AnswerMaxLength, out string text);
            if (check is not (ChatCheck.Ok or ChatCheck.Empty)) return ChatFilter.Describe(check);
            if (text.Length > 0 && !await _contentFilter.IsAllowedAsync(text, _room.ContentPolicy, _cts.Token))
                return "That answer is not allowed.";
            cleaned[i] = text;
        }

        lock (_gate)
        {
            if (_room.State.Stage != Stage.Onboarding) return "The form is closed.";
            _answers[playerId] = cleaned;
        }

        await UpdateProgressAsync();
        return null;
    }

    public async Task<string?> SendChatAsync(Guid playerId, string? text)
    {
        if (!_room.TryGetSeat(playerId, out PlayerSeat seat) || seat.IsBot) return "Unknown player.";
        if (!seat.Alive) return "You have been voted out. You can only watch now.";
        if (_room.State.Stage != Stage.Discussion) return "Chat is closed right now.";

        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (_lastChat.TryGetValue(playerId, out DateTimeOffset last) && (now - last).TotalMilliseconds < _opt.ChatMinIntervalMs)
                return "Slow down a little.";
            _lastChat[playerId] = now;
        }

        return await PostChatAsync(seat, text, _cts.Token);
    }

    public async Task<string?> CastVoteAsync(Guid playerId, Guid targetId)
    {
        if (!_room.TryGetSeat(playerId, out PlayerSeat seat) || seat.IsBot) return "Unknown player.";
        if (!seat.Alive) return "You have been voted out. You can only watch now.";

        lock (_gate)
        {
            if (_room.State.Stage != Stage.Voting) return "Voting is closed.";
            if (targetId == playerId || !_room.TryGetSeat(targetId, out PlayerSeat target) || !target.Alive)
                return "Pick another player who is still in.";
            _votes[playerId] = targetId;
        }

        await UpdateProgressAsync();
        return null;
    }

    // ---------------------------------------------------------------- chat

    private async Task<string?> PostChatAsync(PlayerSeat sender, string? text, CancellationToken ct)
    {
        ChatCheck check = ChatFilter.Check(text, _opt.ChatMaxLength, out string cleaned);
        if (check != ChatCheck.Ok) return ChatFilter.Describe(check);
        if (!await _contentFilter.IsAllowedAsync(cleaned, _room.ContentPolicy, ct)) return "That message is not allowed.";

        ChatMessage message;
        lock (_gate)
        {
            if (_room.State.Stage != Stage.Discussion) return "Chat is closed right now.";

            message = new ChatMessage(Guid.NewGuid(), sender.Id, sender.Alias, sender.Color, sender.CharacterId, cleaned,
                _time.GetUtcNow().ToUnixTimeMilliseconds());
            _transcript.Add(message);
            if (_transcript.Count > _opt.TranscriptLimit) _transcript.RemoveAt(0);
        }

        await _broadcaster.ChatMessage(_room, message);
        return null;
    }

    // Must be called with the gate held.
    private IReadOnlyList<ChatLine> RecentLines() =>
        _transcript.TakeLast(_opt.BotContextLines).Select(m => new ChatLine(m.Alias, m.Text)).ToArray();

    // ---------------------------------------------------------------- the bots

    private IReadOnlyList<PlayerSeat> BotSeats() => _room.GetSeats().Where(s => s.IsBot && s.Alive).ToArray();

    private async Task<IReadOnlyList<OnboardingQuestion>> GenerateQuestionsAsync()
    {
        ContentPolicy policy = _room.ContentPolicy;
        try
        {
            IReadOnlyList<OnboardingQuestion> questions =
                await _agent.GenerateOnboardingQuestionsAsync(new QuestionRequest(policy, _opt.QuestionCount), _cts.Token);

            if (QuestionValidator.IsValid(questions, policy, _opt.QuestionCount)) return questions;
            _logger.LogWarning("Room {Room}: the agent's onboarding questions failed validation, using the built-in ones", _room.Code);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Room {Room}: could not get onboarding questions from the agent, using the built-in ones", _room.Code);
        }

        return ScriptedBotAgent.BuiltInQuestions(policy, _opt.QuestionCount);
    }

    private async Task BotOnboardingAsync(IReadOnlyList<OnboardingQuestion> questions)
    {
        string[] samples;
        lock (_gate)
            samples = _answers.Values.SelectMany(a => a).Where(a => a.Length > 0).ToArray();

        await Task.WhenAll(BotSeats().Select(async bot =>
        {
            try
            {
                IReadOnlyList<string> answers = await _agent.AnswerOnboardingAsync(
                    new OnboardingAnswerRequest(bot.Alias, _room.ContentPolicy, questions, samples), _cts.Token);

                lock (_gate)
                    _personas[bot.Id] = string.Join(" | ", answers);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The bot just plays without a persona.
                _logger.LogWarning(ex, "Room {Room}: a bot could not answer the onboarding form", _room.Code);
            }
        }));
    }

    private async Task BotChatLoopAsync(PlayerSeat bot, int round, CancellationToken ct)
    {
        try
        {
            int posted = 0;
            // Quiet turns do not use up the budget, so cap the attempts as well.
            for (int attempt = 0; attempt < _opt.BotMessagesPerRound * 3 && posted < _opt.BotMessagesPerRound; attempt++)
            {
                await Task.Delay(RandomDelay(_opt.BotChatMinDelayMs, _opt.BotChatMaxDelayMs), _time, ct);

                ChatRequest request;
                lock (_gate)
                    request = new ChatRequest(bot.Alias, _personas.GetValueOrDefault(bot.Id, ""), _room.ContentPolicy, round, RecentLines());

                string? line = await _agent.NextChatMessageAsync(request, ct);
                if (line is null) continue;

                if (await PostChatAsync(bot, line, ct) is null) posted++;
            }
        }
        catch (OperationCanceledException)
        {
            // The discussion ended.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Room {Room}: a bot's chat failed", _room.Code);
        }
    }

    private async Task BotVoteAsync(PlayerSeat bot, int round, CancellationToken ct)
    {
        try
        {
            await Task.Delay(RandomDelay(_opt.BotVoteMinDelayMs, _opt.BotVoteMaxDelayMs), _time, ct);

            VoteRequest request;
            lock (_gate)
            {
                // Bots know their teammates, so they only ever pick among the humans.
                VoteCandidate[] candidates = _room.GetSeats()
                    .Where(s => s.Alive && !s.IsBot)
                    .Select(s => new VoteCandidate(s.Id, s.Alias))
                    .ToArray();
                request = new VoteRequest(bot.Alias, _personas.GetValueOrDefault(bot.Id, ""), _room.ContentPolicy, round, candidates, RecentLines());
            }

            Guid? choice = await _agent.CastVoteAsync(request, ct);
            if (choice is { } id && request.Candidates.All(c => c.Id != id)) choice = null;

            lock (_gate)
            {
                if (_room.State.Stage != Stage.Voting) return;
                _votes[bot.Id] = choice;
            }

            await UpdateProgressAsync();
        }
        catch (OperationCanceledException)
        {
            // The vote ended before this bot cast its own.
        }
        catch (Exception ex)
        {
            // A bot that cannot decide abstains, so the vote is not held up waiting for it.
            _logger.LogWarning(ex, "Room {Room}: a bot's vote failed, it abstains", _room.Code);

            lock (_gate)
            {
                if (_room.State.Stage == Stage.Voting) _votes[bot.Id] = null;
            }

            await UpdateProgressAsync();
        }
    }

    private static TimeSpan RandomDelay(int minMs, int maxMs) =>
        TimeSpan.FromMilliseconds(maxMs <= minMs ? minMs : RandomNumberGenerator.GetInt32(minMs, maxMs + 1));

    // ---------------------------------------------------------------- voting

    /// <summary>
    /// Plurality wins. A tie (or no votes) eliminates nobody.
    /// </summary>
    private RevealData TallyVotes()
    {
        lock (_gate)
        {
            Dictionary<Guid, PlayerSeat> seats = _room.GetSeats().ToDictionary(s => s.Id);
            Dictionary<Guid, int> counts = [];

            foreach (Guid? target in _votes.Values)
            {
                if (target is not { } id || !seats.TryGetValue(id, out PlayerSeat seat) || !seat.Alive) continue;
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }

            VoteCount[] tally = counts
                .Select(kv => new VoteCount(seats[kv.Key].Alias, kv.Value))
                .OrderByDescending(v => v.Votes)
                .ThenBy(v => v.Alias, StringComparer.Ordinal)
                .ToArray();

            int max = counts.Count == 0 ? 0 : counts.Values.Max();
            KeyValuePair<Guid, int>[] top = counts.Where(kv => kv.Value == max).ToArray();

            if (max > 0 && top.Length == 1)
            {
                PlayerSeat eliminated = seats[top[0].Key];
                _room.Eliminate(eliminated.Id);
                return new RevealData(eliminated.ToInfo() with { Alive = false }, eliminated.IsBot, tally);
            }

            return new RevealData(null, null, tally);
        }
    }
}
