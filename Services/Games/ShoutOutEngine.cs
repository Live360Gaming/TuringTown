using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using TuringTown.Models;
using TuringTown.Services.Ai;

namespace TuringTown.Services.Games;

/// <summary>
/// Runs one Shout Out game: the room votes for trivia categories, then answers questions buzz-in style. The first to
/// buzz picks an answer; a wrong one costs points and reopens the question for everyone else. The last few questions are
/// worth double. See docs/shout-out.md.
///
/// Bots play like everyone else and are never labelled; when and how well they buzz is decided here
/// (<see cref="ShoutOutBotPolicy"/>), not by a model. Lock order is engine gate first, then the room's lock. Broadcasts
/// are never sent while a lock is held.
/// </summary>
public sealed class ShoutOutEngine : IGameEngine
{
    private enum Phase
    {
        None,
        Open,
        Buzzed
    }

    private readonly Room _room;
    private readonly ITriviaGenerator _trivia;
    private readonly IContentFilter _contentFilter;
    private readonly IRoomBroadcaster _broadcaster;
    private readonly GameClock _clock;
    private readonly GameStages _stages;
    private readonly ShoutOutOptions _opt;
    private readonly ShoutOutBotPolicy _bots;
    private readonly ILogger _logger;
    private readonly NightContext? _night;
    private readonly CancellationTokenSource _cts = new();

    // Everything below is guarded by _gate.
    private readonly object _gate = new();
    private readonly Dictionary<Guid, int> _points = [];
    private IReadOnlyList<TriviaCategory> _offered = [];
    private readonly Dictionary<Guid, string[]> _categoryVotes = [];
    private Phase _phase;
    private DateTimeOffset _buzzOpensAt;
    private readonly HashSet<Guid> _out = [];
    private readonly Dictionary<Guid, DateTimeOffset> _lockedUntil = [];
    private Guid? _buzzer;
    private string? _answer;
    private IReadOnlyList<TriviaOption> _options = [];
    private TaskCompletionSource _signal = GameClock.NewSignal();

    private readonly TaskCompletionSource _tutorialDone = GameClock.NewSignal();
    private readonly TaskCompletionSource _resultsDone = GameClock.NewSignal();

    internal ShoutOutEngine(
        Room room,
        ITriviaGenerator trivia,
        IContentFilter contentFilter,
        IRoomBroadcaster broadcaster,
        TimeProvider time,
        ShoutOutOptions options,
        ILogger logger,
        NightContext? night = null,
        Func<double>? random = null)
    {
        _room = room;
        _trivia = trivia;
        _contentFilter = contentFilter;
        _broadcaster = broadcaster;
        _opt = options;
        _clock = new GameClock(time, _cts.Token);
        _stages = new GameStages(room, broadcaster, _clock, GameIds.ShoutOut, _cts.Token);
        _bots = new ShoutOutBotPolicy(options, random);
        _logger = logger;
        _night = night;
    }

    public ShoutOutEngine(Room room, ITriviaGenerator trivia, IContentFilter contentFilter, IRoomBroadcaster broadcaster,
        TimeProvider time, ShoutOutOptions options, ILogger logger, NightContext? night)
        : this(room, trivia, contentFilter, broadcaster, time, options, logger, night, null)
    {
    }

    public string GameKey => GameIds.ShoutOut;

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

