using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TuringTown.Models;
using TuringTown.Services.Ai;
using TuringTown.Services.Storage;

namespace TuringTown.Playground;

/// <summary>
/// Chat with the bots outside the game. You play the humans; after every line you send, each living bot gets one turn
/// (and may stay quiet), just as it would in a discussion. Commands start with "/" (see <see cref="Help"/>). Input comes
/// from the keyboard, a pipe or <c>--script</c>, so a person and an agent can both drive it.
/// </summary>
internal static class PlaygroundApp
{
    internal const string Help = """
        Type a line to say it as the current human. Commands:
          /as <alias|number>   speak as another human (a new alias adds a human)
          /bots [n]            let every bot take n turns without anyone speaking (default 1)
          /vote                every bot votes
          /out <alias>         vote that player out: ends the round, the bots write their notes
          /round               end the round with nobody voted out
          /players             who is in the game (roles hidden)   /who   reveal the roles
          /persona             the bots' onboarding answers         /notes  the bots' private notes
          /prompt              the last prompt sent to the model    /raw    toggle raw model replies
          /help                this help                            /quit   leave
        Lines starting with # are comments (for scripts).
        """;

    private static async Task<int> Main(string[] args)
    {
        Options opt;
        try
        {
            opt = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }
        if (opt.ShowUsage)
        {
            Console.WriteLine(Options.Usage);
            return 0;
        }

        Agents agents;
        try
        {
            agents = Agents.Build(opt);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not set up the {opt.Provider} bots: {ex.Message}");
            return 1;
        }

        Out.Note($"Bots: {agents.Description}. Tone: {(opt.Policy.FamilyFriendly ? "family" : "mature")}.");

        if (opt.Openers > 0)
        {
            await OpenersAsync(opt, agents);
            return 0;
        }

        Session session = await Session.StartAsync(opt, agents);
        Out.Note("Type /help for commands.");

        TextReader input = opt.Script is null ? Console.In : new StreamReader(opt.Script);
        bool echo = opt.Script is not null || Console.IsInputRedirected;
        while (true)
        {
            if (!echo) Console.Write($"{session.Speaker}> ");
            string? line = await input.ReadLineAsync();
            if (line is null) break;
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (echo && line.StartsWith('/')) Out.Note(line);
            else if (echo) Out.Human(session.Speaker, line);
            if (!await session.HandleAsync(line)) break;
        }

        agents.PrintUsage();
        return 0;
    }

    /// <summary>
    /// Many fresh games, and each bot's first line into an empty chat. Shows at a glance whether openers repeat.
    /// </summary>
    private static async Task OpenersAsync(Options opt, Agents agents)
    {
        List<string> lines = [];
        for (int game = 1; game <= opt.Openers; game++)
        {
            Session session = await Session.StartAsync(opt with { Quiet = true }, agents);
            foreach (string? line in await session.OpeningLinesAsync())
            {
                Console.WriteLine($"game {game,2}: {line ?? "(quiet)"}");
                if (line is not null) lines.Add(line);
            }
        }

        int distinct = lines.Select(l => new string(l.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray())).Distinct().Count();
        Out.Note($"{lines.Count} opening lines, {distinct} different.");
        agents.PrintUsage();
    }
}

/// <summary>Command-line options.</summary>
internal sealed record Options(AiProvider Provider, int Humans, int Bots, ContentPolicy Policy, string? Script, int Openers, bool ShowPrompts, bool ShowUsage, bool Pace = true, bool Quiet = false)
{
    public const string Usage = """
        dotnet run --project TuringTown.Playground -- [options]
          --provider scripted|foundry   which bots (default: Ai:Provider from appsettings and user secrets)
          --humans <n>                  how many humans you play (default 3)
          --bots <n>                    how many bots (default 1)
          --mature                      mature tone (default family friendly)
          --script <file>               read the input from a file instead of the keyboard
          --openers <n>                 play n fresh games and print each bot's first line, then stop
          --show-prompts                print every prompt sent to the model
          --no-pace                     let bots speak on every turn (the game holds a bot to about 1.3x a human's lines)
        """;

