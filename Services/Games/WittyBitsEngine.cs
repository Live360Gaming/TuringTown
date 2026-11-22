using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using TuringTown.Models;
using TuringTown.Services.Ai;

namespace TuringTown.Services.Games;

/// <summary>
/// Runs one Witty Bits game: everyone writes funny answers to prompts, the answers to each prompt go head to head with
/// no authors shown, and everyone else votes. Two paired rounds (the second worth double), then the Last Bit, where
/// everyone answers one prompt. See docs/witty-bits.md.
///
/// Bots play like everyone else and are never labelled: an answer is shown with its author's alias only after the
/// vote, and nothing here says whether that alias is a bot. Lock order is engine gate first, then the room's lock.
/// Broadcasts are never sent while a lock is held.
/// </summary>
public sealed class WittyBitsEngine : IGameEngine
{
    private sealed class ContentUnavailableException : Exception { }

    private const string NoAnswer = "No answer";

    private readonly Room _room;
    private readonly IWittyBotAgent _agent;
    private readonly IWittyPromptGenerator _prompts;
    private readonly IContentFilter _contentFilter;
    private readonly IRoomBroadcaster _broadcaster;
    private readonly GameClock _clock;
    private readonly GameStages _stages;
    private readonly WittyBitsOptions _opt;
    private readonly ILogger _logger;
    private readonly NightContext? _night;
    private readonly Guid _gameId = Guid.NewGuid();
    private readonly CancellationTokenSource _cts = new();

    // Everything below is guarded by _gate.
    private readonly object _gate = new();
    private readonly Dictionary<(string PromptId, Guid PlayerId), string> _answers = [];
    private readonly Dictionary<Guid, int> _points = [];
    private readonly List<string> _taken = [];
    private IReadOnlyList<WittyAssignment> _writing = [];
    private Dictionary<string, Guid> _entryAuthors = [];
    private HashSet<string> _votable = [];
    private HashSet<Guid> _voters = [];
    private bool _lastBit;
    private readonly Dictionary<Guid, string> _votes = [];
    private TaskCompletionSource _stageDone = GameClock.NewSignal();

    private readonly TaskCompletionSource _tutorialDone = GameClock.NewSignal();
    private readonly TaskCompletionSource _resultsDone = GameClock.NewSignal();

    public WittyBitsEngine(
        Room room,
        IWittyBotAgent agent,
        IWittyPromptGenerator prompts,
        IContentFilter contentFilter,
        IRoomBroadcaster broadcaster,
        TimeProvider time,
        WittyBitsOptions options,
        ILogger logger,
        NightContext? night = null)
    {
        _room = room;
        _agent = agent;
        _prompts = prompts;
        _contentFilter = contentFilter;
        _broadcaster = broadcaster;
        _opt = options;
        _clock = new GameClock(time, _cts.Token);
        _stages = new GameStages(room, broadcaster, _clock, GameIds.WittyBits, _cts.Token);
        _logger = logger;
        _night = night;
    }

    public string GameKey => GameIds.WittyBits;

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

    // ---------------------------------------------------------------- the game

