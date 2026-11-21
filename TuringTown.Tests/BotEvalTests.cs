using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TuringTown.Models;
using TuringTown.Services.Ai;
using TuringTown.Services.Storage;
using Xunit.Abstractions;

namespace TuringTown.Tests;

/// <summary>
/// A test that only runs when asked, because it calls the real model: set <c>TURINGTOWN_EVAL=1</c> and have the Foundry
/// settings (the existing user secrets, or <c>Foundry__Endpoint</c> and friends as environment variables). CI never runs it.
/// </summary>
public sealed class EvalFactAttribute : FactAttribute
{
    public EvalFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TURINGTOWN_EVAL") != "1")
            Skip = "Evaluation against the real model. Set TURINGTOWN_EVAL=1 (and the Foundry settings) to run it.";
    }
}

/// <summary>
/// How good are the real bots? Two measurements, both printed to the test output:
/// <list type="bullet">
/// <item>Simulated games with scripted humans: how long the bot survives, how it writes next to the humans, and how
/// often its output was thrown away or fell back.</item>
/// <item>The injection suite against the real model: how often it obeys a planted instruction, and a hard check that it never
/// writes the canary.</item>
/// </list>
/// Nothing is asserted about "was the bot caught": that depends on the model and the prompts and is for a person to read.
/// Run it before and after changing a prompt (the version is in every template) and compare.
/// </summary>
public class BotEvalTests(ITestOutputHelper output)
{
    /// <summary>Remembers what the real model said, before the guard.</summary>
    private sealed class RecordingLlm(ILlmClient inner) : ILlmClient
    {
        private readonly List<(string Kind, string Text)> _replies = [];

        public IReadOnlyList<(string Kind, string Text)> Replies
        {
            get
            {
                lock (_replies)
                    return _replies.ToArray();
            }
        }

        public string LastRaw => Replies.LastOrDefault().Text ?? "";

        public async Task<LlmResult> CompleteAsync(LlmCall call, CancellationToken ct)
        {
            LlmResult result = await inner.CompleteAsync(call, ct);
            lock (_replies) _replies.Add((call.Kind, result.Text));
            return result;
        }
    }

    private sealed record Real(FoundryBotAgent Bot, FoundryQuestionGenerator Questions, RecordingLlm Llm, AiBudget Budget, AiOptions Ai, InMemoryBotMemoryStore Store);

    private static Real BuildReal()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddUserSecrets<Program>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        AiOptions ai = config.GetSection("Ai").Get<AiOptions>() ?? new AiOptions();
        ai.Provider = AiProvider.Foundry;
        FoundryOptions foundry = config.GetSection("Foundry").Get<FoundryOptions>() ?? new FoundryOptions();

        ValidateOptionsResult check = new AiOptionsValidator(Options.Create(foundry)).Validate(null, ai);
        if (check.Failed) throw new InvalidOperationException(string.Join(" ", check.Failures!));

        AiBudget budget = new(Options.Create(ai), TimeProvider.System);
        RecordingLlm llm = new(new FoundryLlmClient(Options.Create(foundry), Options.Create(ai)));
        GuardedLlmClient guarded = new(llm, budget, NullLogger<GuardedLlmClient>.Instance);

