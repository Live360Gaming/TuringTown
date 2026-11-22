using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using TuringTown.Models;
using TuringTown.Services.Ai;
using TuringTown.Services.Storage;

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
public sealed class TuringTownEngine : IGameEngine
{
    private sealed class ContentUnavailableException : Exception { }

    private readonly Room _room;
    private readonly IBotAgent _agent;
    private readonly IQuestionGenerator _questionGenerator;
    private readonly IBotMemoryStore _memory;
    private readonly IGameArchive _archive;
    private readonly Guid _gameId = Guid.NewGuid();
    private readonly IContentFilter _contentFilter;
    private readonly IRoomBroadcaster _broadcaster;
    private readonly GameClock _clock;
    private readonly ChatLog _chat;
    private readonly TuringTownOptions _opt;
    private readonly ILogger _logger;
    private readonly Action<TuringTownEngine>? _onFinished;
    private readonly NightContext? _night;
    private readonly CancellationTokenSource _cts = new();

    // Writes stages to the room and pauses or resumes the game, never out of order.
    private readonly GameStages _stages;

    // Everything below is guarded by _gate.
    private readonly object _gate = new();
    private readonly Dictionary<Guid, OnboardingAnswer[]> _answers = [];
    private readonly Dictionary<Guid, PersonaAnswer[]> _personas = [];
    private readonly Dictionary<Guid, OnboardingAnswer[]> _botAnswers = [];
    private readonly List<RoundOutcome> _history = [];
    private readonly List<ArchivedLine> _archiveChat = [];
    private readonly Dictionary<Guid, List<string>> _ownLines = [];
    private readonly Dictionary<Guid, double> _latencyMs = [];
    private StyleProfile _style = StyleProfile.Empty;
    private long _roundStartedMs;
    private int _humanPoints, _aiPoints;
    private readonly Dictionary<Guid, Guid?> _votes = [];
    private readonly HashSet<Guid> _unmasked = [];

    // Lines that @mentioned a bot and that it has not answered yet, and a signal that wakes the bot when one arrives.
    private readonly Dictionary<Guid, List<ChatLine>> _pendingMentions = [];
    private readonly Dictionary<Guid, TaskCompletionSource> _mentionSignals = [];

    // Bots that were mentioned and should answer soon (a mention the bot "ignores" stays in _pendingMentions only).
    private readonly HashSet<Guid> _wake = [];
    private IReadOnlyList<OnboardingQuestion> _questions = [];
    private TaskCompletionSource _stageDone = GameClock.NewSignal();

    private readonly TaskCompletionSource _tutorialDone = GameClock.NewSignal();
    private readonly TaskCompletionSource _resultsDone = GameClock.NewSignal();
    private Task? _loop;
    private Task _archiveTask = Task.CompletedTask;

    public TuringTownEngine(
        Room room,
        IBotAgent agent,
        IQuestionGenerator questionGenerator,
        IBotMemoryStore memory,
        IGameArchive archive,
        IContentFilter contentFilter,
        IRoomBroadcaster broadcaster,
        TimeProvider time,
        TuringTownOptions options,
        ILogger logger,
        Action<TuringTownEngine>? onFinished = null,
        NightContext? night = null)
    {
        _room = room;
        _agent = agent;
        _questionGenerator = questionGenerator;
        _memory = memory;
        _archive = archive;
        _contentFilter = contentFilter;
        _broadcaster = broadcaster;
        _opt = options;
        _clock = new GameClock(time, _cts.Token);
        _stages = new GameStages(room, broadcaster, _clock, GameIds.TuringTown, _cts.Token);
        _chat = new ChatLog(options.TranscriptLimit, options.ChatMinIntervalMs);
        _logger = logger;
        _onFinished = onFinished;
        _night = night;
    }

    /// <summary>
    /// 1 bot for 2-4 humans, 2 bots for 5 or more.
    /// </summary>
    public static int BotCountFor(int humans) => humans <= 4 ? 1 : 2;

    /// <summary>Identifies this game in the bot memory store.</summary>
    internal Guid GameId => _gameId;

    public string GameKey => GameIds.TuringTown;

    /// <summary>
    /// Completes when a game started with <see cref="Start"/> has ended and the room is back in the lobby.
    /// </summary>
    public Task Completion => _loop ?? Task.CompletedTask;

    /// <summary>
    /// Plays this one game on its own (outside a game night, as the tests do) and puts the room back in the lobby
    /// at the end. The room must already be out of the lobby (see <see cref="Room.TryStart"/>).
    /// </summary>
    public void Start() => _loop = Task.Run(async () =>
    {
        await PlayAsync();
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
    });

    /// <summary>
    /// Stops the game early (the host closed the room). The loop winds down and calls <c>onFinished</c>, which disposes the engine.
    /// </summary>
    public void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    // ---------------------------------------------------------------- the game loop