    public async Task<GameOutcome> PlayAsync()
    {
        try
        {
            await _broadcaster.RosterChanged(_room);

            Guid[] order = _room.GetSeats().Select(s => s.Id).ToArray();
            RandomNumberGenerator.Shuffle(order.AsSpan());
            int n = order.Length;

            // The prompts are written during the tutorial so the AI's latency is hidden.
            Task<IReadOnlyList<WittyPrompt>> generating = GeneratePromptsAsync(_opt.PairRounds * n + 1);
            await RunTutorialAsync();
            IReadOnlyList<WittyPrompt> prompts = await generating;

            int next = 0;
            for (int round = 1; round <= _opt.PairRounds; round++)
            {
                // Round r pairs each player with the one r seats along, so everyone answers two prompts a round.
                int offset = (round - 1) % (n - 1) + 1;
                WittyAssignment[] assignments = Enumerable.Range(0, n)
                    .Select(i => new WittyAssignment($"r{round}p{i}", prompts[next++].Text, [order[i], order[(i + offset) % n]]))
                    .ToArray();

                await RunWritingAsync(round, lastBit: false, assignments, TimeSpan.FromSeconds(_opt.WritingSeconds));
                for (int i = 0; i < assignments.Length; i++)
                    await RunMatchupAsync(round, lastBit: false, i, assignments.Length, assignments[i]);
            }

            int last = _opt.PairRounds + 1;
            WittyAssignment lastBit = new($"r{last}p0", prompts[next].Text, order);
            await RunWritingAsync(last, lastBit: true, [lastBit], TimeSpan.FromSeconds(_opt.LastBitWritingSeconds));
            await RunMatchupAsync(last, lastBit: true, 0, 1, lastBit);

            await RunResultsAsync();

            lock (_gate)
                return new GameOutcome(true, null, new Dictionary<Guid, int>(_points), []);
        }
        catch (OperationCanceledException)
        {
            // The room was closed.
        }
        catch (ContentUnavailableException)
        {
            _logger.LogWarning("Room {Room}: could not prepare acceptable Witty Bits prompts", _room.Code);
            await _broadcaster.GameError(_room, "Could not prepare game content. Please try starting again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Witty Bits in room {Room} failed", _room.Code);
        }

        return GameOutcome.Failed;
    }

    private async Task RunTutorialAsync()
    {
        TimeSpan max = TimeSpan.FromSeconds(_opt.TutorialMaxSeconds);
        await _stages.EnterAsync(Stage.Tutorial, 0, 0, max, null);
        await _clock.WaitAsync(max, _tutorialDone.Task);
    }

    /// <summary>
    /// Prompts from the generator if they pass validation and the content filter, otherwise the built-in ones. If not
    /// even those pass, the game cannot be played.
    /// </summary>
    private async Task<IReadOnlyList<WittyPrompt>> GeneratePromptsAsync(int count)
    {
        ContentPolicy policy = _room.ContentPolicy;
        try
        {
            IReadOnlyList<WittyPrompt> generated = await _prompts.GenerateAsync(new WittyPromptRequest(count, policy), _cts.Token);
            if (WittyPromptValidator.Problem(generated, policy, count) is { } problem)
                _logger.LogWarning("Room {Room}: Witty Bits prompts rejected: {Problem}", _room.Code, problem);
            else if (await AllAllowedAsync(generated, policy))
                return generated;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_cts.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Room {Room}: Witty Bits prompts could not be generated", _room.Code);
        }

        List<WittyPrompt> fallback = [];
        foreach (WittyPrompt prompt in BuiltInWittyPrompts.Pick(policy, BuiltInWittyPrompts.Count(policy)))
        {
            if (await _contentFilter.IsAllowedAsync(prompt.Text, policy, _cts.Token)) fallback.Add(prompt);
            if (fallback.Count == count) return fallback;
        }

        if (fallback.Count == 0) throw new ContentUnavailableException();
        // A very big room: repeat the allowed prompts rather than stop the game.
        return Enumerable.Range(0, count).Select(i => fallback[i % fallback.Count]).ToArray();
    }

    private async Task<bool> AllAllowedAsync(IReadOnlyList<WittyPrompt> prompts, ContentPolicy policy)
    {
        foreach (WittyPrompt prompt in prompts)
            if (!await _contentFilter.IsAllowedAsync(prompt.Text, policy, _cts.Token)) return false;
        return true;
    }