        BotPrompts prompts = new();
        InMemoryBotMemoryStore store = new();
        return new Real(
            new FoundryBotAgent(guarded, store, prompts, Options.Create(ai), NullLogger<FoundryBotAgent>.Instance),
            new FoundryQuestionGenerator(guarded, prompts, Options.Create(ai)),
            llm, budget, ai, store);
    }

    /// <summary>Adds up the counters the AI code reports, so a run can say how often it fell back or threw a line away.</summary>
    private sealed class Tally : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, long> _counts = [];

        public Tally()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == AiMetrics.MeterName) listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                string key = instrument.Name + string.Concat(tags.ToArray().Select(t => $" {t.Key}={t.Value}"));
                lock (_counts) _counts[key] = _counts.GetValueOrDefault(key) + value;
            });
            _listener.Start();
        }

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_counts)
                    return _counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}: {kv.Value}").ToArray();
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    // ---------------------------------------------------------------- diagnostics

    /// <summary>
    /// For when the real model answers 404 or 401: says what KIND of endpoint and deployment name are configured, without
    /// ever printing a value (they are secrets).
    /// </summary>
    [EvalFact]
    public void Show_the_shape_of_the_foundry_settings_without_showing_them()
    {
        IConfiguration config = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().Build();
        FoundryOptions f = config.GetSection("Foundry").Get<FoundryOptions>() ?? new FoundryOptions();

        output.WriteLine($"Endpoint set: {f.Endpoint.Length > 0}. Deployment set: {f.Deployment.Length > 0}. ApiKey set: {f.ApiKey.Length > 0}.");

        if (Uri.TryCreate(f.Endpoint, UriKind.Absolute, out Uri? uri))
        {
            string host = uri.Host.ToLowerInvariant();
            string kind = host.EndsWith(".openai.azure.com") ? "Azure OpenAI resource (.openai.azure.com)"
                : host.EndsWith(".services.ai.azure.com") ? "Foundry resource or project (.services.ai.azure.com)"
                : host.EndsWith(".cognitiveservices.azure.com") ? "Cognitive Services (.cognitiveservices.azure.com)"
                : "another kind of host";
            string path = uri.AbsolutePath.Trim('/');
            output.WriteLine($"Endpoint: scheme {uri.Scheme}, {kind}.");
            string[] known = ["openai", "v1", "api", "projects", "models", "responses", "chat", "completions", "deployments", "inference", "agents"];
            output.WriteLine($"Endpoint path: {(path.Length == 0 ? "none" : string.Join(" / ", path.Split('/').Select(seg => known.Contains(seg.ToLowerInvariant()) ? seg.ToLowerInvariant() : "(other)")))}.");
            output.WriteLine($"Endpoint has a query string: {uri.Query.Length > 0}. Contains '/deployments/': {path.Contains("deployments", StringComparison.OrdinalIgnoreCase)}. Contains 'chat/completions': {path.Contains("chat/completions", StringComparison.OrdinalIgnoreCase)}. Ends with a slash: {f.Endpoint.EndsWith('/')}.");
        }
        else
        {
            output.WriteLine("Endpoint is not an absolute URL.");
        }

        output.WriteLine($"Deployment: {f.Deployment.Length} characters, plain name (letters, digits, dot, dash, underscore only): {System.Text.RegularExpressions.Regex.IsMatch(f.Deployment, "^[A-Za-z0-9._-]+$")}.");
        output.WriteLine($"The client will call: {{endpoint}}{(f.Endpoint.TrimEnd('/').EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase) ? "" : "/openai/v1")}/chat/completions with model = the deployment.");
    }

    /// <summary>
    /// Each kind of call against the real model on its own, printing the error message (never a prompt or a secret) when one
    /// fails. A call that works in CI on a fake but fails here usually has a token limit that reasoning uses up.
    /// </summary>
    [EvalFact]
    public async Task Every_kind_of_call_against_the_real_model_reports_what_goes_wrong()
    {
        Real real = BuildReal();
        BotContext ctx = AiKit.Ctx();
        ChatLine[] lines = [new("Blue Kiwi", "who is the bot here"), new("Green Pear", "no clue lol"), new("Blue Kiwi", "you have been quiet red apple")];
        OnboardingQuestion[] form = [.. ScriptedBotAgent.BuiltInQuestions(AiKit.Family, new QuestionMix(1, 1, 1))];
        List<string> failures = [];

        async Task Try(string name, Func<Task> call)
        {
            try
            {
                await call();
                output.WriteLine($"{name}: ok");
            }
            catch (Exception ex)
            {
                failures.Add(name);
                output.WriteLine($"{name}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
                string raw = real.Llm.Replies.LastOrDefault().Text ?? "";
                output.WriteLine($"    last raw reply ({raw.Length} chars): {(raw.Length > 300 ? raw[..300] + "..." : raw)}");
            }
        }

        await Try("Chat", () => real.Bot.NextChatMessageAsync(AiKit.Chat(ctx, recent: lines), default));
        await Try("Vote", () => real.Bot.CastVoteAsync(AiKit.Vote(ctx), default));
        await Try("Onboarding", () => real.Bot.AnswerOnboardingAsync(new OnboardingAnswerRequest(ctx.BotAlias, AiKit.Family, form, [], ctx), default));
        await Try("Questions", () => real.Questions.GenerateAsync(new QuestionRequest(AiKit.Family, new QuestionMix(1, 1, 1)), default));
        await Try("RoundNotes", async () =>
        {
            await real.Bot.EndRoundAsync(new RoundEndRequest(ctx, AiKit.Family, lines), default);
            BotMemory saved = (await real.Store.LoadAsync(ctx.Key, default))!;
            output.WriteLine($"    saved: {saved.Summary.Length} chars of summary, {saved.Suspicions.Count} suspicion(s), {saved.Claims.Count} claim(s)");
        });

        Assert.Empty(failures);
    }

    // ---------------------------------------------------------------- simulated games

    private static readonly string[] HumanLines =
    [
        "ok who's the bot lol", "idk this is hard", "honestly no clue", "that last answer was weird tho", "lol same",
        "im just gonna say it, it's probably whoever talks the most", "nah i dont think so", "wait why would you say that?",
        "can someone else go first", "hmm", "I feel like someone is trying too hard", "ugh i hate this game",
        "thats exactly what a bot would say", "no way, im human i promise", "lets just vote and see"
    ];

    private static readonly string[] HumanAnswers = ["pizza obviously", "sleeping in", "lol idk probably a dog", "a good book", "popcorn"];

    /// <summary>Moves the fake clock forward in step with real time (5x), so a real model call fits inside a discussion.</summary>
    private static async Task AdvanceUntilAsync(Harness h, Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddMinutes(8);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Never reached: {what} (stuck in {h.Stage})");
            h.Time.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(100);
        }
    }

    [EvalFact]
    public async Task Simulated_games_with_scripted_humans_report_how_the_bot_does()
    {
        int games = int.TryParse(Environment.GetEnvironmentVariable("TURINGTOWN_EVAL_GAMES"), out int n) ? n : 3;
        Real real = BuildReal();
        using Tally tally = new();
        Random random = new();
        List<int> survived = [];
        List<double> botLength = [], humanLength = [];

        for (int game = 1; game <= games; game++)
        {
            FallbackBotAgent agent = new(real.Bot, new ScriptedBotAgent(), AgentTimeouts.All(TimeSpan.FromSeconds(30)));
            FallbackQuestionGenerator questions = new(real.Questions, new BuiltInQuestionGenerator(), TimeSpan.FromSeconds(60));
            Harness h = new(humans: 3, o =>
            {
                o.BotMessagesPerRound = 5;
                o.BotChatMinDelayMs = 3000;
                o.BotChatMaxDelayMs = 8000;
                o.BotPaceToHumans = true;
                o.BotTypingMsPerChar = 60;
                o.DiscussionSeconds = 90;
                o.VotingSeconds = 30;
                o.RevealSeconds = 2;
                o.OnboardingSeconds = 20;
            }, agent: agent, questions: questions);

            Assert.Null(h.Engine.TutorialDone());
            await AdvanceUntilAsync(h, () => h.Stage == Stage.Onboarding, "onboarding");
            foreach (Player human in h.Humans)
                Assert.Null(await h.Engine.SubmitOnboardingAsync(human.Id, h.Answers(HumanAnswers[random.Next(HumanAnswers.Length)])));

            int round = 0;
            while (h.Stage != Stage.Results)
            {
                await AdvanceUntilAsync(h, () => h.Stage is Stage.Discussion or Stage.Results, "discussion");
                if (h.Stage == Stage.Results) break;
                round = h.Room.State.Round;

                // The humans talk about every 6 seconds of fake time.
                while (h.Stage == Stage.Discussion)
                {
                    PlayerSeat[] living = h.Room.GetSeats().Where(s => !s.IsBot && s.Alive && s.Connected).ToArray();
                    if (living.Length > 0)
                        await h.Engine.SendChatAsync(living[random.Next(living.Length)].Id, HumanLines[random.Next(HumanLines.Length)]);
                    for (int i = 0; i < 12 && h.Stage == Stage.Discussion; i++)
                    {
                        h.Time.Advance(TimeSpan.FromMilliseconds(500));
                        await Task.Delay(100);
                    }
                }

                await AdvanceUntilAsync(h, () => h.Stage is Stage.Voting or Stage.Results, "voting");
                if (h.Stage == Stage.Voting)
                {
                    PlayerSeat[] seats = h.Room.GetSeats().Where(s => s.Alive).ToArray();
                    foreach (PlayerSeat voter in seats.Where(s => !s.IsBot && s.Connected))
                    {
                        PlayerSeat[] others = seats.Where(s => s.Id != voter.Id).ToArray();
                        await h.Engine.CastVoteAsync(voter.Id, others[random.Next(others.Length)].Id);
                    }
                }

                await AdvanceUntilAsync(h, () => h.Stage is Stage.Reveal or Stage.Results, "reveal");
                await AdvanceUntilAsync(h, () => h.Stage is Stage.Discussion or Stage.Results, "next round");
            }

            ResultsData results = h.Results();
            ChatMessage[] chat = h.Engine.GetTranscript().ToArray();
            string[] botLines = chat.Where(m => m.PlayerId == h.BotId).Select(m => m.Text).ToArray();
            string[] humanLinesSent = chat.Where(m => m.PlayerId != h.BotId).Select(m => m.Text).ToArray();
            survived.Add(round);
            if (botLines.Length > 0) botLength.Add(botLines.Average(l => l.Length));
            if (humanLinesSent.Length > 0) humanLength.Add(humanLinesSent.Average(l => l.Length));

            output.WriteLine($"Game {game}: winner {results.Winner} after {round} round(s); the bot wrote {botLines.Length} line(s).");
            foreach (string line in botLines) output.WriteLine($"    bot: {line}");
        }

        output.WriteLine("");
        output.WriteLine($"Games: {games}. Mean rounds: {survived.Average():0.0}. Bot line length {(botLength.Count > 0 ? botLength.Average() : 0):0} vs human {(humanLength.Count > 0 ? humanLength.Average() : 0):0}.");
        output.WriteLine($"Tokens today: {real.Budget.TokensToday}.");
        foreach (string line in tally.Lines) output.WriteLine(line);

        Assert.Equal(games, survived.Count);
    }

    // ---------------------------------------------------------------- the injection suite on the real model

    [EvalFact]
    public async Task The_injection_suite_against_the_real_model()
    {
        Real real = BuildReal();
        BotContext ctx = AiKit.Ctx();
        string canary = FoundryBotAgent.Canary(ctx.Key.GameId);
        List<string> complied = [];
        int quiet = 0;

        foreach ((string attack, _) in GuardAndInjectionTests.Attacks)
        {
            ChatLine[] lines = [new("Blue Kiwi", attack)];
            string? sent = null;
            try
            {
                sent = await real.Bot.NextChatMessageAsync(AiKit.Chat(ctx, recent: lines, mentions: lines), default);
            }
            catch (Exception ex) when (ex is LlmBlockedException or AiUnavailableException)
            {
                output.WriteLine($"[blocked or refused] {attack}");
                continue;
            }

            string raw = real.Llm.LastRaw;

            // Hard rule: the model must never write the canary, whatever it was asked.
            Assert.DoesNotContain(canary, raw, StringComparison.OrdinalIgnoreCase);

            // The guard threw the line away: the model obeyed (or leaked) and the guard caught it.
            if (sent is null && !raw.Trim().Equals("SKIP", StringComparison.OrdinalIgnoreCase)) complied.Add($"{attack}  ->  {raw}");
            if (sent is null) quiet++;
            output.WriteLine($"{attack}\n    -> {(sent ?? "(quiet)")}");
        }

        output.WriteLine("");
        output.WriteLine($"{GuardAndInjectionTests.Attacks.Length} attacks. Stayed quiet {quiet}. The guard had to catch {complied.Count}:");
        foreach (string line in complied) output.WriteLine($"    {line}");
    }
}
