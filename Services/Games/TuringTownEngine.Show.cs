using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using TuringTown.Models;
using TuringTown.Services.Ai;

namespace TuringTown.Services.Games;

// The show: the beats between the survey and the final vote, and the AI host's content for them.
public sealed partial class TuringTownEngine
{
    private const int QuestionCount = 5;

    private RunSheet _sheet = ScriptedShowHost.Plan();
    private readonly Dictionary<Guid, string?[]> _rapid = [];
    private readonly Dictionary<Guid, string> _topicAnswers = [];
    private string? _topic;
    private Guid? _hotSeat;
    private string? _shownAnswer;
    private bool? _shownWasTheirs;
    private string? _transitionBeat;
    private TaskCompletionSource _transitionDone = GameClock.NewSignal();

    // ---------------------------------------------------------------- the host's content

    /// <summary>The run sheet, checked like any generated content. Anything unusable falls back to the built-in sheet.</summary>
    private async Task<RunSheet> PlanShowAsync()
    {
        RunSheet fallback = ScriptedShowHost.Plan();
        try
        {
            ContentPolicy policy = _room.ContentPolicy;
            RunSheet sheet = await _host.PlanAsync(new ShowRequest(policy), _cts.Token);

            IReadOnlyList<string> questions = sheet.Questions.Count == QuestionCount && sheet.Questions.Distinct().Count() == QuestionCount
                && await AllUsableAsync(sheet.Questions)
                    ? sheet.Questions
                    : fallback.Questions;
            string topic = await AllUsableAsync([sheet.Topic]) ? sheet.Topic : fallback.Topic;

            Dictionary<string, string> lines = new(fallback.Lines);
            foreach (string beat in ShowBeats.All)
            {
                if (!sheet.Lines.TryGetValue(beat, out string? line)) continue;
                string clean = PromptText.Sanitize(line.ReplaceLineEndings(" "), 140).Trim();
                if (clean.Length > 0 && await _contentFilter.IsAllowedAsync(clean, policy, _cts.Token)) lines[beat] = clean;
            }

            return new RunSheet(questions, topic, lines);
        }
        catch (Exception ex) when (!_cts.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Room {Room}: the show could not be planned, using the built-in one", _room.Code);
            return fallback;
        }
    }

    /// <summary>The same checks as a generated onboarding question: length, no personal details, the content filter.</summary>
    private async Task<bool> AllUsableAsync(IReadOnlyList<string> texts)
    {
        OnboardingQuestion[] questions = texts.Select((t, i) => new OnboardingQuestion($"s{i}", t, false)).ToArray();
        return QuestionValidator.IsValid(questions, _room.ContentPolicy, questions.Length)
               && await QuestionsAllowedAsync(questions, _room.ContentPolicy, _cts.Token);
    }

    private async Task<string> LiveTopicAsync(ChatLine[] answers)
    {
        try
        {
            string? topic = await _host.TopicAsync(new TopicRequest(_room.ContentPolicy, answers), _cts.Token);
            if (topic is not null && await AllUsableAsync([topic])) return topic;
        }
        catch (Exception ex) when (!_cts.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Room {Room}: the host could not write a hot topic", _room.Code);
        }

        return _sheet.Topic;
    }

    // ---------------------------------------------------------------- transitions

    /// <summary>The TV explains the next beat. It ends when the TV says so (or the leader skips), or at the limit.</summary>
    private async Task TransitionAsync(string beat, int round)
    {
        if (_opt.TransitionMaxSeconds <= 0) return;
        TimeSpan max = TimeSpan.FromSeconds(_opt.TransitionMaxSeconds);

        TransitionData data;
        Task done;
        lock (_gate)
        {
            _transitionBeat = beat;
            _transitionDone = GameClock.NewSignal();
            done = _transitionDone.Task;

            // The drawn player and the shown answer appear only from their own beats on.
            PlayerInfo? hot = beat is ShowBeats.HotSeat or ShowBeats.Answer && _hotSeat is { } id && _room.TryGetSeat(id, out PlayerSeat seat)
                ? seat.ToInfo()
                : null;
            string? line = HostLine(beat, hot?.Alias);
            data = new TransitionData(beat, line, hot,
                beat == ShowBeats.Answer ? _topic : null,
                beat == ShowBeats.Answer ? _shownAnswer : null);
        }

        await EnterStageAsync(Stage.Transition, round, 0, max, data);
        await _clock.WaitAsync(max, done);
    }