    private async Task RunWritingAsync(int round, bool lastBit, IReadOnlyList<WittyAssignment> assignments, TimeSpan duration)
    {
        lock (_gate)
        {
            _writing = assignments;
            _stageDone = GameClock.NewSignal();
        }

        await _stages.EnterAsync(Stage.Writing, round, 0, duration, new WritingData(round, lastBit, assignments));
        await UpdateProgressAsync();

        using CancellationTokenSource stage = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        HashSet<Guid> bots = _room.GetSeats().Where(s => s.IsBot).Select(s => s.Id).ToHashSet();
        Task[] writers = assignments
            .SelectMany(a => a.PlayerIds.Where(bots.Contains).Select(bot => BotWriteAsync(bot, a, duration, stage.Token)))
            .ToArray();

        await _clock.WaitAsync(duration, CurrentSignal());
        stage.Cancel();
        await Task.WhenAll(writers);

        // A bot whose agent was too slow still answers (with a scripted line), so a missing answer is only ever a human's.
        foreach (WittyAssignment assignment in assignments)
            foreach (Guid bot in assignment.PlayerIds.Where(bots.Contains))
                if (!HasAnswer(assignment.PromptId, bot))
                    await StoreBotAnswerAsync(bot, assignment, BuiltInWittyPrompts.ScriptedAnswer(assignment.Text, Taken()));
    }

    private async Task RunMatchupAsync(int round, bool lastBit, int index, int count, WittyAssignment assignment)
    {
        // Answers in a random order, so the order says nothing about who wrote which.
        Guid[] authors = assignment.PlayerIds.ToArray();
        RandomNumberGenerator.Shuffle(authors.AsSpan());

        WittyEntry[] entries;
        Dictionary<string, Guid> entryAuthors = [];
        lock (_gate)
        {
            entries = authors.Select((author, i) =>
            {
                string id = ((char)('a' + i)).ToString();
                entryAuthors[id] = author;
                return _answers.TryGetValue((assignment.PromptId, author), out string? text)
                    ? new WittyEntry(id, text, false)
                    : new WittyEntry(id, NoAnswer, true);
            }).ToArray();
        }

        // The Last Bit only shows the answers that were written.
        if (lastBit) entries = entries.Where(e => !e.Missing).ToArray();
        int present = entries.Count(e => !e.Missing);
        if (present == 0) return;

        IReadOnlyList<PlayerSeat> seats = _room.GetSeats();
        lock (_gate)
        {
            _entryAuthors = entryAuthors;
            _votable = entries.Where(e => !e.Missing).Select(e => e.Id).ToHashSet();
            _voters = (lastBit ? seats : seats.Where(s => !authors.Contains(s.Id))).Select(s => s.Id).ToHashSet();
            _lastBit = lastBit;
            _votes.Clear();
            _stageDone = GameClock.NewSignal();
        }

        // Two or more answers go to a vote. A lone answer (the other player did not answer) wins by default.
        if (present >= 2)
        {
            TimeSpan duration = TimeSpan.FromSeconds(_opt.MatchupSeconds);
            await _stages.EnterAsync(Stage.Matchup, round, index, duration,
                new MatchupData(round, lastBit, index, count, assignment.Text, entries, assignment.PlayerIds));
            await UpdateProgressAsync();

            using CancellationTokenSource stage = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            Task[] voters = seats.Where(s => s.IsBot && IsVoter(s.Id))
                .Select(s => BotVoteAsync(s, assignment.Text, entries, entryAuthors, duration, stage.Token)).ToArray();

            await _clock.WaitAsync(duration, CurrentSignal());
            stage.Cancel();
            await Task.WhenAll(voters);
        }

        MatchupResultData result = Score(round, lastBit, index, count, assignment.Text, entries, entryAuthors, seats);

        TimeSpan show = TimeSpan.FromSeconds(_opt.MatchupResultSeconds);
        await _stages.EnterAsync(Stage.MatchupResult, round, index, show, result);
        await _clock.WaitAsync(show);
    }

