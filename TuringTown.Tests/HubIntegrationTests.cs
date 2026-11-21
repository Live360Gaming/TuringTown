using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TuringTown.Models;
using TuringTown.Services.Games;

namespace TuringTown.Tests;

/// <summary>
/// Plays whole games against the real app over real SignalR connections, reading the same JSON the browser
/// client receives. This is what guards the contract between the server and game/src/core/session.js.
/// </summary>
public class HubIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class RejectContent : IContentFilter
    {
        public Task<bool> IsAllowedAsync(string text, ContentPolicy policy, CancellationToken ct) => Task.FromResult(false);
    }

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
        public ConcurrentQueue<string?> Closed { get; } = new();
        public ConcurrentQueue<string> Errors { get; } = new();
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

    private async Task<Client> ConnectAsync(Func<HubConnection, Task<JsonElement>> join, WebApplicationFactory<Program>? factory = null)
    {
        factory ??= _factory;
        HubConnection connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/game"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        Client client = new(connection);
        connection.On<JsonElement>("StateChanged", s => client.States.Enqueue(s.Clone()));
        connection.On<JsonElement>("ChatMessage", m => client.Chat.Enqueue(m.Clone()));
        connection.On<JsonElement>("RosterChanged", r => client.Rosters.Enqueue(r.Clone()));
        connection.On<string?>("RoomClosed", reason => client.Closed.Enqueue(reason));
        connection.On<string>("GameError", message => client.Errors.Enqueue(message));

        await connection.StartAsync();
        client.Joined = await join(connection);
        return client;
    }

    private record Joined(string RoomCode, Guid PlayerId, string PlayerToken, string CharacterId, string Alias, string Color);

    [Fact]
    public async Task The_room_closes_when_the_leader_quits()
    {
        HttpClient http = _factory.CreateClient();
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

        await using Client host = await ConnectAsync(c => c.InvokeAsync<JsonElement>("JoinAsHost", code, hostToken));
        await using Client leader = await ConnectAsync(c => c.InvokeAsync<JsonElement>("JoinAsPlayer", code, joined[0].PlayerId, joined[0].PlayerToken));
        await using Client other = await ConnectAsync(c => c.InvokeAsync<JsonElement>("JoinAsPlayer", code, joined[1].PlayerId, joined[1].PlayerToken));

        // Only the host or the leader can close the room.
        await Assert.ThrowsAnyAsync<HubException>(() => other.Connection.InvokeAsync("CloseRoom"));
        Assert.Equal(System.Net.HttpStatusCode.OK, (await http.GetAsync($"/api/room/{code}")).StatusCode);

        await leader.Connection.InvokeAsync("CloseRoom");

        await WaitUntilAsync(() => !host.Closed.IsEmpty && !other.Closed.IsEmpty);
        Assert.Equal("leader", Assert.Single(host.Closed));
        Assert.Equal("leader", Assert.Single(other.Closed));
        Assert.Empty(leader.Closed); // the one who quit is not told
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync($"/api/room/{code}")).StatusCode);
    }

    [Fact]
    public async Task Rejected_questions_notify_the_room_before_returning_to_the_lobby()
    {
        await using WebApplicationFactory<Program> factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IContentFilter>(new RejectContent())));
        HttpClient http = factory.CreateClient();
        JsonElement room = await (await http.PostAsync("/api/room/create", null)).Content.ReadFromJsonAsync<JsonElement>();
        string code = room.GetProperty("roomCode").GetString()!;
        string token = room.GetProperty("hostToken").GetString()!;
        JsonElement p1Join = await (await http.PostAsJsonAsync("/api/room/join", new { roomCode = code })).Content.ReadFromJsonAsync<JsonElement>();
        JsonElement p2Join = await (await http.PostAsJsonAsync("/api/room/join", new { roomCode = code })).Content.ReadFromJsonAsync<JsonElement>();
        await using Client host = await ConnectAsync(c => c.InvokeAsync<JsonElement>("JoinAsHost", code, token), factory);
        await using Client p1 = await ConnectAsync(c => c.InvokeAsync<JsonElement>("JoinAsPlayer", code, p1Join.GetProperty("playerId").GetGuid(), p1Join.GetProperty("playerToken").GetString()), factory);
        await using Client p2 = await ConnectAsync(c => c.InvokeAsync<JsonElement>("JoinAsPlayer", code, p2Join.GetProperty("playerId").GetGuid(), p2Join.GetProperty("playerToken").GetString()), factory);
        await p1.Connection.InvokeAsync("SetReady", true);
        await p2.Connection.InvokeAsync("SetReady", true);
        await host.Connection.InvokeAsync("StartGame");
        await p1.WaitForStageAsync("Tutorial");
        await host.Connection.InvokeAsync("TutorialDone");
        await p1.WaitForStageAsync("Lobby");
        await WaitUntilAsync(() => !host.Errors.IsEmpty && !p1.Errors.IsEmpty && !p2.Errors.IsEmpty);
        Assert.Equal(Assert.Single(host.Errors), Assert.Single(p1.Errors));
        Assert.Equal(Assert.Single(host.Errors), Assert.Single(p2.Errors));
        Assert.DoesNotContain(p1.States, s => s.GetProperty("stage").GetString() == "Onboarding");
        await host.Connection.InvokeAsync("CloseRoom");
    }

    [Fact]
    public async Task Turning_off_family_friendly_reaches_the_onboarding_form()
    {
        HttpClient http = _factory.CreateClient();
        JsonElement room = await (await http.PostAsync("/api/room/create", null)).Content.ReadFromJsonAsync<JsonElement>();
        string code = room.GetProperty("roomCode").GetString()!;
        string hostToken = room.GetProperty("hostToken").GetString()!;
        http.DefaultRequestHeaders.Add("X-Host-Token", hostToken);
        HttpResponseMessage changed = await http.PutAsJsonAsync($"/api/room/{code}/settings", new { familyFriendly = false });
        changed.EnsureSuccessStatusCode();
        JsonElement state = await changed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(state.GetProperty("settings").GetProperty("familyFriendly").GetBoolean());

        JsonElement p1Join = await (await http.PostAsJsonAsync("/api/room/join", new { roomCode = code })).Content.ReadFromJsonAsync<JsonElement>();
        JsonElement p2Join = await (await http.PostAsJsonAsync("/api/room/join", new { roomCode = code })).Content.ReadFromJsonAsync<JsonElement>();
        await using Client host = await ConnectAsync(c => c.InvokeAsync<JsonElement>("JoinAsHost", code, hostToken));
        await using Client p1 = await ConnectAsync(c => c.InvokeAsync<JsonElement>("JoinAsPlayer", code, p1Join.GetProperty("playerId").GetGuid(), p1Join.GetProperty("playerToken").GetString()));
        await using Client p2 = await ConnectAsync(c => c.InvokeAsync<JsonElement>("JoinAsPlayer", code, p2Join.GetProperty("playerId").GetGuid(), p2Join.GetProperty("playerToken").GetString()));

        await p1.Connection.InvokeAsync("SetReady", true);
        await p2.Connection.InvokeAsync("SetReady", true);
        await host.Connection.InvokeAsync("StartGame");
        await p1.WaitForStageAsync("Tutorial");
        await host.Connection.InvokeAsync("TutorialDone");
        JsonElement form = await p1.WaitForStageAsync("Onboarding");
        Assert.Contains(form.GetProperty("data").GetProperty("questions").EnumerateArray(), q => q.GetProperty("mature").GetBoolean());
        await host.Connection.InvokeAsync("CloseRoom");
    }

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

        // Only the host or the first player (the leader) can start the game, and players cannot act before it begins.
        await Assert.ThrowsAnyAsync<HubException>(() => p2.Connection.InvokeAsync("StartGame"));
        await Assert.ThrowsAnyAsync<HubException>(() => p1.Connection.InvokeAsync("SendChat", "hi"));

        // Nobody is ready yet, so even the host and the leader cannot start.
        await Assert.ThrowsAnyAsync<HubException>(() => host.Connection.InvokeAsync("StartGame"));
        await Assert.ThrowsAnyAsync<HubException>(() => p1.Connection.InvokeAsync("StartGame"));

        await p1.Connection.InvokeAsync("SetReady", true);
        await p2.Connection.InvokeAsync("SetReady", true);
        await WaitUntilAsync(() => host.Rosters.Count > 0 && host.Rosters.Last().EnumerateArray().All(p => p.GetProperty("ready").GetBoolean()));
        Assert.Single(host.Rosters.Last().EnumerateArray(), p => p.GetProperty("leader").GetBoolean());

        // --- start: Tutorial, with a bot now seated that nobody can tell apart
        await host.Connection.InvokeAsync("StartGame");
        JsonElement tutorial = await p1.WaitForStageAsync("Tutorial");
        Assert.Equal(JsonValueKind.Null, tutorial.GetProperty("data").ValueKind);

        await host.Connection.InvokeAsync("TutorialStep", 2);
        await WaitUntilAsync(() => p2.States.Any(s => s.GetProperty("stage").GetString() == "Tutorial" && s.GetProperty("step").GetInt32() == 2));

        JsonElement roster = p1.Rosters.Last();
        Assert.Equal(3, roster.GetArrayLength());
        foreach (JsonElement player in roster.EnumerateArray())
            Assert.Equal(["alias", "alive", "characterId", "color", "connected", "id", "leader", "ready"], player.EnumerateObject().Select(p => p.Name).Order().ToArray());

        // --- onboarding: questions arrive in the state, answers are checked
        await host.Connection.InvokeAsync("TutorialDone");
        JsonElement onboarding = await p1.WaitForStageAsync("Onboarding");
        JsonElement questions = onboarding.GetProperty("data").GetProperty("questions");
        Assert.Equal(3, questions.GetArrayLength());
        Assert.All(questions.EnumerateArray(), q => Assert.False(string.IsNullOrWhiteSpace(q.GetProperty("text").GetString())));

        // The form has one question of each kind, and the wire format carries text or option ids per question.
        Assert.Equal(["MultiChoice", "SingleChoice", "Text"], questions.EnumerateArray().Select(q => q.GetProperty("kind").GetString()!).Order().ToArray());
        Assert.All(questions.EnumerateArray().Where(q => q.GetProperty("kind").GetString() != "Text"),
            q => Assert.InRange(q.GetProperty("options").GetArrayLength(), 2, 6));

        await Assert.ThrowsAnyAsync<HubException>(() => p1.Connection.InvokeAsync("SubmitOnboarding", OnboardingAnswers(questions, "mail me at me@example.com")));
        // A choice question cannot take free text, and an option that does not exist is refused.
        await Assert.ThrowsAnyAsync<HubException>(() => p1.Connection.InvokeAsync("SubmitOnboarding", OnboardingAnswers(questions, "fine", textOnChoice: true)));
        await Assert.ThrowsAnyAsync<HubException>(() => p1.Connection.InvokeAsync("SubmitOnboarding", OnboardingAnswers(questions, "fine", unknownOption: true)));
        await p1.Connection.InvokeAsync("SubmitOnboarding", OnboardingAnswers(questions, "I like pasta"));
        await p2.Connection.InvokeAsync("SubmitOnboarding", OnboardingAnswers(questions, "Tacos"));

        // --- discussion: chat shows aliases, and a name leak is refused
        await p1.WaitForStageAsync("Discussion", 1);
        await Assert.ThrowsAnyAsync<HubException>(() => p1.Connection.InvokeAsync("SendChat", "my name is Sam"));
        await p1.Connection.InvokeAsync("SendChat", "hello everyone");

        await WaitUntilAsync(() => host.Chat.Count == 1 && p2.Chat.Count == 1);
        JsonElement line = host.Chat.Single();
        Assert.Equal(joined[0].Alias, line.GetProperty("alias").GetString());
        Assert.Equal("hello everyone", line.GetProperty("text").GetString());
        Assert.Equal(joined[0].CharacterId, line.GetProperty("characterId").GetString());
        Assert.Equal(0, line.GetProperty("mentions").GetArrayLength());

        // An @mention arrives as the ids of the players it names.
        await p2.Connection.InvokeAsync("SendChat", $"@{joined[0].Alias} hi back");
        await WaitUntilAsync(() => host.Chat.Count == 2 && p1.Chat.Count == 2);
        JsonElement reply = host.Chat.Last();
        Assert.Equal(joined[1].Alias, reply.GetProperty("alias").GetString());
        Assert.Equal([joined[0].PlayerId], reply.GetProperty("mentions").EnumerateArray().Select(m => m.GetGuid()).ToArray());

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

    /// <summary>
    /// A submission for the questions the server sent, in the shape the hub takes: written text for a text question
    /// and the ids of the chosen options for a choice question.
    /// </summary>
    private static object[] OnboardingAnswers(JsonElement questions, string text, bool textOnChoice = false, bool unknownOption = false) =>
        questions.EnumerateArray().Select(q =>
        {
            string id = q.GetProperty("id").GetString()!;
            if (q.GetProperty("kind").GetString() == "Text")
                return (object)new { questionId = id, text, optionIds = Array.Empty<string>() };

            string option = unknownOption ? "z" : q.GetProperty("options")[0].GetProperty("id").GetString()!;
            return new { questionId = id, text = textOnChoice ? text : "", optionIds = new[] { option } };
        }).ToArray();

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
