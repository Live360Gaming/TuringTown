using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TuringTown.Models;
using TuringTown.Services.Ai;
using TuringTown.Services.Games;
using TuringTown.Services.Storage;

namespace TuringTown.Tests;

/// <summary>
/// The opt-in transcript archive, the content-safety filter, and how the app wires itself from configuration.
/// </summary>
public class StorageSafetyAndWiringTests
{
    // ---------------------------------------------------------------- the transcript archive

    [Fact]
    public async Task A_finished_game_is_archived_with_aliases_and_game_text_only()
    {
        Harness h = new(humans: 2, o => o.BotMessagesPerRound = 0);
        Assert.Null(h.Engine.TutorialDone());
        await h.ToAsync(Stage.Onboarding);
        foreach (Player human in h.Humans) Assert.Null(await h.Engine.SubmitOnboardingAsync(human.Id, h.Answers("my private pasta answer")));
        await h.ToAsync(Stage.Discussion, 1);
        Assert.Null(await h.Engine.SendChatAsync(h.Humans[0].Id, "hello archive"));
        await h.ToAsync(Stage.Voting, 1);
        foreach (Player human in h.Humans) await h.VoteAsync(human, h.BotId);
        await h.ToAsync(Stage.Results);
        await h.UntilAsync(() => h.Archive.Archives.Count == 1, "the archive write");

        GameArchive archive = h.Archive.Archives.Single();
        PlayerSeat bot = h.Room.GetSeats().First(s => s.IsBot);

        Assert.Equal("Humans", archive.Winner);
        Assert.Equal(h.Engine.GameId, archive.GameId);
        Assert.Equal(3, archive.Players.Count);
        Assert.Equal([(bot.Alias, "Bot")], archive.Players.Where(p => p.Role == "Bot").Select(p => (p.Alias, p.Role)));
        Assert.Contains(archive.Chat, l => l.Text == "hello archive" && l.Alias == h.Humans[0].Alias);
        Assert.Equal(3, archive.Questions.Count);
        Assert.Equal(bot.Alias, Assert.Single(archive.Rounds).EliminatedAlias);

        string json = BlobGameArchive.Serialize(archive);
        // Not the onboarding answers, and no player id, token or room code.
        Assert.DoesNotContain("my private pasta answer", json);
        foreach (Player human in h.Humans)
        {
            Assert.DoesNotContain(human.Id.ToString(), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(human.Token, json);
        }
        Assert.DoesNotContain(h.Room.Code.ToString(), json);
        Assert.Contains(h.Humans[0].Alias, json);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}/[0-9a-f]{32}\.json$", archive.BlobName);
    }

    [Fact]
    public async Task A_failing_archive_never_holds_up_the_results()
    {
        Harness h = new(humans: 2, archive: new BrokenArchive());

        await h.ThroughOnboardingAsync();
        await h.ToAsync(Stage.Voting, 1);
        foreach (Player human in h.Humans) await h.VoteAsync(human, h.BotId);

        await h.ToAsync(Stage.Results);
        await h.ToAsync(Stage.Lobby);
    }

    private sealed class BrokenArchive : IGameArchive
    {
        public Task ArchiveAsync(GameArchive archive, CancellationToken ct) => throw new IOException("storage down");
    }

    [Fact]
    public void The_blob_archive_needs_a_service_uri_when_it_is_turned_on()
    {
        StorageOptionsValidator validator = new();

        Assert.True(validator.Validate(null, new StorageOptions()).Succeeded);
        Assert.True(validator.Validate(null, new StorageOptions { Provider = StorageProvider.AzureBlob }).Succeeded); // archiving is off
        StorageOptions on = new() { Provider = StorageProvider.AzureBlob, ArchiveTranscripts = true };
        ValidateOptionsResult missing = validator.Validate(null, on);
        Assert.True(missing.Failed);
        Assert.Contains("Storage:Blob:ServiceUri", missing.Failures!.Single());

        on.Blob.ServiceUri = "https://account.blob.core.windows.net";
        Assert.True(validator.Validate(null, on).Succeeded);
    }

    // ---------------------------------------------------------------- content safety

    private sealed class FakeSafety(Func<string, CancellationToken, Task<IReadOnlyDictionary<string, int>>> analyse) : IContentSafetyClient
    {
        public Task<IReadOnlyDictionary<string, int>> AnalyzeAsync(string text, CancellationToken ct) => analyse(text, ct);
    }

    private static IContentFilter Filter(Func<string, CancellationToken, Task<IReadOnlyDictionary<string, int>>> analyse, Action<ContentSafetyOptions>? configure = null)
    {
        ContentSafetyOptions options = new();
        configure?.Invoke(options);
        return new SeverityContentFilter(new FakeSafety(analyse), Microsoft.Extensions.Options.Options.Create(options), NullLogger<SeverityContentFilter>.Instance);
    }