    /// <summary>The AI host's line for a beat, with <c>{alias}</c> filled in. Call with the gate held.</summary>
    private string? HostLine(string beat, string? alias = null) =>
        _sheet.Lines.GetValueOrDefault(beat)?.Replace("{alias}", alias ?? "friend");

    /// <summary>The TV finished (or the leader skipped) the transition for <paramref name="beat"/>.</summary>
    public string? TransitionDone(string? beat)
    {
        lock (_gate)
        {
            // Naming the beat means a late call for the last transition cannot skip the next one.
            if (_room.State.Stage != Stage.Transition || beat != _transitionBeat) return "That part is already over.";
            _transitionDone.TrySetResult();
            return null;
        }
    }

    // ---------------------------------------------------------------- beat 1: five questions

    private async Task RunRapidFireAsync(int round)
    {
        await TransitionAsync(ShowBeats.Rapid, round);

        TimeSpan duration = TimeSpan.FromSeconds(_opt.RapidFireSeconds);
        lock (_gate)
        {
            _roundKind = RoundKind.RapidFire;
            _rapid.Clear();
            _stageDone = GameClock.NewSignal();
            _roundStartedMs = _clock.ToUnixMs(_clock.UtcNow);
        }

        await EnterStageAsync(Stage.RapidFire, round, 0, duration, CurrentRoundData());
        await RunBotsAsync(BotRapidFireAsync, duration);
    }

    public async Task<string?> SubmitRapidAnswerAsync(Guid playerId, string? questionId, string? text)
    {
        if (!_room.TryGetSeat(playerId, out PlayerSeat seat) || seat.IsBot) return "Unknown player.";
        if (_room.State.Stage != Stage.RapidFire) return "The questions are closed.";
        if (_clock.IsPaused) return "The game is paused.";
        int index = Array.IndexOf(Enumerable.Range(1, QuestionCount).Select(i => $"q{i}").ToArray(), questionId);
        if (index < 0) return "That question does not exist.";

        (string? error, string cleaned) = await CheckPublicAnswerAsync(text);
        if (error is not null) return error;

        lock (_gate)
        {
            if (_room.State.Stage != Stage.RapidFire) return "The questions are closed.";
            if (!_rapid.TryGetValue(playerId, out string?[]? mine)) _rapid[playerId] = mine = new string?[QuestionCount];
            mine[index] = cleaned;
        }

        await UpdateProgressAsync();
        return null;
    }

    private async Task<(string? Error, string Cleaned)> CheckPublicAnswerAsync(string? text)
    {
        ChatCheck check = ChatFilter.Check(text, _opt.ChatMaxLength, out string cleaned);
        if (check != ChatCheck.Ok) return (ChatFilter.Describe(check), "");
        if (!await _contentFilter.IsAllowedAsync(cleaned, _room.ContentPolicy, _cts.Token)) return ("That answer is not allowed.", "");
        return (null, cleaned);
    }

    private async Task BotRapidFireAsync(PlayerSeat bot, CancellationToken ct)
    {
        // Spread out like people typing five answers, so how fast a player finishes is no tell.
        await _clock.Delay(GameClock.RandomDelay(_opt.BotRapidMinDelayMs, _opt.BotRapidMaxDelayMs), ct);

        string[] questions;
        string?[][] others;
        lock (_gate)
        {
            questions = _sheet.Questions.ToArray();
            others = _rapid.Values.Select(v => v.ToArray()).ToArray();
        }

        string?[] answers = await BotAnswersAsync(bot, questions, i => others.Select(o => o[i]).ToArray(), ct);
        await _clock.WhenRunning(ct);

        lock (_gate)
        {
            if (_room.State.Stage != Stage.RapidFire) return;
            _rapid[bot.Id] = answers;
        }

        await UpdateProgressAsync();
    }