            TriviaCategory[] offered = TriviaCategories.All.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue))
                .Take(Math.Clamp(_opt.CategoryChoices, 2, TriviaCategories.All.Length)).ToArray();
            lock (_gate) _offered = offered;

            // The questions are written while the room watches the tutorial and votes, across every category on offer
            // (twice as many as needed), so the vote only has to pick among them and nobody waits for the AI.
            Task<IReadOnlyList<TriviaQuestion>> generating = GenerateAsync(offered, Math.Max(1, _opt.Questions) * 2);

            await RunTutorialAsync();
            IReadOnlyList<string> chosen = await RunCategoriesAsync(offered);
            IReadOnlyList<TriviaQuestion> questions = Order(await generating, chosen, Math.Max(1, _opt.Questions));

            for (int i = 0; i < questions.Count; i++)
                await RunQuestionAsync(i, questions.Count, questions[i], doubled: i >= questions.Count - _opt.DoubleQuestions);

            await RunResultsAsync();

            lock (_gate)
                return new GameOutcome(true, null, new Dictionary<Guid, int>(_points), []);
        }
        catch (OperationCanceledException)
        {
            // The room was closed.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shout Out in room {Room} failed", _room.Code);
        }

        return GameOutcome.Failed;
    }

    private async Task RunTutorialAsync()
    {
        TimeSpan max = TimeSpan.FromSeconds(_opt.TutorialMaxSeconds);
        await _stages.EnterAsync(Stage.Tutorial, 0, 0, max, null);
        await _clock.WaitAsync(max, _tutorialDone.Task);
    }

    /// <summary>Questions from the generator (validated, topped up from the built-in bank), each passing the content filter.</summary>
    private async Task<IReadOnlyList<TriviaQuestion>> GenerateAsync(IReadOnlyList<TriviaCategory> categories, int count)
    {
        ContentPolicy policy = _room.ContentPolicy;
        TriviaRequest request = new(categories, _night?.RoomEras ?? [], count, policy);
        IReadOnlyList<TriviaQuestion> questions;
        try
        {
            questions = TriviaValidator.Usable(await _trivia.GenerateAsync(request, _cts.Token), policy);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_cts.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Room {Room}: trivia could not be generated, using the built-in questions", _room.Code);
            questions = [];
        }

        if (questions.Count < count)
            questions = [.. questions, .. BuiltInTrivia.Pick(request, count - questions.Count, questions.Select(q => q.Text))];

        List<TriviaQuestion> allowed = [];
        foreach (TriviaQuestion q in questions)
            if (await AllowedAsync(q, policy)) allowed.Add(q);
        return allowed;
    }

    private async Task<bool> AllowedAsync(TriviaQuestion q, ContentPolicy policy)
    {
        foreach (string text in q.Options.Prepend(q.Text))
            if (!await _contentFilter.IsAllowedAsync(text, policy, _cts.Token)) return false;
        return true;
    }

    /// <summary>The chosen categories' questions first, then the rest, spread so one category does not come in a run.</summary>
    internal static IReadOnlyList<TriviaQuestion> Order(IReadOnlyList<TriviaQuestion> questions, IReadOnlyList<string> chosen, int count)
    {
        TriviaQuestion[] picked = questions
            .OrderBy(q => chosen.Contains(q.CategoryId) ? 0 : 1)
            .ThenBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue))
            .Take(count).ToArray();

        // Round-robin over the categories, so the same category rarely comes twice in a row.
        return picked.GroupBy(q => q.CategoryId)
            .SelectMany(g => g.Select((q, i) => (q, i)))
            .OrderBy(x => x.i).ThenBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue))
            .Select(x => x.q).ToArray();
    }

    // ---------------------------------------------------------------- categories

    private async Task<IReadOnlyList<string>> RunCategoriesAsync(IReadOnlyList<TriviaCategory> offered)
    {
        lock (_gate)
        {
            _categoryVotes.Clear();
            _signal = GameClock.NewSignal();
        }

        TimeSpan duration = TimeSpan.FromSeconds(_opt.CategoriesSeconds);
        await _stages.EnterAsync(Stage.Categories, 0, 0, duration, new CategoriesData(offered, _opt.MaxCategoryPicks));
        await UpdateProgressAsync();

        // Bots vote too, so the count of votes says nothing about how many humans there are.
        using CancellationTokenSource stage = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        Task[] voters = _room.GetSeats().Where(s => s.IsBot).Select(s => BotCategoryVoteAsync(s.Id, offered, duration, stage.Token)).ToArray();

        await _clock.WaitAsync(duration, CurrentSignal());
        stage.Cancel();
        await Task.WhenAll(voters);

        lock (_gate)
        {
            // The two most picked (ties broken at random), plus one wildcard, so every game has some variety.
            Dictionary<string, int> counts = offered.ToDictionary(c => c.Id, c => _categoryVotes.Values.Count(v => v.Contains(c.Id)));
            string[] top = counts.OrderByDescending(c => c.Value).ThenBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue))
                .Select(c => c.Key).Take(2).ToArray();
            string[] rest = offered.Select(c => c.Id).Except(top).ToArray();
            return rest.Length == 0 ? top : [.. top, rest[RandomNumberGenerator.GetInt32(rest.Length)]];
        }
    }

    private async Task BotCategoryVoteAsync(Guid bot, IReadOnlyList<TriviaCategory> offered, TimeSpan stageLength, CancellationToken ct)
    {
        try
        {
            int maxMs = (int)Math.Max(0, Math.Min(_opt.BotCategoryMaxDelayMs, stageLength.TotalMilliseconds - 2000));
            await _clock.Delay(GameClock.RandomDelay(Math.Min(_opt.BotCategoryMinDelayMs, maxMs), maxMs), ct);
            int picks = 1 + RandomNumberGenerator.GetInt32(Math.Max(1, _opt.MaxCategoryPicks));
            string[] ids = offered.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).Take(picks).Select(c => c.Id).ToArray();
            lock (_gate)
            {
                if (_room.State.Stage != Stage.Categories) return;
                _categoryVotes[bot] = ids;
            }
            await UpdateProgressAsync();
        }
        catch (OperationCanceledException)
        {
            // The vote is over.
        }
    }

    // ---------------------------------------------------------------- a question

    private async Task RunQuestionAsync(int index, int count, TriviaQuestion question, bool doubled)
    {
        TriviaOption[] options = question.Options.Select((o, i) => new TriviaOption(((char)('a' + i)).ToString(), o)).ToArray();
        string correctId = options[question.Correct].Id;
        string category = TriviaCategories.NameOf(question.CategoryId);
        int value = _opt.CorrectPoints * (doubled ? 2 : 1);
        List<TriviaAttempt> attempts = [];
        Dictionary<Guid, ShoutOutBotPolicy.BuzzPlan> plans = [];

        lock (_gate)
        {
            _out.Clear();
            _lockedUntil.Clear();
            _options = options;
        }

        bool first = true;
        while (true)
        {
            // ---- open for buzzing (after a moment to read the first time)
            TimeSpan read = first ? TimeSpan.FromSeconds(_opt.ReadSeconds) : TimeSpan.Zero;
            TimeSpan open = TimeSpan.FromSeconds(first ? _opt.BuzzSeconds : _opt.RebuzzSeconds);
            DateTimeOffset opensAt = _clock.UtcNow + read;
            Guid[] outNow;
            lock (_gate)
            {
                _phase = Phase.Open;
                _buzzOpensAt = opensAt;
                _buzzer = null;
                _answer = null;
                _signal = GameClock.NewSignal();
                outNow = _out.ToArray();
            }

            await _stages.EnterAsync(Stage.Question, index + 1, attempts.Count, read + open,
                new TriviaQuestionData(index, count, doubled, category, question.Text, options, _clock.ToUnixMs(opensAt), outNow));

            using (CancellationTokenSource buzzing = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                Task[] bots = _room.GetSeats().Where(s => s.IsBot && !outNow.Contains(s.Id))
                    .Select(s => BotBuzzAsync(s.Id, question, opensAt, plans, buzzing.Token)).ToArray();
                await _clock.WaitAsync(read + open, CurrentSignal());
                buzzing.Cancel();
                await Task.WhenAll(bots);
            }

            Guid? buzzer;
            lock (_gate)
            {
                buzzer = _buzzer;
                if (buzzer is null) _phase = Phase.None;
            }
            if (buzzer is not { } who) break;

            // ---- the buzzer answers
            PlayerSeat seat = _room.TryGetSeat(who, out PlayerSeat s0) ? s0 : default;
            TimeSpan answerTime = TimeSpan.FromSeconds(_opt.AnswerSeconds);
            await _stages.EnterAsync(Stage.Buzzed, index + 1, attempts.Count, answerTime,
                new TriviaQuestionData(index, count, doubled, category, question.Text, options, _clock.ToUnixMs(opensAt), outNow, who, seat.Alias));

            Task botAnswer = Task.CompletedTask;
            using CancellationTokenSource answering = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            if (seat.IsBot)
                botAnswer = BotAnswerAsync(who, question, options, plans.GetValueOrDefault(who), answerTime, answering.Token);
            await _clock.WaitAsync(answerTime, CurrentSignal());
            answering.Cancel();
            await botAnswer;

            string? picked;
            lock (_gate)
            {
                picked = _answer;
                _phase = Phase.None;
            }

            bool right = picked == correctId;
            int points = right ? value : -_opt.WrongPoints;
            AddPoints(who, points);
            attempts.Add(new TriviaAttempt(who, seat.Alias ?? "", picked, right, points));
            if (right) break;

            bool anyoneLeft;
            lock (_gate)
            {
                _out.Add(who);
                anyoneLeft = _room.GetSeats().Any(p => !_out.Contains(p.Id) && (p.IsBot || p.Connected));
            }
            if (!anyoneLeft) break;
            first = false;
        }

        lock (_gate) _phase = Phase.None;
        TimeSpan show = TimeSpan.FromSeconds(_opt.RevealSeconds);
        await _stages.EnterAsync(Stage.AnswerReveal, index + 1, 0, show,
            new AnswerRevealData(index, count, category, question.Text, options, correctId, attempts, Standings().Take(3).ToArray()));
        await _clock.WaitAsync(show);
    }

    private async Task RunResultsAsync()
    {
        TimeSpan duration = TimeSpan.FromSeconds(_opt.ResultsSeconds);
        await _stages.EnterAsync(Stage.Results, 0, 0, duration, new ShoutOutResultsData(Standings()));
        await _clock.WaitAsync(duration, _resultsDone.Task);
    }

    private IReadOnlyList<Standing> Standings()
    {
        IReadOnlyList<PlayerSeat> seats = _room.GetSeats();
        lock (_gate)
            return seats.Select(s => new Standing(s.Id, s.Alias, _points.GetValueOrDefault(s.Id)))
                .OrderByDescending(s => s.Points).ThenBy(s => s.Alias, StringComparer.Ordinal).ToArray();
    }

    private void AddPoints(Guid player, int points)
    {
        lock (_gate)
            _points[player] = _points.GetValueOrDefault(player) + points;
    }

    // ---------------------------------------------------------------- the bots

    private async Task BotBuzzAsync(Guid bot, TriviaQuestion question, DateTimeOffset opensAt,
        Dictionary<Guid, ShoutOutBotPolicy.BuzzPlan> plans, CancellationToken ct)
    {
        try
        {
            ShoutOutBotPolicy.BuzzPlan plan = _bots.PlanBuzz(bot, question.Difficulty);
            lock (plans) plans[bot] = plan;
            if (plan.BuzzAfter is not { } after) return;

            TimeSpan wait = opensAt - _clock.UtcNow + after;
            if (wait > TimeSpan.Zero) await _clock.Delay(wait, ct);
            TryBuzz(bot, out _);
        }
        catch (OperationCanceledException)
        {
            // Someone else buzzed, or time ran out.
        }
    }

    private async Task BotAnswerAsync(Guid bot, TriviaQuestion question, IReadOnlyList<TriviaOption> options,
        ShoutOutBotPolicy.BuzzPlan plan, TimeSpan limit, CancellationToken ct)
    {
        try
        {
            await _clock.Delay(_bots.AnswerDelay(limit), ct);
            int pick = _bots.PickAnswer(plan.Knows, question.Correct, options.Count);
            TryAnswer(bot, options[pick].Id, out _);
        }
        catch (OperationCanceledException)
        {
            // Out of time: counts as wrong.
        }
    }

    // ---------------------------------------------------------------- buzzing and answering

    /// <summary>Buzzes in. The first buzz while buzzing is open wins; one before it opens locks the player out briefly.</summary>
    private bool TryBuzz(Guid playerId, out string? error)
    {
        DateTimeOffset now = _clock.UtcNow;
        TaskCompletionSource signal;
        bool human;
        lock (_gate)
        {
            error = null;
            if (_buzzer is not null) error = "Someone beat you to it!";
            else if (_phase != Phase.Open || _room.State.Stage != Stage.Question) error = "Buzzing is closed.";
            else if (_out.Contains(playerId)) error = "You already had a go at this one.";
            else if (_lockedUntil.TryGetValue(playerId, out DateTimeOffset until) && now < until) error = "Too early! Wait a moment.";
            else if (now < _buzzOpensAt)
            {
                _lockedUntil[playerId] = now.AddMilliseconds(_opt.EarlyBuzzLockoutMs);
                error = "Too early! Wait for the answers.";
            }
            if (error is not null) return false;

            _buzzer = playerId;
            _phase = Phase.Buzzed;
            signal = _signal;
            _signal = GameClock.NewSignal();
            human = _room.TryGetSeat(playerId, out PlayerSeat seat) && !seat.IsBot;
        }

        if (human) _bots.RecordHumanBuzz(now - _buzzOpensAt);
        signal.TrySetResult();
        return true;
    }

    private bool TryAnswer(Guid playerId, string? optionId, out string? error)
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            error = null;
            if (_phase != Phase.Buzzed || _room.State.Stage != Stage.Buzzed || _answer is not null) error = "It is not your turn to answer.";
            else if (_buzzer != playerId) error = "Someone else buzzed in first.";
            else if (optionId is null || _options.All(o => o.Id != optionId)) error = "Pick one of the answers.";
            if (error is not null) return false;

            _answer = optionId;
            signal = _signal;
        }

        signal.TrySetResult();
        return true;
    }

    // ---------------------------------------------------------------- helpers

    private Task CurrentSignal()
    {
        lock (_gate)
            return _signal.Task;
    }

    /// <summary>The category vote's progress (humans who are here, and every bot), ending it once everyone has voted.</summary>
    private async Task UpdateProgressAsync()
    {
        int done, total;
        TaskCompletionSource signal;
        lock (_gate)
        {
            if (_room.State.Stage != Stage.Categories) return;
            PlayerSeat[] required = _room.GetSeats().Where(s => s.IsBot || s.Connected).ToArray();
            total = required.Length;
            done = required.Count(s => _categoryVotes.ContainsKey(s.Id));
            signal = _signal;
        }

        await _broadcaster.Progress(_room, done, total);
        if (done >= total) signal.TrySetResult();
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

    /// <summary>Shout Out has no chat.</summary>
    public IReadOnlyList<ChatMessage> GetTranscript() => [];

    /// <summary>Shout Out never reveals who is a bot.</summary>
    public IReadOnlyList<Guid> GetUnmasked() => [];

    /// <summary>A player picks up to <see cref="ShoutOutOptions.MaxCategoryPicks"/> categories. They can change them until the vote ends.</summary>
    public async Task<string?> VoteCategoriesAsync(Guid playerId, IReadOnlyList<string>? categoryIds)
    {
        if (!_room.TryGetSeat(playerId, out PlayerSeat seat) || seat.IsBot) return "Unknown player.";
        if (_clock.IsPaused) return "The game is paused.";
        string[] ids = (categoryIds ?? []).Distinct().ToArray();
        if (ids.Length is 0) return "Pick at least one category.";
        if (ids.Length > _opt.MaxCategoryPicks) return $"Pick up to {_opt.MaxCategoryPicks}.";

        lock (_gate)
        {
            if (_room.State.Stage != Stage.Categories) return "The category vote is over.";
            if (ids.Any(id => _offered.All(c => c.Id != id))) return "Pick from the categories shown.";
            _categoryVotes[playerId] = ids;
        }

        await UpdateProgressAsync();
        return null;
    }

    public string? Buzz(Guid playerId)
    {
        if (!_room.TryGetSeat(playerId, out PlayerSeat seat) || seat.IsBot) return "Unknown player.";
        if (_clock.IsPaused) return "The game is paused.";
        return TryBuzz(playerId, out string? error) ? null : error;
    }

    public string? Answer(Guid playerId, string? optionId)
    {
        if (!_room.TryGetSeat(playerId, out PlayerSeat seat) || seat.IsBot) return "Unknown player.";
        if (_clock.IsPaused) return "The game is paused.";
        return TryAnswer(playerId, optionId, out string? error) ? null : error;
    }
}
