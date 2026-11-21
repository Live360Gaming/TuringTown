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
    private readonly IQuestionGenerator _questionGenerator;
    private readonly IContentFilter _contentFilter;
    private readonly IRoomBroadcaster _broadcaster;
    private readonly GameClock _clock;
    private readonly ChatLog _chat;
    private readonly TuringTownOptions _opt;
    private readonly ILogger _logger;
    private readonly Action<TuringTownEngine>? _onFinished;
    private readonly CancellationTokenSource _cts = new();

    // Everything below is guarded by _gate.
    private readonly object _gate = new();
    private readonly Dictionary<Guid, OnboardingAnswer[]> _answers = [];
    private readonly Dictionary<Guid, string> _personas = [];
    private readonly Dictionary<Guid, Guid?> _votes = [];
    private readonly HashSet<Guid> _unmasked = [];

    // Lines that @mentioned a bot and that it has not answered yet, and a signal that wakes the bot when one arrives.
    private readonly Dictionary<Guid, List<ChatLine>> _pendingMentions = [];
    private readonly Dictionary<Guid, TaskCompletionSource> _mentionSignals = [];
    private IReadOnlyList<OnboardingQuestion> _questions = [];
    private TaskCompletionSource _stageDone = GameClock.NewSignal();

    private readonly TaskCompletionSource _tutorialDone = GameClock.NewSignal();
    private readonly TaskCompletionSource _resultsDone = GameClock.NewSignal();
    private Task? _loop;

    public TuringTownEngine(
        Room room,
        IBotAgent agent,
        IQuestionGenerator questionGenerator,
        IContentFilter contentFilter,
        IRoomBroadcaster broadcaster,
        TimeProvider time,
        TuringTownOptions options,
        ILogger logger,
        Action<TuringTownEngine>? onFinished = null)
    {
        _room = room;
        _agent = agent;
        _questionGenerator = questionGenerator;
        _contentFilter = contentFilter;
        _broadcaster = broadcaster;
        _opt = options;
        _clock = new GameClock(time, _cts.Token);
        _chat = new ChatLog(options.TranscriptLimit, options.ChatMinIntervalMs);
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

    private async Task EnterStageAsync(Stage stage, int round, int step, TimeSpan? duration, object? data)
    {
        long? endsAt = duration is { } d ? _clock.EndsAt(d) : null;
        GameState state = _room.SetState(new GameState(Room.GameId, stage, round, step, endsAt, data));
        await _broadcaster.StateChanged(_room, state);
    }

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

        if (!_chat.TryTakeTurn(playerId, _clock.UtcNow)) return "Slow down a little.";

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

        IReadOnlyList<PlayerSeat> mentioned =
            MentionParser.Find(cleaned, _room.GetSeats().Where(s => s.Id != sender.Id), _opt.MaxMentionsPerMessage);

        ChatMessage message;
        lock (_gate)
        {
            if (_room.State.Stage != Stage.Discussion) return "Chat is closed right now.";

            message = new ChatMessage(Guid.NewGuid(), sender.Id, sender.Alias, sender.Color, sender.CharacterId, cleaned,
                _clock.ToUnixMs(_clock.UtcNow), mentioned.Select(s => s.Id).ToArray());
            _chat.Add(message);

            // A bot that was spoken to will answer, rather than waiting for its usual turn.
            foreach (PlayerSeat seat in mentioned.Where(s => s.IsBot))
            {
                if (!_pendingMentions.TryGetValue(seat.Id, out List<ChatLine>? pending))
                    _pendingMentions[seat.Id] = pending = [];
                pending.Add(new ChatLine(sender.Alias, cleaned));

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

            if (QuestionValidator.IsValid(questions, policy, _opt.Mix) && await QuestionsAllowedAsync(questions, policy))
                return questions;
            _logger.LogWarning("Room {Room}: the generated onboarding questions failed validation, using the built-in ones", _room.Code);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Room {Room}: could not get onboarding questions from the agent, using the built-in ones", _room.Code);
        }

        return ScriptedBotAgent.BuiltInQuestions(policy, _opt.Mix);
    }

    /// <summary>
    /// Generated text is shown to every player, so it passes the same content filter as chat.
    /// </summary>
    private async Task<bool> QuestionsAllowedAsync(IReadOnlyList<OnboardingQuestion> questions, ContentPolicy policy)
    {
        foreach (OnboardingQuestion q in questions)
        {
            if (!await _contentFilter.IsAllowedAsync(q.Text, policy, _cts.Token)) return false;
            foreach (QuestionOption option in q.Options)
                if (!await _contentFilter.IsAllowedAsync(option.Text, policy, _cts.Token)) return false;
        }
        return true;
    }

    private async Task BotOnboardingAsync(IReadOnlyList<OnboardingQuestion> questions)
    {
        // What the humans answered, with no ids or names: free text for style, option ids for what they pick.
        OnboardingAnswer[] samples;
        lock (_gate)
            samples = _answers.Values.SelectMany(a => a).ToArray();

        await Task.WhenAll(BotSeats().Select(async bot =>
        {
            try
            {
                IReadOnlyList<OnboardingAnswer> given = await _agent.AnswerOnboardingAsync(
                    new OnboardingAnswerRequest(bot.Alias, _room.ContentPolicy, questions, samples), _cts.Token);

                // The same checks as for a human. An invalid answer is replaced, so a bot always has a full persona.
                OnboardingAnswer[] answers = new OnboardingAnswer[questions.Count];
                for (int i = 0; i < answers.Length; i++)
                {
                    OnboardingAnswer? answer = given?.FirstOrDefault(a => a?.QuestionId == questions[i].Id);
                    bool ok = OnboardingAnswerValidator.ValidateOne(questions[i], i + 1, answer, _opt.AnswerMaxLength, out OnboardingAnswer cleaned) is null
                              && (cleaned.Text is not { Length: > 0 } text || await _contentFilter.IsAllowedAsync(text, _room.ContentPolicy, _cts.Token));

                    answers[i] = ok ? cleaned : ScriptedBotAgent.ScriptedAnswer(questions[i], samples);
                }

                lock (_gate)
                    _personas[bot.Id] = PersonaText(questions, answers);
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

    /// <summary>
    /// The bot's own answers as one line of text, for its prompt. Choices are written out as the option texts.
    /// </summary>
    private static string PersonaText(IReadOnlyList<OnboardingQuestion> questions, IReadOnlyList<OnboardingAnswer> answers)
    {
        List<string> parts = [];
        foreach (OnboardingQuestion q in questions)
        {
            OnboardingAnswer? a = answers.FirstOrDefault(x => x.QuestionId == q.Id);
            string value = q.Kind == QuestionKind.Text
                ? a?.Text ?? ""
                : string.Join(", ", q.Options.Where(o => a?.OptionIds?.Contains(o.Id) == true).Select(o => o.Text));
            if (value.Length > 0) parts.Add(value);
        }
        return string.Join(" | ", parts);
    }

    private async Task BotChatLoopAsync(PlayerSeat bot, int round, CancellationToken ct)
    {
        try
        {
            int posted = 0;
            // Quiet turns do not use up the budget, so cap the attempts as well.
            for (int attempt = 0; attempt < _opt.BotMessagesPerRound * 3 && posted < _opt.BotMessagesPerRound; attempt++)
            {
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
                    string[] others = _room.GetSeats().Where(s => s.Alive && s.Id != bot.Id).Select(s => s.Alias).ToArray();
                    request = new ChatRequest(bot.Alias, _personas.GetValueOrDefault(bot.Id, ""), _room.ContentPolicy, round,
                        RecentLines(), mentions, others);
                }

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

    /// <summary>
    /// Waits for the bot's next turn: a random, human-like pause, or a shorter one if somebody @mentions it, so a
    /// bot that is spoken to answers soon after instead of ignoring the question.
    /// </summary>
    private async Task WaitForTurnAsync(PlayerSeat bot, CancellationToken ct)
    {
        TaskCompletionSource signal;
        bool alreadyMentioned;
        lock (_gate)
        {
            alreadyMentioned = _pendingMentions.TryGetValue(bot.Id, out List<ChatLine>? pending) && pending.Count > 0;
            signal = _mentionSignals[bot.Id] = GameClock.NewSignal();
        }

        if (!alreadyMentioned)
        {
            using CancellationTokenSource local = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task idle = _clock.Delay(GameClock.RandomDelay(_opt.BotChatMinDelayMs, _opt.BotChatMaxDelayMs), local.Token);

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

        // Someone spoke to this bot: reply after a short pause, like a person reading and typing.
        await _clock.Delay(GameClock.RandomDelay(_opt.BotMentionReplyMinDelayMs, _opt.BotMentionReplyMaxDelayMs), ct);
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