    public async Task<GameOutcome> PlayAsync()
    {
        string? won = null;
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
            won = winner;
        }
        catch (OperationCanceledException)
        {
            // The engine was disposed.
        }
        catch (ContentUnavailableException)
        {
            _logger.LogWarning("Room {Room}: could not prepare acceptable game content", _room.Code);
            await _broadcaster.GameError(_room, "Could not prepare game content. Please try starting again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Game in room {Room} failed", _room.Code);
        }
        finally
        {
            await _archiveTask;
            await ClearMemoryAsync();
        }

        if (won is null) return GameOutcome.Failed;
        return new GameOutcome(true, won, new Dictionary<Guid, int>(), GetUnmasked(),
            new TeamTotals(_humanPoints, _aiPoints));
    }

    // A bot's memory only lives for one game.
    private async Task ClearMemoryAsync()
    {
        try
        {
            await _memory.DeleteGameAsync(_gameId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear the bot memory of room {Room}", _room.Code);
        }
    }

    private async Task RunTutorialAsync()
    {
        TimeSpan max = TimeSpan.FromSeconds(_opt.TutorialMaxSeconds);
        await EnterStageAsync(Stage.Tutorial, 0, 0, max, null);
        await _clock.WaitAsync(max, _tutorialDone.Task);
    }

    private async Task RunOnboardingAsync(IReadOnlyList<OnboardingQuestion> questions)
    {
        TimeSpan duration = TimeSpan.FromSeconds(_opt.OnboardingSeconds);
        lock (_gate)
        {
            _questions = questions;
            _answers.Clear();
            _stageDone = GameClock.NewSignal();
        }

        await EnterStageAsync(Stage.Onboarding, 0, 0, duration, new OnboardingData(questions));
        await UpdateProgressAsync();
        await _clock.WaitAsync(duration, CurrentSignal());

        // The bots answer last, so they can match how the humans actually wrote.
        // Answers stay private for the whole game: they only feed the bots' personas and style, and are never
        // sent to any client.
        await BotOnboardingAsync(questions);
    }

    private async Task RunDiscussionAsync(int round)
    {
        TimeSpan duration = TimeSpan.FromSeconds(_opt.DiscussionSeconds);

        // Mentions from an earlier round are old news.
        lock (_gate)
        {
            _pendingMentions.Clear();
            _mentionSignals.Clear();
            _wake.Clear();
            _roundStartedMs = _clock.ToUnixMs(_clock.UtcNow);
        }

        await EnterStageAsync(Stage.Discussion, round, 0, duration, null);

        using CancellationTokenSource stage = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        Task[] bots = BotSeats().Select(b => BotChatLoopAsync(b, round, stage.Token)).ToArray();

        await _clock.WaitAsync(duration);

        stage.Cancel();
        await Task.WhenAll(bots);
    }

    private async Task RunVotingAndRevealAsync(int round)
    {
        TimeSpan duration = TimeSpan.FromSeconds(_opt.VotingSeconds);
        lock (_gate)
        {
            _votes.Clear();
            _stageDone = GameClock.NewSignal();
        }

        await EnterStageAsync(Stage.Voting, round, 0, duration, null);

        using CancellationTokenSource stage = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        Task[] bots = BotSeats().Select(b => BotVoteAsync(b, round, stage.Token)).ToArray();

        await UpdateProgressAsync();
        await _clock.WaitAsync(duration, CurrentSignal());

        stage.Cancel();
        await Task.WhenAll(bots);

        RevealData reveal = TallyVotes();
        RecordRound(round, reveal);

        // Each round is worth double the one before, so a late vote matters most.
        if (reveal.WasBot is { } removedBot)
        {
            int value = _opt.RoundPoints << Math.Min(round - 1, 20);
            if (removedBot) _humanPoints += value;
            else _aiPoints += value;
        }

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
        await _clock.WaitAsync(revealTime);
    }

    /// <summary>
    /// Remembers what the Reveal showed (public information only), then lets each living bot condense its notes in
    /// the background, so the Reveal is never held up.
    /// </summary>
    private void RecordRound(int round, RevealData reveal)
    {
        IReadOnlyList<ChatLine> transcript;
        lock (_gate)
        {
            _history.Add(new RoundOutcome(round, reveal.Eliminated?.Alias, reveal.WasBot, reveal.Tally));
            // The whole room saw the reveal, so the AI team may remember it for the rest of the night.
            _night?.AddHighlight(reveal.Eliminated is { } gone
                ? $"Turing Town round {round}: {gone.Alias} was voted out and was {(reveal.WasBot == true ? "an AI" : "a human")}."
                : $"Turing Town round {round}: the vote tied and nobody was voted out.");
            long since = _roundStartedMs;
            transcript = _chat.Snapshot().Where(m => m.SentAtUtcMs >= since).Select(m => new ChatLine(m.Alias, m.Text)).ToArray();
        }

        foreach (PlayerSeat bot in BotSeats())
        {
            BotContext context;
            lock (_gate)
                context = BuildContext(bot, round);

            _ = Task.Run(async () =>
            {
                try
                {
                    await _agent.EndRoundAsync(new RoundEndRequest(context, _room.ContentPolicy, transcript), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // The game ended.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Room {Room}: a bot could not write its round notes", _room.Code);
                }
            });
        }
    }

    /// <summary>
    /// Hands the finished game to the (opt-in) transcript archive. Aliases and game text only: no onboarding answers, no
    /// ids, no tokens. It runs in the background so a slow store never holds up the Results screen.
    /// </summary>
    private void ArchiveInBackground(string winner, IReadOnlyList<RoleReveal> roles)
    {
        GameArchive archive;
        lock (_gate)
        {
            archive = new GameArchive(
                _gameId,
                _clock.UtcNow,
                winner,
                roles.Select(r => new ArchivedPlayer(r.Alias, r.WasBot ? "Bot" : "Human")).ToArray(),
                _questions.Select(q => new ArchivedQuestion(q.Text, q.Kind, q.Options.Select(o => o.Text).ToArray())).ToArray(),
                _history.ToArray(),
                _archiveChat.ToArray());
        }

        _archiveTask = Task.Run(async () =>
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            try
            {
                await _archive.ArchiveAsync(archive, timeout.Token);
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(ex, "Room {Room}: archiving the finished game was canceled or timed out", _room.Code);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Room {Room}: could not archive the finished game", _room.Code);
            }
        });
    }

    private async Task RunResultsAsync(string winner)
    {
        IReadOnlyList<RoleReveal> roles = _room.GetSeats().Select(s => new RoleReveal(s.Id, s.Alias, s.IsBot)).ToArray();
        lock (_gate)
        {
            foreach (RoleReveal role in roles.Where(r => r.WasBot))
                _unmasked.Add(role.PlayerId);
        }

        ArchiveInBackground(winner, roles);

        TimeSpan duration = TimeSpan.FromSeconds(_opt.ResultsSeconds);
        await EnterStageAsync(Stage.Results, 0, 0, duration, new ResultsData(winner, roles));
        await _clock.WaitAsync(duration, _resultsDone.Task);
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

    private Task EnterStageAsync(Stage stage, int round, int step, TimeSpan? duration, object? data) =>
        _stages.EnterAsync(stage, round, step, duration, data);

    private Task CurrentSignal()
    {
        lock (_gate)
            return _stageDone.Task;
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
        return _chat.Snapshot();
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

    /// <summary>
    /// The host pauses (or resumes) the game. While paused the stage clock stands still, the bots stay quiet and
    /// players cannot chat or vote. The state carries <see cref="GameState.PausedAtUtcMs"/> so every screen can show it,
    /// and on resume the deadline moves back by however long the pause lasted.
    /// </summary>
    public Task<string?> SetPausedAsync(bool paused) => _stages.SetPausedAsync(paused);

    public bool IsPaused => _clock.IsPaused;

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

    public string? EndResults()
    {
        if (_room.State.Stage != Stage.Results) return "The game is not over yet.";
        _resultsDone.TrySetResult();
        return null;
    }

    public async Task<string?> SubmitOnboardingAsync(Guid playerId, IReadOnlyList<OnboardingAnswer?>? answers)
    {
        if (!_room.TryGetSeat(playerId, out PlayerSeat seat) || seat.IsBot) return "Unknown player.";
        if (_room.State.Stage != Stage.Onboarding) return "The form is closed.";

        IReadOnlyList<OnboardingQuestion> questions;
        lock (_gate)
            questions = _questions;

        // Checked against the questions that were asked: unknown or missing questions, wrong option counts and
        // unknown options, free text on a choice question, over-long or leaky text.
        string? error = OnboardingAnswerValidator.Validate(questions, answers, _opt.AnswerMaxLength, out OnboardingAnswer[] cleaned);
        if (error is not null) return error;

        // Only free text can carry anything unwanted; a choice is just option ids from the server's own list.
        foreach (OnboardingAnswer answer in cleaned)
        {
            if (answer.Text is { Length: > 0 } text && !await _contentFilter.IsAllowedAsync(text, _room.ContentPolicy, _cts.Token))
                return "That answer is not allowed.";
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
        if (_clock.IsPaused) return "The game is paused.";

        if (!_chat.TryTakeTurn(playerId, _clock.UtcNow)) return "Slow down a little.";

        return await PostChatAsync(seat, text, _cts.Token);
    }

    /// <summary>A human started or stopped typing. Ignored unless they could send a message right now.</summary>
    public async Task SetTypingAsync(Guid playerId, bool typing)
    {
        if (!_room.TryGetSeat(playerId, out PlayerSeat seat) || seat.IsBot || !seat.Alive) return;
        if (_room.State.Stage != Stage.Discussion || (typing && _clock.IsPaused)) return;
        await _broadcaster.Typing(_room, playerId, typing);
    }

    public async Task<string?> CastVoteAsync(Guid playerId, Guid targetId)
    {
        if (!_room.TryGetSeat(playerId, out PlayerSeat seat) || seat.IsBot) return "Unknown player.";
        if (!seat.Alive) return "You have been voted out. You can only watch now.";
        if (_clock.IsPaused) return "The game is paused.";

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
        // A bot whose answer arrives during a pause holds it until the game resumes.
        if (sender.IsBot) await _clock.WhenRunning(ct);
        if (sender.IsBot && text is not null) text = PromptText.NormalizeDashes(text);
        ChatCheck check = ChatFilter.Check(text, _opt.ChatMaxLength, out string cleaned);
        if (check != ChatCheck.Ok) return ChatFilter.Describe(check);
        if (!await _contentFilter.IsAllowedAsync(cleaned, _room.ContentPolicy, ct)) return "That message is not allowed.";

        IReadOnlyList<PlayerSeat> mentioned =
            MentionParser.Find(cleaned, _room.GetSeats().Where(s => s.Id != sender.Id), _opt.MaxMentionsPerMessage);

        ChatMessage message;
        lock (_gate)
        {
            if (_room.State.Stage != Stage.Discussion) return "Chat is closed right now.";

            message = new ChatMessage(Guid.NewGuid(), sender.Id, sender.Alias, sender.Color, sender.CharacterId, cleaned,
                _clock.ToUnixMs(_clock.UtcNow), mentioned.Select(s => s.Id).ToArray());
            _chat.Add(message);
            if (_archive is not NullGameArchive)
                _archiveChat.Add(new ArchivedLine(sender.Alias, cleaned,
                    DateTimeOffset.FromUnixTimeMilliseconds(message.SentAtUtcMs), sender.IsBot, _room.State.Round));

            // A bot that was spoken to will answer, rather than waiting for its usual turn.
            foreach (PlayerSeat seat in mentioned.Where(s => s.IsBot))
            {
                if (!_pendingMentions.TryGetValue(seat.Id, out List<ChatLine>? pending))
                    _pendingMentions[seat.Id] = pending = [];
                pending.Add(new ChatLine(sender.Alias, cleaned));

                // Not every question gets an instant answer: now and then the bot only gets to it on its next normal
                // turn (the line stays pending and is handed over then).
                if (RandomNumberGenerator.GetInt32(100) < _opt.BotMentionIgnorePercent) continue;

                _wake.Add(seat.Id);
                if (_mentionSignals.TryGetValue(seat.Id, out TaskCompletionSource? signal)) signal.TrySetResult();
            }
        }

        await _broadcaster.ChatMessage(_room, message);
        return null;
    }

    private IReadOnlyList<ChatLine> RecentLines() =>
        _chat.Recent(_opt.BotContextLines).Select(m => new ChatLine(m.Alias, m.Text)).ToArray();

    // ---------------------------------------------------------------- the bots

    private IReadOnlyList<PlayerSeat> BotSeats() => _room.GetSeats().Where(s => s.IsBot && s.Alive).ToArray();

    private async Task<IReadOnlyList<OnboardingQuestion>> GenerateQuestionsAsync()
    {
        ContentPolicy policy = _room.ContentPolicy;
        try
        {
            IReadOnlyList<OnboardingQuestion> questions =
                await _questionGenerator.GenerateAsync(new QuestionRequest(policy, _opt.Mix), _cts.Token);

            string? problem = QuestionValidator.Problem(questions, policy, _opt.Mix);
            if (problem is null && !await QuestionsAllowedAsync(questions, policy, _cts.Token))
                problem = "blocked by the content filter";
            if (problem is null)
            {
                problem = "a mature room got no mature question and no built-in replacement passed";
                if (policy.FamilyFriendly || questions.Count == 0 || questions.Any(q => q.Mature))
                    return questions;

                OnboardingQuestion[] adjusted = questions.ToArray();
                using CancellationTokenSource toneSearch = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                toneSearch.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    Dictionary<string, bool> checkedTexts = [];
                    for (int i = 0; i < adjusted.Length; i++)
                    {
                        foreach (OnboardingQuestion candidate in ScriptedBotAgent.QuestionCandidates(policy, adjusted[i].Kind, matureOnly: true))
                        {
                            OnboardingQuestion replacement = candidate with { Id = adjusted[i].Id };
                            if (!QuestionValidator.IsValid([replacement], policy, 1) || !await QuestionsAllowedAsync([replacement], policy, toneSearch.Token, checkedTexts)) continue;
                            adjusted[i] = replacement;
                            AiMetrics.Fallbacks.Add(1, new KeyValuePair<string, object?>("kind", "question_tone"));
                            return adjusted;
                        }
                    }
                }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested) { }
            }
            AiMetrics.QuestionFailures.Add(1, new KeyValuePair<string, object?>("reason", "validation"));
            _logger.LogWarning("Room {Room}: the generated onboarding questions failed validation ({Problem}), using the built-in ones", _room.Code, problem);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AiMetrics.QuestionFailures.Add(1, new KeyValuePair<string, object?>("reason", "error"));
            _logger.LogWarning(ex, "Room {Room}: could not get onboarding questions from the agent, using the built-in ones", _room.Code);
        }

        AiMetrics.Fallbacks.Add(1, new KeyValuePair<string, object?>("kind", "question_validation"));
        QuestionKind?[] matureKinds = policy.FamilyFriendly || _opt.Mix.Total == 0
            ? [null]
            : new[] { (QuestionKind.Text, _opt.Mix.Text), (QuestionKind.SingleChoice, _opt.Mix.SingleChoice), (QuestionKind.MultiChoice, _opt.Mix.MultiChoice) }
                .Where(x => x.Item2 > 0).Select(x => (QuestionKind?)x.Item1).ToArray();
        using CancellationTokenSource search = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        search.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Dictionary<string, bool> checkedTexts = [];
                foreach (QuestionKind? matureKind in matureKinds)
                {
                    IReadOnlyList<OnboardingQuestion>? fallback = await BuiltInFormAsync(policy, matureKind, search.Token, checkedTexts);
                    if (fallback is not null) return fallback;
                }
            }
        }
        catch (OperationCanceledException) when (!_cts.IsCancellationRequested) { }
        AiMetrics.QuestionFailures.Add(1, new KeyValuePair<string, object?>("reason", "exhausted"));
        throw new ContentUnavailableException();
    }

    private async Task<IReadOnlyList<OnboardingQuestion>?> BuiltInFormAsync(
        ContentPolicy policy, QuestionKind? matureKind, CancellationToken ct, Dictionary<string, bool> checkedTexts)
    {
        List<OnboardingQuestion> result = [];
        foreach ((QuestionKind kind, int count) in new[]
                 {
                     (QuestionKind.Text, _opt.Mix.Text),
                     (QuestionKind.SingleChoice, _opt.Mix.SingleChoice),
                     (QuestionKind.MultiChoice, _opt.Mix.MultiChoice)
                 })
        {
            if (count <= 0) continue;
            List<OnboardingQuestion> accepted = [];
            IReadOnlyList<OnboardingQuestion> candidates = ScriptedBotAgent.QuestionCandidates(policy, kind, kind == matureKind);
            foreach (OnboardingQuestion candidate in candidates)
            {
                OnboardingQuestion question = candidate with { Id = "candidate" };
                if (QuestionValidator.IsValid([question], policy, 1) && await QuestionsAllowedAsync([question], policy, ct, checkedTexts))
                    accepted.Add(question);
                if (accepted.Count >= count) break;
            }

            if (accepted.Count == 0) return null;
            for (int i = 0; i < count; i++) result.Add(accepted[i % accepted.Count]);
        }

        OnboardingQuestion[] form = result.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue))
            .Select((q, i) => q with { Id = $"q{i + 1}" }).ToArray();
        return QuestionValidator.IsValid(form, policy, _opt.Mix) ? form : null;
    }

    /// <summary>
    /// Generated text is shown to every player, so it passes the same content filter as chat.
    /// </summary>
    private async Task<bool> QuestionsAllowedAsync(
        IReadOnlyList<OnboardingQuestion> questions, ContentPolicy policy, CancellationToken ct, Dictionary<string, bool>? checkedTexts = null)
    {
        async Task<bool> Allowed(string text)
        {
            ct.ThrowIfCancellationRequested();
            if (checkedTexts is not null && checkedTexts.TryGetValue(text, out bool cached)) return cached;
            bool allowed = await _contentFilter.IsAllowedAsync(text, policy, ct);
            if (checkedTexts is not null) checkedTexts[text] = allowed;
            return allowed;
        }

        foreach (OnboardingQuestion q in questions)
        {
            if (!await Allowed(q.Text)) return false;
            foreach (QuestionOption option in q.Options)
                if (!await Allowed(option.Text)) return false;
        }
        return true;
    }

    private async Task BotOnboardingAsync(IReadOnlyList<OnboardingQuestion> questions)
    {
        // What the humans answered, with no ids or names: free text for style, option ids for what they pick.
        OnboardingAnswer[] samples;
        lock (_gate)
        {
            samples = _answers.Values.SelectMany(a => a).ToArray();
            _style = StyleProfile.From(questions, samples);
        }

        await Task.WhenAll(BotSeats().Select(async bot =>
        {
            try
            {
                BotContext context;
                lock (_gate)
                    context = BuildContext(bot, 0);

                IReadOnlyList<OnboardingAnswer> given;
                try
                {
                    given = await _agent.AnswerOnboardingAsync(
                        new OnboardingAnswerRequest(bot.Alias, _room.ContentPolicy, questions, samples, context), _cts.Token);
                }
                catch (Exception ex) when (!_cts.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Room {Room}: a bot's onboarding agent failed, using scripted answers", _room.Code);
                    AiMetrics.Fallbacks.Add(1, new KeyValuePair<string, object?>("kind", "onboarding"));
                    given = questions.Select(q => ScriptedBotAgent.ScriptedAnswer(q, samples, bot.Id)).ToArray();
                }

                // The same checks as for a human. An invalid answer is replaced, so a bot always has a full persona.
                // A near-copy of a human's answer is replaced too: if it came up in chat, twins would stand out.
                OnboardingAnswer[] answers = new OnboardingAnswer[questions.Count];
                for (int i = 0; i < answers.Length; i++)
                {
                    string id = questions[i].Id;
                    IEnumerable<string?> humanTexts = samples.Where(a => a.QuestionId == id).Select(a => a.Text);
                    OnboardingAnswer? answer = given?.FirstOrDefault(a => a?.QuestionId == id);
                    if (answer?.Text is { } authored)
                        answer = answer with { Text = PromptText.NormalizeDashes(authored) };
                    bool ok = OnboardingAnswerValidator.ValidateOne(questions[i], i + 1, answer, _opt.AnswerMaxLength, out OnboardingAnswer cleaned) is null
                              && !OnboardingAnswerValidator.IsNearCopy(cleaned.Text, humanTexts)
                              && (cleaned.Text is not { Length: > 0 } text || await _contentFilter.IsAllowedAsync(text, _room.ContentPolicy, _cts.Token));

                    if (ok)
                    {
                        answers[i] = cleaned;
                        continue;
                    }

                    AiMetrics.Fallbacks.Add(1, new KeyValuePair<string, object?>("kind", "onboarding_validation"));
                    bool found = false;
                    for (int attempt = 0; attempt < 8; attempt++)
                    {
                        OnboardingAnswer replacement = ScriptedBotAgent.ScriptedAnswer(questions[i], samples, bot.Id);
                        if (replacement.Text is { } fallbackText)
                            replacement = replacement with { Text = PromptText.NormalizeDashes(fallbackText) };
                        if (OnboardingAnswerValidator.ValidateOne(questions[i], i + 1, replacement, _opt.AnswerMaxLength, out cleaned) is not null
                            || OnboardingAnswerValidator.IsNearCopy(cleaned.Text, humanTexts)
                            || (cleaned.Text is { Length: > 0 } checkedText && !await _contentFilter.IsAllowedAsync(checkedText, _room.ContentPolicy, _cts.Token)))
                            continue;
                        answers[i] = cleaned;
                        found = true;
                        break;
                    }
                    if (!found) throw new ContentUnavailableException();
                }

                lock (_gate)
                {
                    _personas[bot.Id] = ToPersona(questions, answers);
                    _botAnswers[bot.Id] = answers;
                }
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                throw;
            }
            catch (ContentUnavailableException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Room {Room}: a bot could not answer the onboarding form", _room.Code);
                throw new ContentUnavailableException();
            }
        }));
    }

    /// <summary>
    /// A bot's own answers written out with the question and the chosen option texts, for its prompt.
    /// </summary>
    private static PersonaAnswer[] ToPersona(IReadOnlyList<OnboardingQuestion> questions, IReadOnlyList<OnboardingAnswer> answers) =>
        questions.Select(q =>
        {
            OnboardingAnswer? a = answers.FirstOrDefault(x => x.QuestionId == q.Id);
            return q.Kind == QuestionKind.Text
                ? new PersonaAnswer(q.Text, q.Kind, a?.Text ?? "", [])
                : new PersonaAnswer(q.Text, q.Kind, null, q.Options.Where(o => a?.OptionIds?.Contains(o.Id) == true).Select(o => o.Text).ToArray());
        }).ToArray();

    /// <summary>
    /// The persona as one line of text.
    /// </summary>
    private static string PersonaText(IReadOnlyList<PersonaAnswer> persona) =>
        string.Join(" | ", persona
            .Select(p => p.Kind == QuestionKind.Text ? p.Text ?? "" : string.Join(", ", p.ChosenOptions))
            .Where(v => v.Length > 0));

    /// <summary>
    /// Everything one bot is allowed to know. Call with the gate held. It carries the bot's own persona and lines and
    /// what the Reveal showed everyone: <b>never</b> a role for any other player, nor how many bots there are.
    /// </summary>
    private BotContext BuildContext(PlayerSeat bot, int round)
    {
        IReadOnlyList<PlayerSeat> seats = _room.GetSeats();
        ChatLine[] own = _ownLines.TryGetValue(bot.Id, out List<string>? lines)
            ? lines.TakeLast(_opt.BotOwnLinesContext).Select(t => new ChatLine(bot.Alias, t)).ToArray()
            : [];

        return new BotContext(
            new BotKey(_gameId, bot.Id),
            bot.Alias,
            _personas.GetValueOrDefault(bot.Id, []),
            round,
            seats.Where(s => s.Alive && s.Id != bot.Id).Select(s => s.Alias).ToArray(),
            _history.ToArray(),
            own,
            // Round 0 is the bot's own form, which may model the humans' answers. After that, style numbers only.
            round == 0 ? _style : _style.WithoutAnswers(),
            _opt.ChatMaxLength)
        {
            Night = _night?.Snapshot()
        };
    }

    private async Task BotChatLoopAsync(PlayerSeat bot, int round, CancellationToken ct)
    {
        try
        {
            int posted = 0;
            int attempt = 0;
            // Quiet turns do not use up the budget, so cap the attempts as well.
            while (attempt < _opt.BotMessagesPerRound * 3 && posted < _opt.BotMessagesPerRound)
            {
                // Keeping pace with the room: once this bot has said as much as the humans have, it waits for them
                // (unless someone is speaking to it).
                if (posted >= PaceBudget() && !HasWakeMention(bot))
                {
                    await _clock.Delay(TimeSpan.FromMilliseconds(Math.Max(250, _opt.BotChatMinDelayMs)), ct);
                    continue;
                }

                attempt++;
                await WaitForTurnAsync(bot, ct);

                ChatRequest request;
                lock (_gate)
                {
                    // Hand over what was said to the bot since it last spoke, and start collecting afresh.
                    ChatLine[] mentions = [];
                    if (_pendingMentions.TryGetValue(bot.Id, out List<ChatLine>? pending))
                    {
                        mentions = pending.ToArray();
                        pending.Clear();
                    }

                    // Aliases only. A bot is never told who is human and who is a bot.
                    BotContext context = BuildContext(bot, round);
                    request = new ChatRequest(bot.Alias, PersonaText(context.Persona), _room.ContentPolicy, round,
                        RecentLines(), mentions, context.AlivePlayers, context);
                }

                // Shown as "typing" while the bot thinks and types, exactly like a human at the keyboard.
                await _broadcaster.Typing(_room, bot.Id, true);
                string? line;
                string? error;
                try
                {
                    DateTimeOffset asked = _clock.UtcNow;
                    line = await _agent.NextChatMessageAsync(request, ct);
                    TimeSpan thought = _clock.UtcNow - asked;
                    NoteLatency(bot, thought);
                    if (line is null) continue;
                    line = PromptText.NormalizeDashes(line);

                    // Typing takes time too. The time the agent already took counts towards it.
                    TimeSpan typing = TimeSpan.FromMilliseconds(TypingTargetMs(bot.Id, line.Length, _opt.BotTypingMsPerChar, _opt.BotTypingMaxMs, Random.Shared.NextDouble())) - thought;
                    if (typing > TimeSpan.Zero) await _clock.Delay(typing, ct);

                    error = await PostChatAsync(bot, line, ct);
                }

                finally
                {
                    // Also on a quiet turn or when the discussion ends. The client drops it when the message arrives too.
                    await _broadcaster.Typing(_room, bot.Id, false);
                }

                if (error is null)
                {
                    posted++;
                    lock (_gate)
                    {
                        if (!_ownLines.TryGetValue(bot.Id, out List<string>? lines)) _ownLines[bot.Id] = lines = [];
                        lines.Add(line.Trim());
                    }
                }
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

    /// <summary>
    /// How long a bot "types" a line of <paramref name="length"/> characters: <paramref name="msPerChar"/> per character,
    /// scaled by a fixed per-bot speed and a little random variation, and never more than <paramref name="capMs"/>.
    /// </summary>
    internal static int TypingTargetMs(Guid botId, int length, int msPerChar, int capMs, double variation)
    {
        uint seed = BitConverter.ToUInt32(botId.ToByteArray(), 0);
        double speed = 0.85 + seed % 31 / 100.0;
        double jitter = 0.9 + Math.Clamp(variation, 0, 1) * 0.2;
        return (int)Math.Clamp(length * (double)msPerChar * speed * jitter, 0, Math.Max(0, capMs));
    }

    /// <summary>
    /// How many lines this bot may have said by now. Normally the per-round budget; with <see cref="TuringTownOptions.BotPaceToHumans"/>
    /// it follows the humans: about 1.3x what a living human has said this round, and at least one.
    /// </summary>
    private int PaceBudget()
    {
        if (_opt.BotPaceToHumans != true) return _opt.BotMessagesPerRound;

        lock (_gate)
        {
            IReadOnlyList<PlayerSeat> seats = _room.GetSeats();
            HashSet<Guid> humans = seats.Where(s => !s.IsBot).Select(s => s.Id).ToHashSet();
            int living = Math.Max(1, seats.Count(s => !s.IsBot && s.Alive));
            int lines = _chat.Snapshot().Count(m => m.SentAtUtcMs >= _roundStartedMs && humans.Contains(m.PlayerId));
            return Math.Clamp((int)Math.Ceiling(lines / (double)living * 1.3), 1, _opt.BotMessagesPerRound);
        }
    }

    private bool HasWakeMention(PlayerSeat bot)
    {
        lock (_gate)
            return _wake.Contains(bot.Id);
    }

    private void NoteLatency(PlayerSeat bot, TimeSpan took)
    {
        lock (_gate)
        {
            double ms = took.TotalMilliseconds;
            _latencyMs[bot.Id] = _latencyMs.TryGetValue(bot.Id, out double before) ? (before + ms) / 2 : ms;
        }
    }

    /// <summary>
    /// A pause shortened by how long this bot's agent usually takes to answer, so the line lands about when the
    /// pause says it should rather than that much later.
    /// </summary>
    private TimeSpan ShortenedBy(TimeSpan pause, PlayerSeat bot)
    {
        double ms;
        lock (_gate)
            ms = _latencyMs.GetValueOrDefault(bot.Id);

        TimeSpan result = pause - TimeSpan.FromMilliseconds(ms);
        return result < TimeSpan.Zero ? TimeSpan.Zero : result;
    }

    /// <summary>
    /// Waits for the bot's next turn: a random, human-like pause, or a shorter one if somebody @mentions it, so a
    /// bot that is spoken to usually answers soon after instead of ignoring the question.
    /// </summary>
    private async Task WaitForTurnAsync(PlayerSeat bot, CancellationToken ct)
    {
        TaskCompletionSource signal;
        bool alreadyMentioned;
        lock (_gate)
        {
            alreadyMentioned = _wake.Contains(bot.Id);
            signal = _mentionSignals[bot.Id] = GameClock.NewSignal();
        }

        if (!alreadyMentioned)
        {
            using CancellationTokenSource local = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task idle = _clock.Delay(ShortenedBy(GameClock.RandomDelay(_opt.BotChatMinDelayMs, _opt.BotChatMaxDelayMs), bot), local.Token);

            Task first = await Task.WhenAny(idle, signal.Task);
            local.Cancel();
            ct.ThrowIfCancellationRequested();

            try
            {
                await idle;
            }
            catch (OperationCanceledException)
            {
                // We cancelled the idle wait ourselves because the bot was mentioned.
            }

            if (first == idle) return;
        }

        lock (_gate)
            _wake.Remove(bot.Id);

        // Someone spoke to this bot: reply after a short pause, like a person reading and typing.
        await _clock.Delay(ShortenedBy(GameClock.RandomDelay(_opt.BotMentionReplyMinDelayMs, _opt.BotMentionReplyMaxDelayMs), bot), ct);
    }

    private async Task BotVoteAsync(PlayerSeat bot, int round, CancellationToken ct)
    {
        try
        {
            await _clock.Delay(GameClock.RandomDelay(_opt.BotVoteMinDelayMs, _opt.BotVoteMaxDelayMs), ct);

            VoteRequest request;
            lock (_gate)
            {
                // A bot is never told who is human and who is a bot, not even its teammate: it may vote for any
                // living player but itself, and may well pick the other bot without knowing.
                VoteCandidate[] candidates = _room.GetSeats()
                    .Where(s => s.Alive && s.Id != bot.Id)
                    .Select(s => new VoteCandidate(s.Id, s.Alias))
                    .ToArray();
                BotContext context = BuildContext(bot, round);
                request = new VoteRequest(bot.Alias, PersonaText(context.Persona), _room.ContentPolicy, round, candidates, RecentLines(), context);
            }

            Guid? choice = await _agent.CastVoteAsync(request, ct);
            if (choice is { } id && request.Candidates.All(c => c.Id != id)) choice = null;
            await _clock.WhenRunning(ct);

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

    // ---------------------------------------------------------------- voting

    /// <summary>
    /// Plurality wins. A tie (or no votes) eliminates nobody.
    /// </summary>
    private RevealData TallyVotes()
    {
        lock (_gate)
        {
            Dictionary<Guid, PlayerSeat> seats = _room.GetSeats().ToDictionary(s => s.Id);
            VoteTally.Result result = VoteTally.Plurality(_votes.Values, id => seats.TryGetValue(id, out PlayerSeat seat) && seat.Alive);

            VoteCount[] tally = result.Counts
                .Select(kv => new VoteCount(seats[kv.Key].Alias, kv.Value))
                .OrderByDescending(v => v.Votes)
                .ThenBy(v => v.Alias, StringComparer.Ordinal)
                .ToArray();

            if (result.Winner is { } winner)
            {
                PlayerSeat eliminated = seats[winner];
                _room.Eliminate(eliminated.Id);
                return new RevealData(eliminated.ToInfo() with { Alive = false }, eliminated.IsBot, tally);
            }

            return new RevealData(null, null, tally);
        }
    }
}