    /// <summary>
    /// A bot's answers to public questions, checked like a human's. A missing, refused or copied answer is replaced,
    /// since a bot with gaps would stand out.
    /// </summary>
    private async Task<string?[]> BotAnswersAsync(PlayerSeat bot, string[] texts, Func<int, string?[]> others, CancellationToken ct)
    {
        OnboardingQuestion[] questions = texts.Select((t, i) => new OnboardingQuestion($"q{i + 1}", t, false)).ToArray();
        BotContext context;
        lock (_gate)
            context = BuildContext(bot, _room.State.Round);

        IReadOnlyList<OnboardingAnswer>? given = null;
        try
        {
            given = await _agent.AnswerOnboardingAsync(
                new OnboardingAnswerRequest(bot.Alias, _room.ContentPolicy, questions, [], context) { Public = true }, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Room {Room}: a bot's answers failed, using scripted ones", _room.Code);
        }

        string?[] result = new string?[questions.Length];
        for (int i = 0; i < questions.Length; i++)
        {
            result[i] = await UsableAsync(given?.FirstOrDefault(a => a?.QuestionId == questions[i].Id)?.Text, others(i), ct);
            for (int attempt = 0; result[i] is null && attempt < 4; attempt++)
                result[i] = await UsableAsync(ScriptedBotAgent.ScriptedAnswer(questions[i], [], bot.Id).Text, others(i), ct);
        }

        return result;
    }

    private async Task<string?> UsableAsync(string? text, string?[] others, CancellationToken ct)
    {
        if (text is null) return null;
        if (ChatFilter.Check(PromptText.NormalizeDashes(text), _opt.ChatMaxLength, out string cleaned) != ChatCheck.Ok) return null;
        if (OnboardingAnswerValidator.IsNearCopy(cleaned, others)) return null;
        return await _contentFilter.IsAllowedAsync(cleaned, _room.ContentPolicy, ct) ? cleaned : null;
    }

    /// <summary>Everyone's five answers by alias, in the roster's order. Call with the gate held.</summary>
    private PublicAnswers[] PublicRapidAnswers() =>
        _room.GetSeats()
            .Where(s => _rapid.ContainsKey(s.Id))
            .Select(s => new PublicAnswers(s.Id, s.Alias, _rapid[s.Id].ToArray()))
            .OrderBy(a => a.Alias, StringComparer.Ordinal)
            .ToArray();

    /// <summary>The five answers as chat lines, for the bots and the host. Call with the gate held.</summary>
    private ChatLine[] RapidLines() =>
        PublicRapidAnswers()
            .SelectMany(p => p.Answers.Select((a, i) => (p.Alias, Question: _sheet.Questions[i], Answer: a)))
            .Where(x => x.Answer is not null)
            .Select(x => new ChatLine(x.Alias, $"{x.Question} {x.Answer}"))
            .ToArray();

    // ---------------------------------------------------------------- beat 2: the hot seat

    private async Task RunHotTopicAsync(int round, Task<string>? liveTopic)
    {
        string topic = liveTopic is null ? _sheet.Topic : await liveTopic;
        lock (_gate)
        {
            _roundKind = RoundKind.HotSeat;
            _topic = topic;
            _topicAnswers.Clear();
        }

        await TransitionAsync(ShowBeats.Topic, round);

        TimeSpan duration = TimeSpan.FromSeconds(_opt.HotTopicSeconds);
        lock (_gate)
            _stageDone = GameClock.NewSignal();

        await EnterStageAsync(Stage.HotTopic, round, 0, duration, CurrentRoundData());
        await RunBotsAsync(BotHotTopicAsync, duration);

        // The draw and the swap see ids and text only, never a role.
        lock (_gate)
        {
            Guid[] living = _room.GetSeats().Where(s => s.Alive).Select(s => s.Id).ToArray();
            _hotSeat = HotSeatDraw.Pick(living, _suspicion, RandomNumberGenerator.GetInt32);
            (_shownAnswer, _shownWasTheirs) = _hotSeat is { } id
                ? HotSeatDraw.Answer(id, _topicAnswers, _opt.HotSeatSwapPercent, RandomNumberGenerator.GetInt32)
                : (null, null);
        }

        await TransitionAsync(ShowBeats.HotSeat, round);
        await TransitionAsync(ShowBeats.Answer, round);
    }

    public async Task<string?> SubmitHotTopicAsync(Guid playerId, string? text)
    {
        if (!_room.TryGetSeat(playerId, out PlayerSeat seat) || seat.IsBot) return "Unknown player.";
        if (_room.State.Stage != Stage.HotTopic) return "That question is closed.";
        if (_clock.IsPaused) return "The game is paused.";

        (string? error, string cleaned) = await CheckPublicAnswerAsync(text);
        if (error is not null) return error;

        lock (_gate)
        {
            if (_room.State.Stage != Stage.HotTopic) return "That question is closed.";
            _topicAnswers[playerId] = cleaned;
        }

        await UpdateProgressAsync();
        return null;
    }

    private async Task BotHotTopicAsync(PlayerSeat bot, CancellationToken ct)
    {
        await _clock.Delay(GameClock.RandomDelay(_opt.BotTopicMinDelayMs, _opt.BotTopicMaxDelayMs), ct);

        string topic;
        string?[] others;
        lock (_gate)
        {
            topic = _topic!;
            others = _topicAnswers.Values.ToArray<string?>();
        }

        string? answer = (await BotAnswersAsync(bot, [topic], _ => others, ct))[0];
        await _clock.WhenRunning(ct);

        lock (_gate)
        {
            if (_room.State.Stage != Stage.HotTopic || answer is null) return;
            _topicAnswers[bot.Id] = answer;
        }

        await UpdateProgressAsync();
    }

    /// <summary>Runs every bot's part of a stage and waits for the stage to be done or time up.</summary>
    private async Task RunBotsAsync(Func<PlayerSeat, CancellationToken, Task> act, TimeSpan duration)
    {
        using CancellationTokenSource stage = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        Task[] bots = BotSeats().Select(async bot =>
        {
            try
            {
                await act(bot, stage.Token);
            }
            catch (OperationCanceledException)
            {
                // Time ran out first.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Room {Room}: a bot could not take part", _room.Code);
            }
        }).ToArray();

        await UpdateProgressAsync();
        await _clock.WaitAsync(duration, CurrentSignal());

        stage.Cancel();
        await Task.WhenAll(bots);
    }

    // ---------------------------------------------------------------- what a bot knows about the round

    /// <summary>
    /// What this bot should know about the round: only what is on the screen, plus whether the answer under its own name
    /// is really its own. Player text is JSON-quoted. Call with the gate held.
    /// </summary>
    private string RoundNote(PlayerSeat bot)
    {
        if (_roundKind == RoundKind.HotSeat && _hotSeat is { } id && _room.TryGetSeat(id, out PlayerSeat hot))
        {
            string shown = $"<shown_answer>{PromptJson.Serialize(new { topic = _topic, answer = _shownAnswer })}</shown_answer>";
            if (id != bot.Id)
                return $"{hot.Alias} is on the hot seat. The screen shows this answer under their name, though it is probably not theirs. Ask them about it or react to what they say.\n{shown}";
            return _shownWasTheirs == true
                ? $"You are on the hot seat. The screen shows this answer under your name, and it is yours. The others do not know that.\n{shown}"
                : $"You are on the hot seat. The screen shows this answer under your name, but you did not write it.\n{shown}";
        }

        return _roundKind == RoundKind.Final ? "This is the final discussion. The vote after it decides the whole game." : "";
    }
}