    private static Func<string, CancellationToken, Task<IReadOnlyDictionary<string, int>>> Severity(string category, int severity) =>
        (_, _) => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>
        {
            ["Hate"] = 0, ["SelfHarm"] = 0, ["Sexual"] = 0, ["Violence"] = 0, [category] = severity
        });

    [Fact]
    public async Task Clean_text_is_allowed_in_both_modes()
    {
        IContentFilter filter = Filter(Severity("Hate", 0));
        Assert.True(await filter.IsAllowedAsync("hello", AiKit.Family, default));
        Assert.True(await filter.IsAllowedAsync("hello", AiKit.Mature, default));
    }

    [Theory]
    [InlineData("Hate", 2)]
    [InlineData("SelfHarm", 2)]
    [InlineData("Sexual", 2)]
    [InlineData("Violence", 2)]
    [InlineData("Violence", 6)]
    public async Task A_family_room_blocks_anything_above_safe_in_every_category(string category, int severity)
    {
        Assert.False(await Filter(Severity(category, severity)).IsAllowedAsync("text", AiKit.Family, default));
    }

    [Fact]
    public async Task A_mature_room_relaxes_only_violence_and_the_mandatory_protections_stay()
    {
        // Violence: low is fine, medium is not.
        Assert.True(await Filter(Severity("Violence", 2)).IsAllowedAsync("text", AiKit.Mature, default));
        Assert.False(await Filter(Severity("Violence", 4)).IsAllowedAsync("text", AiKit.Mature, default));

        // Hate, self-harm and sexual content are held to the family threshold in both modes.
        foreach (string category in new[] { "Hate", "SelfHarm", "Sexual" })
            Assert.False(await Filter(Severity(category, 2)).IsAllowedAsync("text", AiKit.Mature, default), category);
    }

    [Fact]
    public async Task The_thresholds_come_from_configuration()
    {
        IContentFilter strict = Filter(Severity("Hate", 2), o => o.FamilyThreshold = 4);

        Assert.True(await strict.IsAllowedAsync("text", AiKit.Family, default));
    }

    [Fact]
    public async Task When_the_service_is_down_the_filter_fails_open_or_closed_as_configured()
    {
        Func<string, CancellationToken, Task<IReadOnlyDictionary<string, int>>> down = (_, _) => throw new HttpRequestException("no network");

        Assert.True(await Filter(down).IsAllowedAsync("text", AiKit.Family, default));
        Assert.False(await Filter(down, o => o.FailOpen = false).IsAllowedAsync("text", AiKit.Family, default));
    }

    [Fact]
    public async Task A_slow_service_times_out_like_a_failed_one()
    {
        Func<string, CancellationToken, Task<IReadOnlyDictionary<string, int>>> hangs = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new Dictionary<string, int>();
        };

        Assert.False(await Filter(hangs, o =>
        {
            o.TimeoutMs = 50;
            o.FailOpen = false;
        }).IsAllowedAsync("text", AiKit.Family, default).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_caller_that_gives_up_is_not_swallowed_by_the_filter()
    {
        using CancellationTokenSource cts = new();
        IContentFilter filter = Filter(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new Dictionary<string, int>();
        });

        Task<bool> call = filter.IsAllowedAsync("text", AiKit.Family, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [Fact]
    public void The_azure_client_is_built_with_a_key_or_with_managed_identity_without_any_network()
    {
        Assert.NotNull(new AzureContentSafetyClient(Microsoft.Extensions.Options.Options.Create(new ContentSafetyOptions { Endpoint = "https://x.cognitiveservices.azure.com", ApiKey = "k" })));
        Assert.NotNull(new AzureContentSafetyClient(Microsoft.Extensions.Options.Options.Create(new ContentSafetyOptions { Endpoint = "https://x.cognitiveservices.azure.com" })));
    }

    // ---------------------------------------------------------------- wiring from configuration

    /// <summary>
    /// The app with some settings overridden. In-memory settings are added last, so they win over user secrets on a
    /// developer's machine (which hold the real Foundry keys), and the tests behave the same everywhere.
    /// </summary>
    private static WebApplicationFactory<Program> App(Dictionary<string, string?> settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings)));

    [Fact]
    public void By_default_every_game_runs_on_the_scripted_bots_with_no_keys()
    {
        using WebApplicationFactory<Program> app = App(new() { ["Foundry:Endpoint"] = "", ["Foundry:Deployment"] = "", ["Foundry:ApiKey"] = "" });

        AgentSet set = app.Services.GetRequiredService<AgentSelector>().Select();

        Assert.False(set.UsesAi);
        Assert.IsType<ScriptedBotAgent>(set.Agent);
        Assert.IsType<BuiltInQuestionGenerator>(set.Questions);
        Assert.IsType<NullGameArchive>(app.Services.GetRequiredService<IGameArchive>());
        Assert.IsType<AllowAllContentFilter>(app.Services.GetRequiredService<IContentFilter>());
        Assert.IsType<InMemoryBotMemoryStore>(app.Services.GetRequiredService<IBotMemoryStore>());
    }

    [Fact]
    public void The_foundry_provider_gives_the_real_agents_and_a_lease_and_falls_back_to_scripted_when_full()
    {
        using WebApplicationFactory<Program> app = App(new()
        {
            ["Ai:Provider"] = "Foundry",
            ["Ai:Limits:MaxAiGames"] = "1",
            ["Foundry:Endpoint"] = "https://res.openai.azure.com",
            ["Foundry:Deployment"] = "mini",
            ["Foundry:ApiKey"] = "not-a-real-key"
        });
        AgentSelector selector = app.Services.GetRequiredService<AgentSelector>();

        AgentSet first = selector.Select();
        Assert.True(first.UsesAi);
        Assert.IsType<FallbackBotAgent>(first.Agent);
        Assert.IsType<FallbackQuestionGenerator>(first.Questions);
        Assert.NotNull(first.Lease);

        Assert.False(selector.Select().UsesAi);
        first.Lease!.Dispose();
        Assert.True(selector.Select().UsesAi);
    }

    [Fact]
    public void The_foundry_provider_without_an_endpoint_refuses_to_start_and_says_which_key_is_missing()
    {
        using WebApplicationFactory<Program> app = App(new() { ["Ai:Provider"] = "Foundry", ["Foundry:Endpoint"] = "", ["Foundry:Deployment"] = "" });

        Exception ex = Assert.ThrowsAny<Exception>(() => app.CreateClient());

        string text = ex.ToString();
        Assert.Contains("Foundry:Endpoint", text);
        Assert.Contains("Foundry:Deployment", text);
    }

    [Fact]
    public void The_archive_and_content_safety_are_chosen_from_configuration()
    {
        using WebApplicationFactory<Program> app = App(new()
        {
            ["Storage:ArchiveTranscripts"] = "true",
            ["Storage:Provider"] = "InMemory",
            ["ContentSafety:Provider"] = "Azure",
            ["ContentSafety:Endpoint"] = "https://x.cognitiveservices.azure.com",
            ["ContentSafety:ApiKey"] = "not-a-real-key"
        });

        Assert.IsType<InMemoryGameArchive>(app.Services.GetRequiredService<IGameArchive>());
        Assert.IsType<SeverityContentFilter>(app.Services.GetRequiredService<IContentFilter>());
    }

    [Fact]
    public async Task Creating_rooms_is_limited_per_client_address()
    {
        using WebApplicationFactory<Program> app = App(new() { ["RateLimits:RoomRequestsPerMinute"] = "3" });
        using HttpClient client = app.CreateClient();

        List<HttpStatusCode> codes = [];
        for (int i = 0; i < 5; i++) codes.Add((await client.PostAsync("/api/room/create", null)).StatusCode);

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], codes);
        // Joining shares the limit, and the health check is never limited.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/api/room/join", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/test")).StatusCode);
    }

    [Fact]
    public async Task The_limit_is_per_client_so_one_address_cannot_use_up_anothers()
    {
        using WebApplicationFactory<Program> app = App(new() { ["RateLimits:RoomRequestsPerMinute"] = "1" });
        using HttpClient client = app.CreateClient();

        HttpRequestMessage Create(string ip)
        {
            HttpRequestMessage request = new(HttpMethod.Post, "/api/room/create");
            request.Headers.Add("X-Forwarded-For", ip);
            return request;
        }

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Create("203.0.113.1"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Create("203.0.113.1"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Create("203.0.113.2"))).StatusCode);
    }

    [Fact]
    public void Every_template_in_the_prompts_folder_is_embedded()
    {
        string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Services", "Ai", "Prompts"));
        if (!Directory.Exists(folder)) return; // the source tree is not next to the test output (for example on a build agent)

        string[] files = Directory.GetFiles(folder, "*.md").Select(f => Path.GetFileNameWithoutExtension(f)).Order().ToArray();
        Assert.Equal(files, new BotPrompts().All.Select(t => t.Name).Order());
        Assert.All(files, f => Assert.Matches(new Regex(@"^[a-z-]+$"), f));
    }
}