    private MatchupResultData Score(int round, bool lastBit, int index, int count, string prompt, IReadOnlyList<WittyEntry> entries,
        IReadOnlyDictionary<string, Guid> authors, IReadOnlyList<PlayerSeat> seats)
    {
        lock (_gate)
        {
            Dictionary<string, int> votes = entries.ToDictionary(e => e.Id, e => _votes.Values.Count(v => v == e.Id));
            int total = votes.Values.Sum();
            int present = entries.Count(e => !e.Missing);

            WittyEntryResult[] results = entries.Select(e =>
            {
                int v = votes[e.Id];
                bool sweep = false;
                int points;
                if (e.Missing) points = 0;
                else if (lastBit) points = v * _opt.LastBitPointsPerVote;
                else if (present == 1) points = _opt.ForfeitPoints * round;
                else
                {
                    points = total == 0 ? 0 : (int)Math.Round((double)_opt.MatchupPoints * round * v / total, MidpointRounding.AwayFromZero);
                    sweep = total > 0 && v == total;
                    if (sweep) points += _opt.SweepBonus * round;
                }

                Guid author = authors[e.Id];
                string alias = seats.FirstOrDefault(s => s.Id == author).Alias ?? "";
                return new WittyEntryResult(e.Id, e.Text, e.Missing, author, alias, v, points, sweep);
            }).ToArray();

            foreach (WittyEntryResult r in results)
            {
                _points[r.AuthorId] = _points.GetValueOrDefault(r.AuthorId) + r.Points;
                if (!r.Missing) _taken.Add(r.Text);
            }

            // The room just saw who wrote what, so the AI team may remember what the room found funny.
            if (results.Where(r => !r.Missing).OrderByDescending(r => r.Votes).FirstOrDefault() is { Votes: > 0 } best)
                _night?.AddHighlight($"Witty Bits: {best.Alias}'s answer \"{best.Text}\" to \"{prompt}\" won {best.Votes} vote(s).");

            return new MatchupResultData(round, lastBit, index, count, prompt, results);
        }
    }

    private async Task RunResultsAsync()
    {
        IReadOnlyList<PlayerSeat> seats = _room.GetSeats();
        Standing[] standings;
        lock (_gate)
            standings = seats.Select(s => new Standing(s.Id, s.Alias, _points.GetValueOrDefault(s.Id)))
                .OrderByDescending(s => s.Points).ThenBy(s => s.Alias, StringComparer.Ordinal).ToArray();

        TimeSpan duration = TimeSpan.FromSeconds(_opt.ResultsSeconds);
        await _stages.EnterAsync(Stage.Results, 0, 0, duration, new WittyResultsData(standings));
        await _clock.WaitAsync(duration, _resultsDone.Task);
    }

    // ---------------------------------------------------------------- the bots

    private async Task BotWriteAsync(Guid botId, WittyAssignment assignment, TimeSpan stageLength, CancellationToken ct)
    {
        try
        {
            if (!_room.TryGetSeat(botId, out PlayerSeat bot)) return;

            // Write within the stage, like a person would, leaving a few seconds to spare.
            int maxMs = (int)Math.Max(0, Math.Min(_opt.BotAnswerMaxDelayMs, stageLength.TotalMilliseconds - 3000));
            TimeSpan pause = GameClock.RandomDelay(Math.Min(_opt.BotAnswerMinDelayMs, maxMs), maxMs);
            DateTimeOffset started = _clock.UtcNow;

            string? text = await _agent.WriteAnswerAsync(
                new WittyAnswerRequest(new BotKey(_gameId, botId), bot.Alias, assignment.Text, Taken(), _opt.AnswerMaxLength, _room.ContentPolicy, _night?.Snapshot()), ct);

            TimeSpan left = pause - (_clock.UtcNow - started);
            if (left > TimeSpan.Zero) await _clock.Delay(left, ct);

            await StoreBotAnswerAsync(botId, assignment, text);
        }
        catch (OperationCanceledException)
        {
            // Time is up; a scripted answer fills in.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Room {Room}: a bot could not write its Witty Bits answer", _room.Code);
        }
    }

