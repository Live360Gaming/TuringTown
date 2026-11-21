using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TuringTown.Services.Ai;

namespace TuringTown.Tests;

/// <summary>
/// The limits around the model (budget, caps, circuit breaker), the settings and their validation, and the request the real
/// client sends. None of it touches a network or a real key.
/// </summary>
public class AiInfrastructureTests
{
    // ---------------------------------------------------------------- budget and limits

    private static (AiBudget Budget, FakeTimeProvider Time) Budget(Action<AiLimits>? configure = null)
    {
        FakeTimeProvider time = new();
        return (new AiBudget(AiKit.Options(o => configure?.Invoke(o.Limits)), time), time);
    }

    [Fact]
    public async Task A_game_may_only_make_so_many_calls()
    {
        (AiBudget budget, _) = Budget(l => l.MaxCallsPerGame = 2);

        (await budget.BeginCallAsync("game-1", default)).Dispose();
        (await budget.BeginCallAsync("game-1", default)).Dispose();
        AiUnavailableException ex = await Assert.ThrowsAsync<AiUnavailableException>(() => budget.BeginCallAsync("game-1", default));
        Assert.Contains("call limit", ex.Message);

        // Another game has its own allowance.
        (await budget.BeginCallAsync("game-2", default)).Dispose();
    }

