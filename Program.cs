using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using TuringTown.Models;
using TuringTown.Services;
using TuringTown.Services.Ai;
using TuringTown.Services.Games;
using TuringTown.Services.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddHealthChecks();
builder.Services.AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddOptions<GameOptions>()
    .Bind(builder.Configuration.GetSection(GameOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<RoomManager>();

// Turing Town: the game engine, its timings, and the AI behind the bots.
builder.Services.AddSingleton(TimeProvider.System);

// Which seasonal art the clients show (Christmas in December, or forced by Season:Mode).
builder.Services.Configure<SeasonOptions>(builder.Configuration.GetSection(SeasonOptions.SectionName));
builder.Services.AddSingleton<SeasonProvider>();
builder.Services.Configure<TuringTownOptions>(builder.Configuration.GetSection(TuringTownOptions.SectionName));
builder.Services.Configure<GameNightOptions>(builder.Configuration.GetSection(GameNightOptions.SectionName));
builder.Services.Configure<WittyBitsOptions>(builder.Configuration.GetSection(WittyBitsOptions.SectionName));

// The AI. The tunables are in the "Ai" section of appsettings.json. Where the model lives (Endpoint, Deployment, ApiKey) is the
// "Foundry" section: user secrets in Development, the same keys as environment variables (Foundry__Endpoint) in production.
// Ai:Provider is Scripted by default, so everything runs offline with no keys.
builder.Services.AddOptions<AiOptions>().Bind(builder.Configuration.GetSection(AiOptions.SectionName)).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<AiOptions>, AiOptionsValidator>();
builder.Services.AddOptions<FoundryOptions>().Bind(builder.Configuration.GetSection(FoundryOptions.SectionName));
builder.Services.AddOptions<StorageOptions>().Bind(builder.Configuration.GetSection(StorageOptions.SectionName))
    .Validate(o => !o.ArchiveTranscripts || o.Provider != StorageProvider.LocalFile || builder.Environment.IsDevelopment(),
        "Storage:Provider=LocalFile is only available in Development.")
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>();
builder.Services.AddOptions<ContentSafetyOptions>().Bind(builder.Configuration.GetSection(ContentSafetyOptions.SectionName))
    .Validate(o => o.Provider != ContentSafetyProvider.Azure || Uri.TryCreate(o.Endpoint, UriKind.Absolute, out _),
        "ContentSafety:Endpoint is missing or is not an absolute URL.")
    .ValidateOnStart();

builder.Services.AddSingleton<ScriptedBotAgent>();
builder.Services.AddSingleton<IBotAgent>(sp => sp.GetRequiredService<ScriptedBotAgent>());
builder.Services.AddSingleton<BuiltInQuestionGenerator>();
builder.Services.AddSingleton<IQuestionGenerator>(sp => sp.GetRequiredService<BuiltInQuestionGenerator>());
builder.Services.AddSingleton<ScriptedWittyBotAgent>();
builder.Services.AddSingleton<BuiltInWittyPrompts>();
builder.Services.AddSingleton<IBotMemoryStore, InMemoryBotMemoryStore>();
builder.Services.AddSingleton<BotPrompts>();
builder.Services.AddSingleton<AiBudget>();

// Each game gets the real agents while the budget allows and the scripted ones otherwise (a failed call also falls back).
builder.Services.AddSingleton(sp =>
{
    AiOptions ai = sp.GetRequiredService<IOptions<AiOptions>>().Value;
    AgentSet scripted = new(sp.GetRequiredService<ScriptedBotAgent>(), sp.GetRequiredService<BuiltInQuestionGenerator>(),
        sp.GetRequiredService<ScriptedWittyBotAgent>(), sp.GetRequiredService<BuiltInWittyPrompts>(), UsesAi: false);
    AiBudget budget = sp.GetRequiredService<AiBudget>();
    if (ai.Provider != AiProvider.Foundry) return new AgentSelector(scripted, null, budget);

    ILoggerFactory logs = sp.GetRequiredService<ILoggerFactory>();
    ILlmClient llm = new GuardedLlmClient(
        new FoundryLlmClient(sp.GetRequiredService<IOptions<FoundryOptions>>(), sp.GetRequiredService<IOptions<AiOptions>>()),
        budget, logs.CreateLogger<GuardedLlmClient>());

    // The fallback gives up a moment after the client's own timeout, so the client reports the timeout first.
    TimeSpan Wait(AiCallOptions call) => call.Timeout + TimeSpan.FromSeconds(1);
    IBotAgent bot = new FallbackBotAgent(
        new FoundryBotAgent(llm, sp.GetRequiredService<IBotMemoryStore>(), sp.GetRequiredService<BotPrompts>(),
            sp.GetRequiredService<IOptions<AiOptions>>(), logs.CreateLogger<FoundryBotAgent>()),
        scripted.Agent,
        new AgentTimeouts(Wait(ai.Calls.Chat), Wait(ai.Calls.Vote), Wait(ai.Calls.Onboarding), Wait(ai.Calls.RoundNotes)));
    IQuestionGenerator questions = new FallbackQuestionGenerator(
        new FoundryQuestionGenerator(llm, sp.GetRequiredService<BotPrompts>(), sp.GetRequiredService<IOptions<AiOptions>>()),
        scripted.Questions, Wait(ai.Calls.Questions), logs.CreateLogger<FallbackQuestionGenerator>());

    IWittyBotAgent witty = new FallbackWittyBotAgent(
        new FoundryWittyBotAgent(llm, sp.GetRequiredService<BotPrompts>(), sp.GetRequiredService<IOptions<AiOptions>>()),
        scripted.WittyAgent, Wait(ai.Calls.WittyAnswer), Wait(ai.Calls.WittyVote), logs.CreateLogger<FallbackWittyBotAgent>());
    IWittyPromptGenerator wittyPrompts = new FallbackWittyPromptGenerator(
        new FoundryWittyPromptGenerator(llm, sp.GetRequiredService<BotPrompts>(), sp.GetRequiredService<IOptions<AiOptions>>()),
        Wait(ai.Calls.WittyPrompts), logs.CreateLogger<FallbackWittyPromptGenerator>());
    // The night's own timeout (GameNight:PlanningMaxSeconds) and its random-order fallback cover the planner.
    IGameNightPlanner planner = new FoundryGameNightPlanner(llm, sp.GetRequiredService<BotPrompts>(), sp.GetRequiredService<IOptions<AiOptions>>());

    IShowHost host = new FallbackShowHost(
        new FoundryShowHost(llm, sp.GetRequiredService<BotPrompts>(), sp.GetRequiredService<IOptions<AiOptions>>()),
        Wait(ai.Calls.Show), Wait(ai.Calls.ShowTopic), logs.CreateLogger<FallbackShowHost>());

    return new AgentSelector(scripted, new AgentSet(bot, questions, witty, wittyPrompts, UsesAi: true, Planner: planner, Host: host), budget);
});

// The opt-in transcript archive (off by default).
builder.Services.AddSingleton<IGameArchive>(sp =>
{
    StorageOptions storage = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
    if (!storage.ArchiveTranscripts) return new NullGameArchive();
    return storage.Provider switch
    {
        StorageProvider.AzureBlob => BlobGameArchive.Create(storage.Blob),
        StorageProvider.LocalFile => new FileGameArchive(
            Path.Combine(sp.GetRequiredService<IHostEnvironment>().ContentRootPath, "transcripts"),
            sp.GetRequiredService<ILogger<FileGameArchive>>()),
        _ => new InMemoryGameArchive()
    };
});

// Content safety for every chat line, answer and generated question (Azure AI Content Safety when configured).
builder.Services.AddSingleton<IContentFilter>(sp =>
{
    IOptions<ContentSafetyOptions> options = sp.GetRequiredService<IOptions<ContentSafetyOptions>>();
    return options.Value.Provider == ContentSafetyProvider.Azure
        ? new SeverityContentFilter(new AzureContentSafetyClient(options), options, sp.GetRequiredService<ILogger<SeverityContentFilter>>())
        : new AllowAllContentFilter();
});

// The AI team's coach, which picks the order of the night's games (a random order offline).
builder.Services.AddSingleton<IGameNightPlanner, ScriptedGameNightPlanner>();
// Which games a night may play: the "enabled" flag in each minigame's data/game.json.
builder.Services.AddSingleton(GameDefinitions.FromEmbeddedData());
builder.Services.AddSingleton<IRoomBroadcaster, SignalRRoomBroadcaster>();
builder.Services.AddSingleton<GameCoordinator>();

// Creating and joining rooms is limited per client address, so a script cannot spin up rooms (and AI games) without end.
// Behind Azure's front end the client address is in X-Forwarded-For; only the last entry (added by that front end) is used.
builder.Services.AddOptions<RateLimitOptions>().Bind(builder.Configuration.GetSection(RateLimitOptions.SectionName));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(Program.RoomRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = http.RequestServices.GetRequiredService<IOptions<RateLimitOptions>>().Value.RoomRequestsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

// Say which AI is running and how it signs in (never a value), so a wrong environment or a missing key is obvious in the log.
{
    AiOptions ai = app.Services.GetRequiredService<IOptions<AiOptions>>().Value;
    bool hasKey = !string.IsNullOrWhiteSpace(app.Services.GetRequiredService<IOptions<FoundryOptions>>().Value.ApiKey);
    app.Logger.LogInformation("AI: {Provider}{Auth}. Environment: {Environment}.",
        ai.Provider,
        ai.Provider != AiProvider.Foundry ? " (offline scripted bots and built-in questions)"
            : hasKey ? ", signing in with the API key"
            : ", signing in with Entra ID (DefaultAzureCredential): no Foundry:ApiKey was found, so your identity or the app's managed identity needs the Cognitive Services OpenAI User role",
        app.Environment.EnvironmentName);
}

app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// This middleware ensures that ONLY GET method requests can be cached by the browser.
// This absolutely 100% prevents any other http methods from being stored and recycled from a browser cache.
// I'm not sure if this is necessary, but I think it's a good catch-all to prevent us scratching our heads
// in frustration down the line.
app.Use(async (context, next) =>
{
    if (context.Request.Method != "GET")
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";
            return Task.CompletedTask;
        });
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseRateLimiter();

//app.UseHttpsRedirection();

app.MapControllers();
app.MapHub<TuringTown.Hubs.GameHub>("/hubs/game");

// Liveness check used by the CI/deploy smoke tests. Available in all environments.
app.MapHealthChecks("/api/test");

app.Run();

// Lets the integration tests start the whole app (WebApplicationFactory<Program>).
public partial class Program
{
    /// <summary>The rate limit policy for creating and joining rooms.</summary>
    public const string RoomRateLimit = "rooms";
}