    /// <summary>
    /// A bot's answer goes through the same checks as a human's. One that fails is replaced by a scripted answer,
    /// which is checked too; if that also fails the bot leaves the prompt unanswered.
    /// </summary>
    private async Task StoreBotAnswerAsync(Guid botId, WittyAssignment assignment, string? text)
    {
        foreach (string? candidate in new[] { text, BuiltInWittyPrompts.ScriptedAnswer(assignment.Text, Taken()) })
        {
            string? clean = await CheckAnswerAsync(candidate, CancellationToken.None);
            if (clean is null) continue;

            lock (_gate)
            {
                if (_room.State.Stage != Stage.Writing || !_writing.Contains(assignment)) return;
                _answers[(assignment.PromptId, botId)] = clean;
            }
            await UpdateProgressAsync();
            return;
        }
    }

    private async Task BotVoteAsync(PlayerSeat bot, string prompt, IReadOnlyList<WittyEntry> entries, IReadOnlyDictionary<string, Guid> authors,
        TimeSpan stageLength, CancellationToken ct)
    {
        try
        {
            // A bot never votes for its own Last Bit answer, and is never told who wrote the others.
            WittyEntry[] choices = entries.Where(e => !e.Missing && authors[e.Id] != bot.Id).ToArray();
            if (choices.Length == 0) return;

            int maxMs = (int)Math.Max(0, Math.Min(_opt.BotVoteMaxDelayMs, stageLength.TotalMilliseconds - 2000));
            await _clock.Delay(GameClock.RandomDelay(Math.Min(_opt.BotVoteMinDelayMs, maxMs), maxMs), ct);

            string? choice = await _agent.VoteAsync(new WittyVoteRequest(new BotKey(_gameId, bot.Id), bot.Alias, prompt, choices, _room.ContentPolicy, _night?.Snapshot()), ct);
            // No answer, or one that is not on offer, is an abstention (as in Turing Town).
            if (choice is null || choices.All(c => c.Id != choice)) return;

            lock (_gate)
            {
                if (_room.State.Stage != Stage.Matchup || !_voters.Contains(bot.Id)) return;
                _votes[bot.Id] = choice;
            }
            await UpdateProgressAsync();
        }
        catch (OperationCanceledException)
        {
            // The vote is over; the bot abstains.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Room {Room}: a bot could not vote in Witty Bits", _room.Code);
        }
    }

    // ---------------------------------------------------------------- helpers

    private Task CurrentSignal()
    {
        lock (_gate)
            return _stageDone.Task;
    }

    private bool HasAnswer(string promptId, Guid playerId)
    {
        lock (_gate)
            return _answers.ContainsKey((promptId, playerId));
    }

    private bool IsVoter(Guid playerId)
    {
        lock (_gate)
            return _voters.Contains(playerId);
    }

    private IReadOnlyList<string> Taken()
    {
        lock (_gate)
            return _taken.Concat(_answers.Values).ToArray();
    }

    /// <summary>The cleaned answer if it may be shown, or null.</summary>
    private async Task<string?> CheckAnswerAsync(string? text, CancellationToken ct)
    {
        if (ChatFilter.Check(text, _opt.AnswerMaxLength, out string clean) != ChatCheck.Ok) return null;
        return await _contentFilter.IsAllowedAsync(clean, _room.ContentPolicy, ct) ? clean : null;
    }

    private (Stage Stage, int Done, int Total) ComputeProgress()
    {
        Stage stage = _room.State.Stage;
        Dictionary<Guid, PlayerSeat> seats = _room.GetSeats().ToDictionary(s => s.Id);
        bool Required(Guid id) => seats.TryGetValue(id, out PlayerSeat s) && (s.IsBot || s.Connected);

        switch (stage)
        {
            case Stage.Writing:
            {
                (string PromptId, Guid Id)[] required = _writing.SelectMany(a => a.PlayerIds.Where(Required).Select(id => (a.PromptId, id))).ToArray();
                return (stage, required.Count(r => _answers.ContainsKey(r)), required.Length);
            }
            case Stage.Matchup:
            {
                Guid[] required = _voters.Where(Required).ToArray();
                return (stage, required.Count(_votes.ContainsKey), required.Length);
            }
            default:
                return (stage, 0, 0);
        }
    }

