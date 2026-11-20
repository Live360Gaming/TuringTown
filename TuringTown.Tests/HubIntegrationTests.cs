using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;

namespace TuringTown.Tests;

/// <summary>
/// Plays whole games against the real app over real SignalR connections, reading the same JSON the browser
/// client receives. This is what guards the contract between the server and game/src/core/session.js.
/// </summary>
public class HubIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HubIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TuringTown:TutorialMaxSeconds"] = "30",
                ["TuringTown:OnboardingSeconds"] = "30",
                ["TuringTown:DiscussionSeconds"] = "1",
                ["TuringTown:VotingSeconds"] = "30",
                ["TuringTown:RevealSeconds"] = "1",
                ["TuringTown:ResultsSeconds"] = "30",
                ["TuringTown:MaxRounds"] = "2",
                ["TuringTown:BotMessagesPerRound"] = "0",
                ["TuringTown:BotVoteMinDelayMs"] = "0",
                ["TuringTown:BotVoteMaxDelayMs"] = "0",
                ["TuringTown:ChatMinIntervalMs"] = "0"
            })));
    }

    /// <summary>
    /// One client of the hub: the host or a player. Records everything the server pushes.
    /// </summary>
    private sealed class Client(HubConnection connection) : IAsyncDisposable
    {
        public HubConnection Connection { get; } = connection;
        public ConcurrentQueue<JsonElement> States { get; } = new();
        public ConcurrentQueue<JsonElement> Chat { get; } = new();
        public ConcurrentQueue<JsonElement> Rosters { get; } = new();
        public JsonElement Joined { get; set; }

        public async Task<JsonElement> WaitForStageAsync(string stage, int? round = null)
        {
            for (int i = 0; i < 600; i++)
            {
                foreach (JsonElement state in States)
                    if (state.GetProperty("stage").GetString() == stage && (round is null || state.GetProperty("round").GetInt32() == round))
                        return state;
                await Task.Delay(25);
            }
            throw new TimeoutException($"Never saw stage {stage}. Saw: {string.Join(", ", States.Select(s => s.GetProperty("stage").GetString() + s.GetProperty("round").GetInt32()))}");
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    private async Task<Client> ConnectAsync(Func<HubConnection, Task<JsonElement>> join)
    {
        HubConnection connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/game"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        Client client = new(connection);
        connection.On<JsonElement>("StateChanged", s => client.States.Enqueue(s.Clone()));
        connection.On<JsonElement>("ChatMessage", m => client.Chat.Enqueue(m.Clone()));
        connection.On<JsonElement>("RosterChanged", r => client.Rosters.Enqueue(r.Clone()));

        await connection.StartAsync();
        client.Joined = await join(connection);
        return client;
    }

    private record Joined(string RoomCode, Guid PlayerId, string PlayerToken, string CharacterId, string Alias, string Color);

    [Fact]
    public async Task A_whole_game_runs_over_real_connections()
    {
        HttpClient http = _factory.CreateClient();

        // --- the host creates a room, two players join over HTTP
        JsonElement room = await (await http.PostAsync("/api/room/create", null)).Content.ReadFromJsonAsync<JsonElement>();
        string code = room.GetProperty("roomCode").GetString()!;
        string hostToken = room.GetProperty("hostToken").GetString()!;

        Joined[] joined = new Joined[2];
        for (int i = 0; i < 2; i++)
        {
            JsonElement r = await (await http.PostAsJsonAsync("/api/room/join", new { roomCode = code })).Content.ReadFromJsonAsync<JsonElement>();
            joined[i] = new Joined(r.GetProperty("roomCode").GetString()!, r.GetProperty("playerId").GetGuid(), r.GetProperty("playerToken").GetString()!,
                r.GetProperty("characterId").GetString()!, r.GetProperty("alias").GetString()!, r.GetProperty("color").GetString()!);
        }

        // Random "Color Fruit" alias, a character key, and a hex colour, as the client expects.
        Assert.All(joined, j =>
        {
            Assert.Matches(@"^[A-Z][a-z]+ [A-Z][a-z]+$", j.Alias);
            Assert.Matches(@"^character_0[1-6]$", j.CharacterId);
            Assert.Matches("^#[0-9a-f]{6}$", j.Color);
        });
        Assert.NotEqual(joined[0].Alias, joined[1].Alias);

        // --- everyone connects to the hub
        await using Client host = await ConnectAsync(c => c.InvokeAsync<JsonElement>("JoinAsHost", code, hostToken));
        await using Client p1 = await ConnectAsync(c => c.InvokeAsync<JsonElement>("JoinAsPlayer", code, joined[0].PlayerId, joined[0].PlayerToken));
        await using Client p2 = await ConnectAsync(c => c.InvokeAsync<JsonElement>("JoinAsPlayer", code, joined[1].PlayerId, joined[1].PlayerToken));

        // The join result has what a screen needs to render straight away.
        Assert.Equal("Lobby", p1.Joined.GetProperty("state").GetProperty("stage").GetString());
        Assert.Equal("turing-town", p1.Joined.GetProperty("state").GetProperty("gameId").GetString());
        Assert.Equal(joined[0].Alias, p1.Joined.GetProperty("you").GetProperty("alias").GetString());
        Assert.True(p1.Joined.GetProperty("serverTimeUtcMs").GetInt64() > 0);
        Assert.Equal(JsonValueKind.Null, host.Joined.GetProperty("you").ValueKind);

        // Wrong credentials are refused.
        await Assert.ThrowsAnyAsync<HubException>(() => ConnectAsync(c => c.InvokeAsync<JsonElement>("JoinAsPlayer", code, joined[0].PlayerId, "wrong")));
        await Assert.ThrowsAnyAsync<HubException>(() => ConnectAsync(c => c.InvokeAsync<JsonElement>("JoinAsHost", code, "wrong")));

        // Only the host can start the game, and players cannot act before it begins.
        await Assert.ThrowsAnyAsync<HubException>(() => p1.Connection.InvokeAsync("StartGame"));
        await Assert.ThrowsAnyAsync<HubException>(() => p1.Connection.InvokeAsync("SendChat", "hi"));

        // --- start: Tutorial, with a bot now seated that nobody can tell apart
        await host.Connection.InvokeAsync("StartGame");
        JsonElement tutorial = await p1.WaitForStageAsync("Tutorial");
        Assert.Equal(JsonValueKind.Null, tutorial.GetProperty("data").ValueKind);

        await host.Connection.InvokeAsync("TutorialStep", 2);
        await WaitUntilAsync(() => p2.States.Any(s => s.GetProperty("stage").GetString() == "Tutorial" && s.GetProperty("step").GetInt32() == 2));

        JsonElement roster = p1.Rosters.Last();
        Assert.Equal(3, roster.GetArrayLength());
        foreach (JsonElement player in roster.EnumerateArray())
            Assert.Equal(["alias", "alive", "characterId", "color", "connected", "id"], player.EnumerateObject().Select(p => p.Name).Order().ToArray());

        // --- onboarding: questions arrive in the state, answers are checked
        await host.Connection.InvokeAsync("TutorialDone");
        JsonElement onboarding = await p1.WaitForStageAsync("Onboarding");
        JsonElement questions = onboarding.GetProperty("data").GetProperty("questions");
        Assert.Equal(3, questions.GetArrayLength());
        Assert.All(questions.EnumerateArray(), q => Assert.False(string.IsNullOrWhiteSpace(q.GetProperty("text").GetString())));

        string[] leaky = ["a", "b", "mail me at me@example.com"];
        await Assert.ThrowsAnyAsync<HubException>(() => p1.Connection.InvokeAsync("SubmitOnboarding", leaky));
        await p1.Connection.InvokeAsync("SubmitOnboarding", new[] { "I like pasta", "Sundays", "A cat" });
        await p2.Connection.InvokeAsync("SubmitOnboarding", new[] { "Tacos", "Mondays", "A dog" });

        // --- discussion: chat shows aliases, and a name leak is refused
        await p1.WaitForStageAsync("Discussion", 1);
        await Assert.ThrowsAnyAsync<HubException>(() => p1.Connection.InvokeAsync("SendChat", "my name is Sam"));
        await p1.Connection.InvokeAsync("SendChat", "hello everyone");

        await WaitUntilAsync(() => host.Chat.Count == 1 && p2.Chat.Count == 1);
        JsonElement line = host.Chat.Single();
        Assert.Equal(joined[0].Alias, line.GetProperty("alias").GetString());
        Assert.Equal("hello everyone", line.GetProperty("text").GetString());
        Assert.Equal(joined[0].CharacterId, line.GetProperty("characterId").GetString());

        // --- voting rounds until the game ends (two rounds at most)
        JsonElement results = default;
        for (int round = 1; round <= 2; round++)
        {
            JsonElement voting = await p1.WaitForStageAsync("Voting", round);
            Assert.NotEqual(JsonValueKind.Undefined, voting.ValueKind);

            await VoteForSomeoneElseAsync(p1, joined[0].PlayerId);
            await VoteForSomeoneElseAsync(p2, joined[1].PlayerId);

            JsonElement reveal = await p1.WaitForStageAsync("Reveal", round);
            Assert.Equal(JsonValueKind.Array, reveal.GetProperty("data").GetProperty("tally").ValueKind);

            // The bot's vote is random, so the game either ends here (a side won) or carries on to the next round.
            await WaitUntilAsync(() => p1.States.Any(s =>
                s.GetProperty("stage").GetString() == "Results"
                || (s.GetProperty("stage").GetString() == "Discussion" && s.GetProperty("round").GetInt32() == round + 1)));
            if (p1.States.Any(s => s.GetProperty("stage").GetString() == "Results"))
            {
                results = await p1.WaitForStageAsync("Results");
                break;
            }
        }

        if (results.ValueKind == JsonValueKind.Undefined)
            results = await p1.WaitForStageAsync("Results");

        // --- results are the first place roles appear: exactly one bot among three seats
        Assert.Contains(results.GetProperty("data").GetProperty("winner").GetString(), new[] { "Humans", "Bots" });
        JsonElement roles = results.GetProperty("data").GetProperty("roles");
        Assert.Equal(3, roles.GetArrayLength());
        Assert.Equal(1, roles.EnumerateArray().Count(r => r.GetProperty("wasBot").GetBoolean()));

        // --- the host sends everyone back to the lobby, and the bot leaves
        await host.Connection.InvokeAsync("ReturnToLobby");
        await WaitUntilAsync(() => host.States.Last().GetProperty("stage").GetString() == "Lobby");
        await WaitUntilAsync(() => host.Rosters.Last().GetArrayLength() == 2);
    }

    private static async Task VoteForSomeoneElseAsync(Client client, Guid myId)
    {
        // Vote the way the real client does: only from what the roster says. Any refusal is a failure, because it
        // means the server and the client disagree about who is still in.
        JsonElement roster = client.Rosters.Last();
        JsonElement me = roster.EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == myId);
        if (!me.GetProperty("alive").GetBoolean()) return; // voted out: watching only

        Guid target = roster.EnumerateArray()
            .Where(p => p.GetProperty("alive").GetBoolean() && p.GetProperty("id").GetGuid() != myId)
            .Select(p => p.GetProperty("id").GetGuid())
            .First();

        await client.Connection.InvokeAsync("CastVote", target);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 600; i++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("Condition was never met.");
    }
}