    public static Options Parse(string[] args)
    {
        AiProvider? provider = null;
        int humans = 3, bots = 1, openers = 0;
        bool mature = false, prompts = false, usage = false, pace = true;
        string? script = null;

        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            int Count(int min, int max)
            {
                string flag = args[i];
                return int.TryParse(Next(), out int n) && n >= min && n <= max ? n : throw new ArgumentException($"{flag} must be {min}-{max}.");
            }

            switch (args[i])
            {
                case "--provider":
                    provider = Enum.TryParse(Next(), ignoreCase: true, out AiProvider p) ? p : throw new ArgumentException("--provider is scripted or foundry.");
                    break;
                case "--humans": humans = Count(1, 8); break;
                case "--bots": bots = Count(1, 4); break;
                case "--openers": openers = Count(1, 100); break;
                case "--mature": mature = true; break;
                case "--show-prompts": prompts = true; break;
                case "--no-pace": pace = false; break;
                case "--script":
                    script = Next();
                    if (!File.Exists(script)) throw new ArgumentException($"There is no script file {script}.");
                    break;
                case "--help" or "-h": usage = true; break;
                default: throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }

        return new Options(provider ?? Config.Load().GetSection("Ai").Get<AiOptions>()?.Provider ?? AiProvider.Scripted,
            humans, bots, new ContentPolicy(!mature), script, openers, prompts, usage, pace);
    }
}

/// <summary>The app's own configuration: the repo's appsettings.json, then the app's user secrets, then the environment.</summary>
internal static class Config
{
    public static IConfiguration Load()
    {
        ConfigurationBuilder builder = new();
        string? settings = FindAppSettings();
        if (settings is not null) builder.AddJsonFile(settings, optional: true);
        // The same user secrets as the server (its assembly carries the UserSecretsId).
        builder.AddUserSecrets(typeof(AiOptions).Assembly, optional: true);
        builder.AddEnvironmentVariables();
        return builder.Build();
    }

    private static string? FindAppSettings()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "appsettings.json");
            if (File.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "TuringTown.csproj"))) return candidate;
        }
        return null;
    }
}

/// <summary>The bot agent, its memory store, and (on the real model) a recorder for the prompts and replies.</summary>
internal sealed class Agents
{
    private Agents(IBotAgent bot, string description, IBotMemoryStore store, RecordingLlm? llm)
    {
        Bot = bot;
        Description = description;
        Store = store;
        Llm = llm;
    }

    public IBotAgent Bot { get; }
    public string Description { get; }
    public IBotMemoryStore Store { get; }
    public RecordingLlm? Llm { get; }

    public static Agents Build(Options opt)
    {
        InMemoryBotMemoryStore store = new();
        if (opt.Provider != AiProvider.Foundry) return new Agents(new ScriptedBotAgent(), "scripted (offline)", store, null);

        IConfiguration config = Config.Load();
        AiOptions ai = config.GetSection("Ai").Get<AiOptions>() ?? new AiOptions();
        ai.Provider = AiProvider.Foundry;
        FoundryOptions foundry = config.GetSection("Foundry").Get<FoundryOptions>() ?? new FoundryOptions();

        ValidateOptionsResult check = new AiOptionsValidator(Microsoft.Extensions.Options.Options.Create(foundry)).Validate(null, ai);
        if (check.Failed) throw new InvalidOperationException(string.Join(" ", check.Failures!));

        IOptions<AiOptions> aiOptions = Microsoft.Extensions.Options.Options.Create(ai);
        RecordingLlm llm = new(new FoundryLlmClient(Microsoft.Extensions.Options.Options.Create(foundry), aiOptions), opt.ShowPrompts);
        GuardedLlmClient guarded = new(llm, new AiBudget(aiOptions, TimeProvider.System), NullLogger<GuardedLlmClient>.Instance);

        // No fallback on purpose: the playground shows a failure instead of hiding it.
        FoundryBotAgent bot = new(guarded, store, new BotPrompts(), aiOptions, NullLogger<FoundryBotAgent>.Instance);
        return new Agents(bot, $"Foundry, chat effort {ai.Calls.Chat.ReasoningEffort}, vote effort {ai.Calls.Vote.ReasoningEffort}", store, llm);
    }