    /// <summary>Tells the room how far along the stage is, and ends it early once everyone required is done.</summary>
    private async Task UpdateProgressAsync()
    {
        (Stage Stage, int Done, int Total) progress;
        TaskCompletionSource signal;
        lock (_gate)
        {
            progress = ComputeProgress();
            signal = _stageDone;
        }

        if (progress.Stage is not (Stage.Writing or Stage.Matchup)) return;

        await _broadcaster.Progress(_room, progress.Done, progress.Total);
        if (progress.Done >= progress.Total) signal.TrySetResult();
    }

    // ---------------------------------------------------------------- calls from the hub (return an error message, or null)

    public bool IsPaused => _clock.IsPaused;

    public Task<string?> SetPausedAsync(bool paused) => _stages.SetPausedAsync(paused);

    public async Task<string?> TutorialStepAsync(int step)
    {
        if (step is < 0 or > 100) return "That tutorial step does not exist.";
        if (_room.State.GameId != GameKey || !_room.TryUpdateState(Stage.Tutorial, s => s with { Step = step }, out GameState state))
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

    public Task NotifyPresenceChangedAsync() => UpdateProgressAsync();

    /// <summary>Witty Bits has no chat.</summary>
    public IReadOnlyList<ChatMessage> GetTranscript() => [];

    /// <summary>Witty Bits never reveals who is a bot.</summary>
    public IReadOnlyList<Guid> GetUnmasked() => [];

    /// <summary>A player writes (or rewrites) their answer to one of their prompts.</summary>
    public async Task<string?> SubmitAnswerAsync(Guid playerId, string? promptId, string? text)
    {
        if (!_room.TryGetSeat(playerId, out PlayerSeat seat) || seat.IsBot) return "Unknown player.";
        if (_room.State.Stage != Stage.Writing) return "Writing time is over.";
        if (_clock.IsPaused) return "The game is paused.";

        WittyAssignment? assignment;
        lock (_gate)
            assignment = _writing.Cast<WittyAssignment?>().FirstOrDefault(a => a!.Value.PromptId == promptId && a.Value.PlayerIds.Contains(playerId));
        if (assignment is null) return "That is not one of your prompts.";

        ChatCheck check = ChatFilter.Check(text, _opt.AnswerMaxLength, out string clean);
        if (check != ChatCheck.Ok) return check == ChatCheck.TooLong ? "That answer is too long." : ChatFilter.Describe(check);
        if (!await _contentFilter.IsAllowedAsync(clean, _room.ContentPolicy, _cts.Token)) return "That answer is not allowed.";

        lock (_gate)
        {
            if (_room.State.Stage != Stage.Writing || !_writing.Contains(assignment.Value)) return "Writing time is over.";
            _answers[(assignment.Value.PromptId, playerId)] = clean;
        }

        await UpdateProgressAsync();
        return null;
    }

    /// <summary>A player votes for one answer in the current matchup. They can change it until the vote ends.</summary>
    public async Task<string?> CastVoteAsync(Guid playerId, string? entryId)
    {
        if (!_room.TryGetSeat(playerId, out PlayerSeat seat) || seat.IsBot) return "Unknown player.";
        if (_clock.IsPaused) return "The game is paused.";

        lock (_gate)
        {
            if (_room.State.Stage != Stage.Matchup) return "Voting is closed.";
            if (!_voters.Contains(playerId)) return "You wrote one of these, so you sit this vote out.";
            if (entryId is null || !_votable.Contains(entryId)) return "Pick one of the answers.";
            if (_lastBit && _entryAuthors[entryId] == playerId) return "You cannot vote for your own answer.";
            _votes[playerId] = entryId;
        }

        await UpdateProgressAsync();
        return null;
    }
}