    [Fact]
    public async Task The_daily_token_budget_stops_calls_and_new_ai_games_and_resets_the_next_day()
    {
        (AiBudget budget, FakeTimeProvider time) = Budget(l => l.DailyTokenBudget = 1000);
        budget.RecordUsage(999);
        Assert.True(budget.TryReserveGame(out IDisposable? lease));
        lease!.Dispose();

        budget.RecordUsage(1);
        await Assert.ThrowsAsync<AiUnavailableException>(() => budget.BeginCallAsync(null, default));
        Assert.False(budget.TryReserveGame(out _));

        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, budget.TokensToday);
        (await budget.BeginCallAsync(null, default)).Dispose();
        Assert.True(budget.TryReserveGame(out _));
    }

    [Fact]
    public void Only_so_many_games_run_on_the_ai_at_once_and_a_lease_frees_its_place_exactly_once()
    {
        (AiBudget budget, _) = Budget(l => l.MaxAiGames = 2);

        Assert.True(budget.TryReserveGame(out IDisposable? first));
        Assert.True(budget.TryReserveGame(out IDisposable? second));
        Assert.False(budget.TryReserveGame(out IDisposable? none));
        Assert.Null(none);

        first!.Dispose();
        first.Dispose(); // disposing twice must not free a second place
        Assert.True(budget.TryReserveGame(out IDisposable? third));
        Assert.False(budget.TryReserveGame(out _));
        second!.Dispose();
        third!.Dispose();
    }

    [Fact]
    public async Task Repeated_failures_open_the_circuit_for_a_while_and_a_success_resets_the_count()
    {
        (AiBudget budget, FakeTimeProvider time) = Budget(l =>
        {
            l.CircuitFailures = 3;
            l.CircuitOpenSeconds = 60;
        });

        budget.RecordFailure();
        budget.RecordFailure();
        budget.RecordSuccess();
        budget.RecordFailure();
        budget.RecordFailure();
        Assert.False(budget.CircuitOpen);

        budget.RecordFailure();
        Assert.True(budget.CircuitOpen);
        await Assert.ThrowsAsync<AiUnavailableException>(() => budget.BeginCallAsync(null, default));
        Assert.False(budget.TryReserveGame(out _));

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.False(budget.CircuitOpen);
        (await budget.BeginCallAsync(null, default)).Dispose();
    }

    [Fact]
    public async Task Only_so_many_calls_run_at_once()
    {
        (AiBudget budget, _) = Budget(l => l.MaxConcurrentCalls = 1);

        IDisposable first = await budget.BeginCallAsync(null, default);
        Task<IDisposable> second = budget.BeginCallAsync(null, default);

        await Task.Delay(100);
        Assert.False(second.IsCompleted);

        first.Dispose();
        (await second.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    // ---------------------------------------------------------------- the guarded client

    private sealed class StubLlm(Func<LlmCall, CancellationToken, Task<LlmResult>> reply) : ILlmClient
    {
        public int Calls;

        public Task<LlmResult> CompleteAsync(LlmCall call, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return reply(call, ct);
        }
    }

    private static LlmCall Call(TimeSpan? timeout = null, string? scope = "g") =>
        new("Chat", "system", "user", null, "minimal", 100, timeout ?? TimeSpan.FromSeconds(5), scope);

    private static GuardedLlmClient Guarded(StubLlm inner, AiBudget budget) => new(inner, budget, NullLogger<GuardedLlmClient>.Instance);

    [Fact]
    public async Task A_successful_call_is_counted_against_the_daily_tokens()
    {
        (AiBudget budget, _) = Budget();
        StubLlm inner = new((_, _) => Task.FromResult(new LlmResult("hi", 700, 0, 300, 0)));

        LlmResult result = await Guarded(inner, budget).CompleteAsync(Call(), default);

        Assert.Equal("hi", result.Text);
        Assert.Equal(1000, budget.TokensToday);
    }

    [Fact]
    public async Task A_call_that_takes_too_long_times_out_and_counts_as_a_failure()
    {
        (AiBudget budget, _) = Budget(l => l.CircuitFailures = 2);
        StubLlm inner = new(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new LlmResult("", 0, 0, 0, 0);
        });
        GuardedLlmClient client = Guarded(inner, budget);

        await Assert.ThrowsAsync<TimeoutException>(() => client.CompleteAsync(Call(TimeSpan.FromMilliseconds(50)), default));
        await Assert.ThrowsAsync<TimeoutException>(() => client.CompleteAsync(Call(TimeSpan.FromMilliseconds(50)), default));

        Assert.True(budget.CircuitOpen);
    }

    [Fact]
    public async Task A_caller_that_gives_up_is_not_a_failure_of_the_model()
    {
        (AiBudget budget, _) = Budget(l => l.CircuitFailures = 1);
        StubLlm inner = new(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new LlmResult("", 0, 0, 0, 0);
        });
        using CancellationTokenSource cts = new();
        Task<LlmResult> call = Guarded(inner, budget).CompleteAsync(Call(), cts.Token);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

        Assert.False(budget.CircuitOpen);
    }

    [Fact]
    public async Task The_models_own_content_filter_is_not_counted_as_an_outage_but_errors_are()
    {
        (AiBudget budget, _) = Budget(l => l.CircuitFailures = 2);

        StubLlm blocked = new((_, _) => throw new LlmBlockedException("filtered"));
        for (int i = 0; i < 5; i++) await Assert.ThrowsAsync<LlmBlockedException>(() => Guarded(blocked, budget).CompleteAsync(Call(), default));
        Assert.False(budget.CircuitOpen);

        StubLlm broken = new((_, _) => throw new InvalidOperationException("500"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Guarded(broken, budget).CompleteAsync(Call(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Guarded(broken, budget).CompleteAsync(Call(), default));
        Assert.True(budget.CircuitOpen);
    }

    [Fact]
    public async Task A_call_over_a_limit_never_reaches_the_model()
    {
        (AiBudget budget, _) = Budget(l => l.MaxCallsPerGame = 1);
        StubLlm inner = new((_, _) => Task.FromResult(new LlmResult("hi", 1, 0, 1, 0)));
        GuardedLlmClient client = Guarded(inner, budget);

        await client.CompleteAsync(Call(), default);
        await Assert.ThrowsAsync<AiUnavailableException>(() => client.CompleteAsync(Call(), default));

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task When_a_limit_is_hit_the_bot_falls_back_and_chat_goes_quiet()
    {
        (AiBudget budget, _) = Budget(l => l.MaxCallsPerGame = 1);
        GuardedLlmClient guarded = new(new FakeLlmClient(), budget, NullLogger<GuardedLlmClient>.Instance);

        // A model-backed agent on the guarded client: the first call works, then the game's call limit is reached.
        FoundryBotAgent real = new(guarded, new TuringTown.Services.Storage.InMemoryBotMemoryStore(), new BotPrompts(), AiKit.Options(), NullLogger<FoundryBotAgent>.Instance);
        FallbackBotAgent wrapped = new(real, new ScriptedBotAgent(), TimeSpan.FromSeconds(5));
        BotContext ctx = AiKit.Ctx();

        Assert.NotNull(await wrapped.NextChatMessageAsync(AiKit.Chat(ctx), default));
        Assert.Null(await wrapped.NextChatMessageAsync(AiKit.Chat(ctx), default));
        // Votes still get an answer from the scripted agent.
        Guid? vote = await wrapped.CastVoteAsync(AiKit.Vote(ctx), default);
        Assert.NotNull(vote);
    }

    // ---------------------------------------------------------------- choosing the agents for a game

    private static AgentSet Set(bool ai) => new(new ScriptedBotAgent(), new BuiltInQuestionGenerator(), ai);

    [Fact]
    public void A_game_gets_the_real_ai_while_the_limits_allow_and_the_scripted_bots_otherwise()
    {
        (AiBudget budget, _) = Budget(l => l.MaxAiGames = 1);
        AgentSet scripted = Set(false), real = Set(true);
        AgentSelector selector = new(scripted, real, budget);

        AgentSet first = selector.Select();
        Assert.True(first.UsesAi);
        Assert.NotNull(first.Lease);

        // The second game starts on the scripted bots, and stays on them.
        Assert.False(selector.Select().UsesAi);

        first.Lease!.Dispose();
        Assert.True(selector.Select().UsesAi);
    }

    [Fact]
    public void Without_a_real_provider_every_game_is_scripted()
    {
        (AiBudget budget, _) = Budget();
        Assert.False(new AgentSelector(Set(false), null, budget).Select().UsesAi);
    }

    // ---------------------------------------------------------------- settings

    private static AiOptionsValidator Validator(FoundryOptions? foundry = null) => new(Microsoft.Extensions.Options.Options.Create(foundry ?? new FoundryOptions()));

    [Fact]
    public void The_scripted_provider_needs_no_keys()
    {
        Assert.True(Validator().Validate(null, new AiOptions()).Succeeded);
    }

    [Fact]
    public void The_foundry_provider_needs_an_https_endpoint_and_a_deployment_and_the_messages_never_show_values()
    {
        AiOptions foundry = new() { Provider = AiProvider.Foundry };

        ValidateOptionsResult missing = Validator().Validate(null, foundry);
        Assert.True(missing.Failed);
        Assert.Contains(missing.Failures!, f => f.Contains("Foundry:Endpoint"));
        Assert.Contains(missing.Failures!, f => f.Contains("Foundry:Deployment"));

        // A value that is wrong is never echoed back.
        ValidateOptionsResult http = Validator(new FoundryOptions { Endpoint = "http://leaky-host-xyz.example", Deployment = "leaky-deployment-xyz", ApiKey = "leaky-key-xyz" }).Validate(null, foundry);
        Assert.True(http.Failed);
        Assert.All(http.Failures!, f =>
        {
            Assert.DoesNotContain("leaky-host-xyz", f);
            Assert.DoesNotContain("leaky-deployment-xyz", f);
            Assert.DoesNotContain("leaky-key-xyz", f);
        });

        Assert.True(Validator(new FoundryOptions { Endpoint = "https://res.openai.azure.com", Deployment = "mini" }).Validate(null, foundry).Succeeded);
    }

    [Fact]
    public void Nonsense_numbers_are_refused_at_startup()
    {
        AiOptions o = new();
        o.Calls.Chat.MaxOutputTokens = 0;
        o.Calls.Vote.TimeoutMs = -1;
        o.Limits.MaxAiGames = 0;

        ValidateOptionsResult result = Validator().Validate(null, o);

        Assert.Contains(result.Failures!, f => f.Contains("Ai:Calls:Chat:MaxOutputTokens"));
        Assert.Contains(result.Failures!, f => f.Contains("Ai:Calls:Vote:TimeoutMs"));
        Assert.Contains(result.Failures!, f => f.Contains("Ai:Limits:MaxAiGames"));
    }

    [Fact]
    public void The_two_sections_bind_separately_so_secrets_stay_out_of_the_tunables()
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Provider"] = "Foundry",
            ["Ai:Calls:Chat:ReasoningEffort"] = "low",
            ["Ai:Calls:Chat:MaxOutputTokens"] = "123",
            ["Ai:Limits:MaxAiGames"] = "3",
            ["Foundry:Endpoint"] = "https://res.openai.azure.com",
            ["Foundry:Deployment"] = "some-deployment",
            ["Foundry:ApiKey"] = "k"
        }).Build();

        AiOptions ai = config.GetSection("Ai").Get<AiOptions>()!;
        FoundryOptions foundry = config.GetSection("Foundry").Get<FoundryOptions>()!;

        Assert.Equal(AiProvider.Foundry, ai.Provider);
        Assert.Equal(("low", 123), (ai.Calls.Chat.ReasoningEffort, ai.Calls.Chat.MaxOutputTokens));
        Assert.Equal(3, ai.Limits.MaxAiGames);
        // The defaults of what was not set are kept.
        Assert.Equal(8000, ai.Calls.Vote.TimeoutMs);
        Assert.Equal(("https://res.openai.azure.com", "some-deployment", "k"), (foundry.Endpoint, foundry.Deployment, foundry.ApiKey));
    }

    [Fact]
    public void The_settings_in_appsettings_json_match_the_defaults_in_code_and_hold_no_secret()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Assert.True(File.Exists(path), $"{path} is not copied to the test output.");
        IConfiguration config = new ConfigurationBuilder().AddJsonFile(path).Build();

        AiOptions fromFile = config.GetSection("Ai").Get<AiOptions>()!;
        AiOptions defaults = new();

        Assert.Equal(AiProvider.Scripted, fromFile.Provider);
        Assert.Equal(JsonSerializer.Serialize(defaults), JsonSerializer.Serialize(fromFile));
        // The secrets live elsewhere.
        Assert.False(config.GetSection("Foundry").Exists());
        Assert.DoesNotContain("ApiKey", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- the real client's request

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Seen) Seen.Add((request, body));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Completion(string content, string finish = "stop") => Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
    {
        id = "chatcmpl-1",
        @object = "chat.completion",
        created = 1,
        model = "mini-deploy",
        choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = finish } },
        usage = new
        {
            prompt_tokens = 120,
            completion_tokens = 40,
            total_tokens = 160,
            prompt_tokens_details = new { cached_tokens = 100 },
            completion_tokens_details = new { reasoning_tokens = 25 }
        }
    }));

    private static FoundryLlmClient Client(CaptureHandler handler, string apiKey = "test-key", string endpoint = "https://res.openai.azure.com") =>
        new(FoundryLlmClient.Build(
            new FoundryOptions { Endpoint = endpoint, Deployment = "mini-deploy", ApiKey = apiKey },
            new AiOptions(),
            new HttpClientPipelineTransport(new HttpClient(handler))));

    [Fact]
    public async Task The_request_goes_to_the_v1_chat_completions_endpoint_with_the_reasoning_settings_and_no_temperature()
    {
        CaptureHandler handler = new(_ => Completion("hello there"));
        LlmCall call = new("Chat", "be a player", "say something", null, "minimal", 77, TimeSpan.FromSeconds(5));

        LlmResult result = await Client(handler).CompleteAsync(call, default);

        (HttpRequestMessage request, string body) = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("res.openai.azure.com", request.RequestUri!.Host);
        Assert.Equal("/openai/v1/chat/completions", request.RequestUri.AbsolutePath);

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;
        Assert.Equal("mini-deploy", root.GetProperty("model").GetString());
        Assert.Equal(77, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal("minimal", root.GetProperty("reasoning_effort").GetString());
        Assert.Equal(["system", "user"], root.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()!));
        Assert.Equal("be a player", root.GetProperty("messages")[0].GetProperty("content").GetString());
        // Reasoning models reject these, and a plain chat reply has no structured format.
        foreach (string name in new[] { "temperature", "top_p", "frequency_penalty", "presence_penalty", "max_tokens", "response_format" })
            Assert.False(root.TryGetProperty(name, out _), name);

        Assert.Equal(("hello there", 120, 100, 40, 25), (result.Text, result.InputTokens, result.CachedTokens, result.OutputTokens, result.ReasoningTokens));
    }

    [Fact]
    public async Task A_structured_call_sends_a_strict_json_schema()
    {
        CaptureHandler handler = new(_ => Completion("{\"target\":\"ABSTAIN\"}"));
        const string schema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"}},\"required\":[\"target\"],\"additionalProperties\":false}";

        await Client(handler).CompleteAsync(new LlmCall("Vote", "s", "u", new LlmJsonSchema("vote", schema), "low", 200, TimeSpan.FromSeconds(5)), default);

        using JsonDocument doc = JsonDocument.Parse(handler.Seen.Single().Body);
        JsonElement format = doc.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("vote", format.GetProperty("json_schema").GetProperty("name").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        Assert.False(format.GetProperty("json_schema").GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task An_empty_reasoning_effort_sends_nothing_so_a_model_without_it_still_works()
    {
        CaptureHandler handler = new(_ => Completion("ok"));

        await Client(handler).CompleteAsync(new LlmCall("Chat", "s", "u", null, "", 50, TimeSpan.FromSeconds(5)), default);

        using JsonDocument doc = JsonDocument.Parse(handler.Seen.Single().Body);
        Assert.False(doc.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task The_api_key_is_sent_as_a_credential_and_never_in_the_url_or_the_body()
    {
        CaptureHandler handler = new(_ => Completion("ok"));

        await Client(handler, apiKey: "very-secret-key").CompleteAsync(new LlmCall("Chat", "s", "u", null, "low", 50, TimeSpan.FromSeconds(5)), default);

        (HttpRequestMessage request, string body) = handler.Seen.Single();
        Assert.DoesNotContain("very-secret-key", request.RequestUri!.ToString());
        Assert.DoesNotContain("very-secret-key", body);
        Assert.Contains("very-secret-key", request.Headers.Authorization?.Parameter ?? string.Join("", request.Headers.TryGetValues("api-key", out IEnumerable<string>? v) ? v : []));
    }

    [Theory]
    [InlineData("https://res.openai.azure.com", "https://res.openai.azure.com/openai/v1/")]
    [InlineData("https://res.openai.azure.com/", "https://res.openai.azure.com/openai/v1/")]
    [InlineData("https://res.openai.azure.com/openai/v1", "https://res.openai.azure.com/openai/v1/")]
    [InlineData("https://res.openai.azure.com/openai/v1/", "https://res.openai.azure.com/openai/v1/")]
    [InlineData("  https://x.services.ai.azure.com/api/projects/p  ", "https://x.services.ai.azure.com/api/projects/p/openai/v1/")]
    // A full request URL pasted from the portal or the docs is cut back to the base.
    [InlineData("https://x.services.ai.azure.com/openai/v1/responses", "https://x.services.ai.azure.com/openai/v1/")]
    [InlineData("https://x.services.ai.azure.com/openai/v1/chat/completions?api-version=2025-01-01", "https://x.services.ai.azure.com/openai/v1/")]
    [InlineData("https://r.openai.azure.com/openai/deployments/mini/chat/completions?api-version=2024-10-21", "https://r.openai.azure.com/openai/v1/")]
    [InlineData("https://x.services.ai.azure.com/api/projects/p/openai/v1/responses", "https://x.services.ai.azure.com/api/projects/p/openai/v1/")]
    [InlineData("https://localhost:8443/openai/v1/responses", "https://localhost:8443/openai/v1/")]
    public void A_resource_a_project_a_v1_url_or_a_pasted_request_url_all_work(string given, string expected) =>
        Assert.Equal(expected, FoundryLlmClient.V1Endpoint(given).ToString());

    [Fact]
    public async Task A_project_endpoint_is_used_as_given()
    {
        CaptureHandler handler = new(_ => Completion("ok"));

        await Client(handler, endpoint: "https://x.services.ai.azure.com/api/projects/p")
            .CompleteAsync(new LlmCall("Chat", "s", "u", null, "low", 50, TimeSpan.FromSeconds(5)), default);

        Assert.Equal("/api/projects/p/openai/v1/chat/completions", handler.Seen.Single().Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task A_request_the_models_filter_refuses_is_reported_as_blocked()
    {
        CaptureHandler handler = new(_ => Json(HttpStatusCode.BadRequest,
            "{\"error\":{\"code\":\"content_filter\",\"message\":\"The prompt was filtered by the content management policy.\"}}"));

        await Assert.ThrowsAsync<LlmBlockedException>(() =>
            Client(handler).CompleteAsync(new LlmCall("Chat", "s", "u", null, "low", 50, TimeSpan.FromSeconds(5)), default));
    }

    [Fact]
    public async Task A_reply_the_models_filter_stops_is_reported_as_blocked()
    {
        CaptureHandler handler = new(_ => Completion("partial", finish: "content_filter"));

        await Assert.ThrowsAsync<LlmBlockedException>(() =>
            Client(handler).CompleteAsync(new LlmCall("Chat", "s", "u", null, "low", 50, TimeSpan.FromSeconds(5)), default));
    }

    [Fact]
    public async Task An_empty_reply_because_reasoning_used_all_the_tokens_says_so()
    {
        CaptureHandler handler = new(_ => Completion("", finish: "length"));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client(handler).CompleteAsync(new LlmCall("Chat", "s", "u", null, "high", 50, TimeSpan.FromSeconds(5)), default));

        Assert.Contains("MaxOutputTokens", ex.Message);
    }

    [Fact]
    public async Task Json_that_was_cut_off_is_an_error_not_a_half_answer()
    {
        CaptureHandler handler = new(_ => Completion("{\"target\":\"Blu", finish: "length"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client(handler).CompleteAsync(new LlmCall("Vote", "s", "u", new LlmJsonSchema("vote", "{\"type\":\"object\"}"), "low", 50, TimeSpan.FromSeconds(5)), default));
    }

    [Fact]
    public async Task A_rate_limited_call_fails_at_once_without_retrying()
    {
        CaptureHandler handler = new(_ => Json(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":\"429\",\"message\":\"slow down\"}}"));

        ClientResultException ex = await Assert.ThrowsAsync<ClientResultException>(() =>
            Client(handler).CompleteAsync(new LlmCall("Chat", "s", "u", null, "low", 50, TimeSpan.FromSeconds(5)), default));

        Assert.Equal(429, ex.Status);
        Assert.Single(handler.Seen);
    }

    [Fact]
    public void Without_an_api_key_the_client_is_built_for_managed_identity_and_needs_no_network_to_exist()
    {
        // The credential is only used when a call is made, so building it must not need Azure.
        Assert.NotNull(FoundryLlmClient.Build(new FoundryOptions { Endpoint = "https://res.openai.azure.com", Deployment = "mini" }, new AiOptions(), null));
    }
}