    private readonly GuardTally _guard = new();

    public void PrintUsage()
    {
        if (Llm is null) return;
        Out.Note($"Model calls: {Llm.Calls}, tokens in {Llm.InputTokens} (cached {Llm.CachedTokens}), out {Llm.OutputTokens} (reasoning {Llm.ReasoningTokens}).");
        foreach (string line in Llm.LatencyLines()) Out.Note(line);
        Out.Note($"Guard rejects: {_guard.Summary()}");
    }
}

/// <summary>Remembers the last prompt and reply, and counts tokens.</summary>
internal sealed class RecordingLlm(ILlmClient inner, bool showPrompts) : ILlmClient
{
    public LlmCall? LastCall { get; private set; }
    public string? LastReply { get; private set; }
    public bool ShowRaw { get; set; }
    public int Calls, InputTokens, CachedTokens, OutputTokens, ReasoningTokens;
    private readonly List<(string Kind, double Seconds, string? Failure)> _timings = [];

    public async Task<LlmResult> CompleteAsync(LlmCall call, CancellationToken ct)
    {
        LastCall = call;
        if (showPrompts) Out.Prompt(call.Kind, call.User);
        Stopwatch watch = Stopwatch.StartNew();
        LlmResult result;
        try
        {
            result = await inner.CompleteAsync(call, ct);
        }
        catch (Exception ex)
        {
            lock (_timings) _timings.Add((call.Kind, watch.Elapsed.TotalSeconds, ex.GetType().Name));
            throw;
        }
        lock (_timings) _timings.Add((call.Kind, watch.Elapsed.TotalSeconds, null));
        LastReply = result.Text;
        Interlocked.Increment(ref Calls);
        Interlocked.Add(ref InputTokens, result.InputTokens);
        Interlocked.Add(ref CachedTokens, result.CachedTokens);
        Interlocked.Add(ref OutputTokens, result.OutputTokens);
        Interlocked.Add(ref ReasoningTokens, result.ReasoningTokens);
        if (ShowRaw) Out.Note($"raw {call.Kind}: {result.Text}");
        return result;
    }

    /// <summary>Per kind of call: how many, median, p90 and max seconds, and the failures by type.</summary>
    public IEnumerable<string> LatencyLines()
    {
        (string Kind, double Seconds, string? Failure)[] all;
        lock (_timings) all = _timings.ToArray();
        foreach (IGrouping<string, (string Kind, double Seconds, string? Failure)> kind in all.GroupBy(t => t.Kind).OrderBy(g => g.Key))
        {
            double[] s = kind.Select(t => t.Seconds).Order().ToArray();
            string failures = string.Join(", ", kind.Where(t => t.Failure is not null).GroupBy(t => t.Failure).Select(g => $"{g.Key} x{g.Count()}"));
            yield return $"Latency {kind.Key}: {s.Length} calls, median {s[s.Length / 2]:0.0}s, p90 {s[Math.Min(s.Length - 1, (int)(s.Length * 0.9))]:0.0}s, max {s[^1]:0.0}s"
                + (failures.Length > 0 ? $", failed: {failures}" : "");
        }
    }
}

/// <summary>Counts the guard's rejections by reason, from the AI code's own metrics (the same counters production reports).</summary>
internal sealed class GuardTally : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, long> _counts = [];

    public GuardTally()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == AiMetrics.MeterName && instrument.Name == "ai.guard_rejects") listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string reason = "";
            foreach (KeyValuePair<string, object?> tag in tags)
                if (tag.Key == "reason") reason = tag.Value?.ToString() ?? "";
            lock (_counts) _counts[reason] = _counts.GetValueOrDefault(reason) + value;
        });
        _listener.Start();
    }

    public string Summary()
    {
        lock (_counts)
            return _counts.Count == 0 ? "none" : string.Join(", ", _counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} x{kv.Value}"));
    }

    public void Dispose() => _listener.Dispose();
}

/// <summary>One Turing Town discussion: the seats, the chat, and the bots' personas, lines and notes.</summary>
internal sealed class Session
{
    private sealed class Seat(string alias, bool isBot)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Alias { get; } = alias;
        public bool IsBot { get; } = isBot;
        public bool Alive { get; set; } = true;
    }

    private const int ContextLines = 25;
    private const int OwnLinesContext = 5;
    private const int ChatMaxLength = 200;

    private readonly Options _opt;
    private readonly Agents _agents;
    private readonly Guid _gameId = Guid.NewGuid();
    private readonly List<Seat> _seats = [];
    private readonly List<ChatLine> _chat = [];
    private readonly List<RoundOutcome> _history = [];
    private readonly Dictionary<Guid, IReadOnlyList<PersonaAnswer>> _personas = [];
    private readonly Dictionary<Guid, List<string>> _own = [];
    private readonly Dictionary<Guid, List<ChatLine>> _mentions = [];
    private StyleProfile _style = StyleProfile.Empty;
    private int _round = 1;
    private int _roundStart;
    private IReadOnlyList<VoteCount> _lastTally = [];
    private readonly Dictionary<Guid, int> _postedThisRound = [];
    private int _humanLinesThisRound;

    // The engine's defaults (TuringTownOptions.BotMessagesPerRound, and the 1.3x of PaceBudget).
    private const int BotMessagesPerRound = 5;
    private const double PaceFactor = 1.3;

    private Session(Options opt, Agents agents)
    {
        _opt = opt;
        _agents = agents;
    }

    public string Speaker { get; private set; } = "";

    private IEnumerable<Seat> Bots => _seats.Where(s => s.IsBot && s.Alive);

    public static async Task<Session> StartAsync(Options opt, Agents agents)
    {
        Session s = new(opt, agents);
        // Bots and humans in a random order, as in the game, so a seat number gives nothing away.
        foreach (bool isBot in Enumerable.Repeat(false, opt.Humans).Concat(Enumerable.Repeat(true, opt.Bots)).OrderBy(_ => Random.Shared.Next()))
            s._seats.Add(new Seat(Aliases.PickRandom(s._seats.Select(x => x.Alias)).Name, isBot));
        s.Speaker = s._seats.First(x => !x.IsBot).Alias;

        await s.OnboardAsync();
        if (!opt.Quiet) s.PrintPlayers();
        return s;
    }

    /// <summary>The form: the humans get scripted answers, then the bots answer like in the game (they see the humans' answers).</summary>
    private async Task OnboardAsync()
    {
        IReadOnlyList<OnboardingQuestion> questions = ScriptedBotAgent.BuiltInQuestions(_opt.Policy, new QuestionMix(2, 1, 1));
        List<OnboardingAnswer> human = [];
        foreach (Seat _ in _seats.Where(x => !x.IsBot))
            human.AddRange(questions.Select(q => ScriptedBotAgent.ScriptedAnswer(q, human)));
        _style = StyleProfile.From(questions, human);

        await Task.WhenAll(Bots.Select(async bot =>
        {
            IReadOnlyList<OnboardingAnswer> given;
            try
            {
                given = await _agents.Bot.AnswerOnboardingAsync(new OnboardingAnswerRequest(bot.Alias, _opt.Policy, questions, human, Context(bot, round: 0)), CancellationToken.None);
            }
            catch (Exception ex)
            {
                Out.Error($"{bot.Alias}'s onboarding failed ({ex.Message}); using scripted answers.");
                given = [];
            }

            OnboardingAnswer[] answers = questions
                .Select(q => given.FirstOrDefault(a => a.QuestionId == q.Id) ?? ScriptedBotAgent.ScriptedAnswer(q, human, bot.Id))
                .ToArray();
            lock (_personas) _personas[bot.Id] = Persona(questions, answers);
        }));
    }

    private static PersonaAnswer[] Persona(IReadOnlyList<OnboardingQuestion> questions, IReadOnlyList<OnboardingAnswer> answers) =>
        questions.Zip(answers, (q, a) => new PersonaAnswer(q.Text, q.Kind, a.Text,
            q.Options.Where(o => a.OptionIds?.Contains(o.Id) == true).Select(o => o.Text).ToArray())).ToArray();

    private BotContext Context(Seat bot, int? round = null) =>
        new(new BotKey(_gameId, bot.Id), bot.Alias, _personas.GetValueOrDefault(bot.Id, []), round ?? _round,
            _seats.Where(s => s.Alive && s.Id != bot.Id).Select(s => s.Alias).ToArray(),
            _history.ToArray(),
            _own.GetValueOrDefault(bot.Id, []).TakeLast(OwnLinesContext).Select(t => new ChatLine(bot.Alias, t)).ToArray(),
            (round ?? _round) == 0 ? _style : _style.WithoutAnswers(),
            ChatMaxLength);

    public async Task<string?[]> OpeningLinesAsync()
    {
        List<string?> lines = [];
        foreach (Seat bot in Bots.ToArray()) lines.Add(await BotTurnAsync(bot, print: false));
        return lines.ToArray();
    }

    /// <summary>Handles one line of input. False means quit.</summary>
    public async Task<bool> HandleAsync(string line)
    {
        if (!line.StartsWith('/'))
        {
            Say(Speaker, line, bot: false);
            await BotsRespondAsync();
            return true;
        }

        string[] parts = line.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string arg = parts.Length > 1 ? parts[1] : "";
        switch (parts[0].ToLowerInvariant())
        {
            case "/quit" or "/exit": return false;
            case "/help": Console.WriteLine(PlaygroundApp.Help); break;
            case "/as": SwitchSpeaker(arg); break;
            case "/bots":
                int turns = int.TryParse(arg, out int n) && n > 0 ? Math.Min(n, 20) : 1;
                for (int i = 0; i < turns; i++) await BotsRespondAsync();
                break;
            case "/vote": await VoteAsync(); break;
            case "/out": await OutAsync(arg); break;
            case "/round": await EndRoundAsync(null); break;
            case "/players": PrintPlayers(); break;
            case "/who":
                foreach (Seat s in _seats) Out.Note($"{s.Alias}: {(s.IsBot ? "BOT" : "human")}{(s.Alive ? "" : " (out)")}");
                break;
            case "/persona":
                foreach (Seat b in _seats.Where(s => s.IsBot))
                foreach (PersonaAnswer p in _personas.GetValueOrDefault(b.Id, []))
                    Out.Note($"{b.Alias} | {p.Question} -> {(p.Kind == QuestionKind.Text ? p.Text : string.Join(", ", p.ChosenOptions))}");
                break;
            case "/notes":
                foreach (Seat b in Bots)
                {
                    BotMemory? m = await _agents.Store.LoadAsync(new BotKey(_gameId, b.Id), CancellationToken.None);
                    Out.Note(m is null
                        ? $"{b.Alias}: no notes yet (they are written at the end of a round)"
                        : $"{b.Alias}: {m.Summary}\n  suspects: {string.Join("; ", m.Suspicions.Select(x => $"{x.Alias} {x.Score:0.0} {x.Why}"))}\n  claims: {string.Join("; ", m.Claims)}");
                }
                break;
            case "/prompt":
                if (_agents.Llm?.LastCall is { } call)
                {
                    Out.Prompt(call.Kind + " (system)", call.System);
                    Out.Prompt(call.Kind + " (user)", call.User);
                    Out.Note($"reply: {_agents.Llm.LastReply}");
                }
                else Out.Note("No model call yet (the scripted bots make none).");
                break;
            case "/raw":
                if (_agents.Llm is null) Out.Note("The scripted bots make no model calls.");
                else Out.Note($"Raw replies {((_agents.Llm.ShowRaw = !_agents.Llm.ShowRaw) ? "on" : "off")}.");
                break;
            default: Out.Error($"Unknown command {parts[0]}. Type /help."); break;
        }
        return true;
    }

    private void SwitchSpeaker(string arg)
    {
        Seat[] humans = _seats.Where(s => !s.IsBot && s.Alive).ToArray();
        Seat? pick = int.TryParse(arg, out int n) && n >= 1 && n <= humans.Length
            ? humans[n - 1]
            : humans.FirstOrDefault(h => h.Alias.Equals(arg, StringComparison.OrdinalIgnoreCase));

        if (pick is null && arg.Length > 0)
        {
            if (_seats.Any(s => s.Alias.Equals(arg, StringComparison.OrdinalIgnoreCase)))
            {
                Out.Error($"{arg} is not a living human you play.");
                return;
            }
            pick = new Seat(arg, isBot: false);
            _seats.Add(pick);
            Out.Note($"{arg} joins as a human.");
        }

        if (pick is null) Out.Error("Usage: /as <alias or number>");
        else Speaker = pick.Alias;
    }

    private void Say(string alias, string text, bool bot)
    {
        ChatLine line = new(alias, text);
        _chat.Add(line);
        // Queue the line for every bot it @mentions, as the engine does.
        foreach (Seat b in Bots.Where(b => b.Alias != alias && text.Contains("@" + b.Alias, StringComparison.OrdinalIgnoreCase)))
        {
            if (!_mentions.TryGetValue(b.Id, out List<ChatLine>? list)) _mentions[b.Id] = list = [];
            list.Add(line);
        }
        if (bot) Out.Bot(alias, text);
        else _humanLinesThisRound++;
    }

    /// <summary>
    /// How many lines a bot may have said this round, as the engine's PaceBudget with BotPaceToHumans on (the real-AI
    /// default): about 1.3x what a living human has said, at least one, at most BotMessagesPerRound.
    /// </summary>
    private int PaceBudget()
    {
        if (!_opt.Pace) return int.MaxValue;
        int living = Math.Max(1, _seats.Count(s => !s.IsBot && s.Alive));
        return Math.Clamp((int)Math.Ceiling(_humanLinesThisRound / (double)living * PaceFactor), 1, BotMessagesPerRound);
    }

    /// <summary>Every living bot gets one turn, the ones that were @mentioned first.</summary>
    private async Task BotsRespondAsync()
    {
        foreach (Seat bot in Bots.OrderByDescending(b => _mentions.GetValueOrDefault(b.Id)?.Count > 0).ThenBy(_ => Random.Shared.Next()).ToArray())
        {
            // As in the engine: a bot that has said its share waits, unless someone @mentioned it.
            if (_postedThisRound.GetValueOrDefault(bot.Id) >= PaceBudget() && !_mentions.ContainsKey(bot.Id))
            {
                Out.Note($"{bot.Alias} waits (pacing: it has said its share this round; --no-pace turns this off)");
                continue;
            }
            await BotTurnAsync(bot, print: true);
        }
    }

    private async Task<string?> BotTurnAsync(Seat bot, bool print)
    {
        ChatLine[] mentions = _mentions.Remove(bot.Id, out List<ChatLine>? pending) ? pending.ToArray() : [];
        BotContext ctx = Context(bot);
        ChatRequest request = new(bot.Alias, "", _opt.Policy, _round, _chat.TakeLast(ContextLines).ToArray(), mentions, ctx.AlivePlayers, ctx);

        Stopwatch watch = Stopwatch.StartNew();
        string? line;
        try
        {
            line = await _agents.Bot.NextChatMessageAsync(request, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Out.Error($"{bot.Alias}'s chat call failed after {watch.Elapsed.TotalSeconds:0.0}s: {ex.Message} (in a game: silence)");
            return null;
        }

        if (line is null)
        {
            if (print) Out.Note($"{bot.Alias} stays quiet ({watch.Elapsed.TotalSeconds:0.0}s)");
            return null;
        }

        line = PromptText.NormalizeDashes(line.Trim());
        if (!_own.TryGetValue(bot.Id, out List<string>? own)) _own[bot.Id] = own = [];
        own.Add(line);
        _postedThisRound[bot.Id] = _postedThisRound.GetValueOrDefault(bot.Id) + 1;
        if (print)
        {
            Say(bot.Alias, line, bot: true);
            Out.Note($"  ({watch.Elapsed.TotalSeconds:0.0}s)");
        }
        else _chat.Add(new ChatLine(bot.Alias, line));
        return line;
    }

    private async Task VoteAsync()
    {
        Dictionary<string, int> tally = [];
        foreach (Seat bot in Bots.ToArray())
        {
            VoteCandidate[] candidates = _seats.Where(s => s.Alive && s.Id != bot.Id).Select(s => new VoteCandidate(s.Id, s.Alias)).ToArray();
            try
            {
                Guid? choice = await _agents.Bot.CastVoteAsync(
                    new VoteRequest(bot.Alias, "", _opt.Policy, _round, candidates, _chat.TakeLast(ContextLines).ToArray(), Context(bot)), CancellationToken.None);
                string target = candidates.FirstOrDefault(c => c.Id == choice).Alias ?? "ABSTAIN";
                Out.Note($"{bot.Alias} votes {target}");
                if (choice is not null) tally[target] = tally.GetValueOrDefault(target) + 1;
            }
            catch (Exception ex)
            {
                Out.Error($"{bot.Alias}'s vote failed: {ex.Message} (in a game: the scripted fallback votes)");
            }
        }
        _lastTally = tally.Select(kv => new VoteCount(kv.Key, kv.Value)).ToArray();
        Out.Note("Add the humans' votes yourself, then /out <alias> or /round.");
    }

    private async Task OutAsync(string alias)
    {
        Seat? seat = _seats.FirstOrDefault(s => s.Alive && s.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));
        if (seat is null)
        {
            Out.Error("Usage: /out <alias of a living player>");
            return;
        }
        seat.Alive = false;
        Out.Note($"{seat.Alias} is voted out and was {(seat.IsBot ? "a BOT" : "a human")}.");
        await EndRoundAsync(seat);

        if (!Bots.Any()) Out.Note("No bot is left: the humans win. Keep chatting or /quit.");
        if (!_seats.Any(s => !s.IsBot && s.Alive && s.Alias == Speaker))
            Speaker = _seats.FirstOrDefault(s => !s.IsBot && s.Alive)?.Alias ?? Speaker;
    }

    /// <summary>Records the round as the Reveal would show it, then lets each bot write its notes (as the engine does).</summary>
    private async Task EndRoundAsync(Seat? eliminated)
    {
        _history.Add(new RoundOutcome(_round, eliminated?.Alias, eliminated?.IsBot, _lastTally));
        ChatLine[] transcript = _chat.Skip(_roundStart).ToArray();

        await Task.WhenAll(Bots.Select(async bot =>
        {
            try
            {
                await _agents.Bot.EndRoundAsync(new RoundEndRequest(Context(bot), _opt.Policy, transcript), CancellationToken.None);
            }
            catch (Exception ex)
            {
                Out.Error($"{bot.Alias}'s round notes failed: {ex.Message}");
            }
        }));

        _round++;
        _roundStart = _chat.Count;
        _lastTally = [];
        _postedThisRound.Clear();
        _humanLinesThisRound = 0;
        Out.Note($"--- round {_round} ---");
    }

    private void PrintPlayers()
    {
        Seat[] humans = _seats.Where(s => !s.IsBot && s.Alive).ToArray();
        Out.Note($"Players: {string.Join(", ", _seats.Where(s => s.Alive).Select(s => s.Alias))}");
        Out.Note($"You play: {string.Join(", ", humans.Select((h, i) => $"{i + 1}={h.Alias}"))}. Speaking as {Speaker}.");
    }
}

/// <summary>Console output with a little colour (ignored when redirected).</summary>
internal static class Out
{
    private static readonly object Gate = new();

    public static void Bot(string alias, string text) => Write(ConsoleColor.Cyan, $"{alias}: {text}");
    public static void Human(string alias, string text) => Write(ConsoleColor.White, $"{alias}: {text}");
    public static void Note(string text) => Write(ConsoleColor.DarkGray, text);
    public static void Error(string text) => Write(ConsoleColor.Red, text);
    public static void Prompt(string title, string text) => Write(ConsoleColor.DarkYellow, $"===== {title}\n{text}\n=====");

    private static void Write(ConsoleColor color, string text)
    {
        lock (Gate)
        {
            ConsoleColor before = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = before;
        }
    }
}
