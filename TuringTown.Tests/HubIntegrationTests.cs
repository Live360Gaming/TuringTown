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
using TuringTown.Services.Ai;
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

    /// <summary>Plays Turing Town first, then Witty Bits, then Shout Out, so a test knows which game comes when.</summary>
    private sealed class TuringTownFirst : IGameNightPlanner
    {
        public Task<GameNightPlan> PlanAsync(GameNightPlanRequest request, CancellationToken ct) =>
            Task.FromResult(new GameNightPlan([GameIds.TuringTown, GameIds.WittyBits, GameIds.ShoutOut], "Bring it on."));
    }

    private readonly WebApplicationFactory<Program> _factory;

    public HubIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(s => s.AddSingleton<IGameNightPlanner>(new TuringTownFirst()).AddSingleton(GameDefinitions.AllEnabled())).ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TuringTown:TutorialMaxSeconds"] = "30",
                ["TuringTown:OnboardingSeconds"] = "30",
                ["TuringTown:DiscussionSeconds"] = "1",
                ["TuringTown:VotingSeconds"] = "30",
                ["TuringTown:RevealSeconds"] = "1",
                ["TuringTown:ResultsSeconds"] = "30",
                ["TuringTown:RapidFireSeconds"] = "30",
                ["TuringTown:HotTopicSeconds"] = "30",
                ["TuringTown:HotSeatSeconds"] = "1",
                ["TuringTown:TransitionMaxSeconds"] = "1",
                ["TuringTown:BotRapidMinDelayMs"] = "0",
                ["TuringTown:BotRapidMaxDelayMs"] = "0",
                ["TuringTown:BotTopicMinDelayMs"] = "0",
                ["TuringTown:BotTopicMaxDelayMs"] = "0",
                ["TuringTown:BotMessagesPerRound"] = "0",
                ["TuringTown:BotVoteMinDelayMs"] = "0",
                ["TuringTown:BotVoteMaxDelayMs"] = "0",
                ["TuringTown:ChatMinIntervalMs"] = "0",
                ["GameNight:PlanRevealSeconds"] = "1",
                ["GameNight:ScoreboardSeconds"] = "30",
                ["GameNight:FinaleSeconds"] = "30",
                ["WittyBits:TutorialMaxSeconds"] = "30",
                ["WittyBits:WritingSeconds"] = "30",
                ["WittyBits:LastBitWritingSeconds"] = "30",
                ["WittyBits:MatchupSeconds"] = "30",
                ["WittyBits:MatchupResultSeconds"] = "1",
                ["WittyBits:ResultsSeconds"] = "30",
                ["WittyBits:BotAnswerMinDelayMs"] = "0",
                ["WittyBits:BotAnswerMaxDelayMs"] = "0",
                ["WittyBits:BotVoteMinDelayMs"] = "0",
                ["WittyBits:BotVoteMaxDelayMs"] = "0",
                ["ShoutOut:TutorialMaxSeconds"] = "30",
                ["ShoutOut:CategoriesSeconds"] = "30",
                ["ShoutOut:Questions"] = "2",
                ["ShoutOut:DoubleQuestions"] = "1",
                ["ShoutOut:ReadSeconds"] = "0",
                ["ShoutOut:BuzzSeconds"] = "2",
                ["ShoutOut:RebuzzSeconds"] = "1",
                ["ShoutOut:AnswerSeconds"] = "2",
                ["ShoutOut:RevealSeconds"] = "1",
                ["ShoutOut:ResultsSeconds"] = "30",
                ["ShoutOut:BotCategoryMinDelayMs"] = "0",
                ["ShoutOut:BotCategoryMaxDelayMs"] = "0"
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
        public ConcurrentQueue<JsonElement> Rooms { get; } = new();
        public ConcurrentQueue<string?> Closed { get; } = new();
        public ConcurrentQueue<string> Errors { get; } = new();
        public JsonElement Joined { get; set; }

        public async Task<JsonElement> WaitForStageAsync(string stage, int? round = null, string? gameId = null)
        {
            for (int i = 0; i < 600; i++)
            {
                foreach (JsonElement state in States)
                    if (state.GetProperty("stage").GetString() == stage && (round is null || state.GetProperty("round").GetInt32() == round)
                        && (gameId is null || state.GetProperty("gameId").GetString() == gameId))
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
        connection.On<JsonElement>("RoomChanged", r => client.Rooms.Enqueue(r.Clone()));
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
    public async Task Leaving_frees_the_seat_at_once_and_the_next_player_leads()
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

        // The join result carries the lobby's settings, so no screen has to poll for them.
        Assert.Equal(2, other.Joined.GetProperty("room").GetProperty("playerCount").GetInt32());
        Assert.True(other.Joined.GetProperty("room").GetProperty("minPlayers").GetInt32() >= 1);

        await leader.Connection.InvokeAsync("LeaveRoom");

        // The room stays open, the seat is gone straight away, and the other player now leads.
        await WaitUntilAsync(() => other.Rosters.Any(r => r.GetArrayLength() == 1));
        JsonElement me = Assert.Single(other.Rosters.Last(r => r.GetArrayLength() == 1).EnumerateArray());
        Assert.Equal(joined[1].PlayerId, me.GetProperty("id").GetGuid());
        Assert.True(me.GetProperty("leader").GetBoolean());
        await WaitUntilAsync(() => host.Rooms.Any(r => r.GetProperty("playerCount").GetInt32() == 1));
        Assert.Empty(host.Closed);
        Assert.Empty(other.Closed);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await http.GetAsync($"/api/room/{code}")).StatusCode);

        // The old seat's credentials no longer work.
        await Assert.ThrowsAnyAsync<HubException>(() => ConnectAsync(c => c.InvokeAsync<JsonElement>("JoinAsPlayer", code, joined[0].PlayerId, joined[0].PlayerToken)));
    }

    [Fact]
    public async Task A_settings_change_is_pushed_to_every_screen()
    {
        HttpClient http = _factory.CreateClient();
        JsonElement room = await (await http.PostAsync("/api/room/create", null)).Content.ReadFromJsonAsync<JsonElement>();
        string code = room.GetProperty("roomCode").GetString()!;
        string hostToken = room.GetProperty("hostToken").GetString()!;
        JsonElement j = await (await http.PostAsJsonAsync("/api/room/join", new { roomCode = code })).Content.ReadFromJsonAsync<JsonElement>();

        await using Client host = await ConnectAsync(c => c.InvokeAsync<JsonElement>("JoinAsHost", code, hostToken));
        await using Client player = await ConnectAsync(c => c.InvokeAsync<JsonElement>("JoinAsPlayer", code, j.GetProperty("playerId").GetGuid(), j.GetProperty("playerToken").GetString()));

        using HttpRequestMessage put = new(HttpMethod.Put, $"/api/room/{code}/settings") { Content = JsonContent.Create(new { familyFriendly = false }) };
        put.Headers.Add("X-Host-Token", hostToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await http.SendAsync(put)).StatusCode);

        await WaitUntilAsync(() => player.Rooms.Any(r => !r.GetProperty("settings").GetProperty("familyFriendly").GetBoolean()));
        await WaitUntilAsync(() => host.Rooms.Any(r => !r.GetProperty("settings").GetProperty("familyFriendly").GetBoolean()));
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
            Assert.Contains(j.CharacterId, Characters.All);
            Assert.Matches("^#[0-9a-f]{6}$", j.Color);
        });
        Assert.NotEqual(joined[0].Alias, joined[1].Alias);

        // --- everyone connects to the hub
        await using Client host = await ConnectAsync(c => c.InvokeAsync<JsonElement>("JoinAsHost", code, hostToken));
        await using Client p1 = await ConnectAsync(c => c.InvokeAsync<JsonElement>("JoinAsPlayer", code, joined[0].PlayerId, joined[0].PlayerToken));
        await using Client p2 = await ConnectAsync(c => c.InvokeAsync<JsonElement>("JoinAsPlayer", code, joined[1].PlayerId, joined[1].PlayerToken));

        // The join result has what a screen needs to render straight away.
        Assert.Equal("Lobby", p1.Joined.GetProperty("state").GetProperty("stage").GetString());
        Assert.Equal("game-night", p1.Joined.GetProperty("state").GetProperty("gameId").GetString());
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

        // --- start: the AI plans the night, then the first game's Tutorial, with bots now seated that nobody can tell apart
        await host.Connection.InvokeAsync("StartGame");
        await WaitUntilAsync(() => p1.States.Any(s => s.GetProperty("stage").GetString() == "Planning"
                                                      && s.GetProperty("data").GetProperty("order").GetArrayLength() > 0));
        JsonElement planning = p1.States.Last(s => s.GetProperty("stage").GetString() == "Planning");
        Assert.Equal("game-night", planning.GetProperty("gameId").GetString());
        Assert.Contains("turing-town", planning.GetProperty("data").GetProperty("order").EnumerateArray().Select(g => g.GetProperty("gameId").GetString()));

        JsonElement tutorial = await p1.WaitForStageAsync("Tutorial");
        Assert.Equal("turing-town", tutorial.GetProperty("gameId").GetString());
        Assert.Equal(JsonValueKind.Null, tutorial.GetProperty("data").ValueKind);

        await host.Connection.InvokeAsync("TutorialStep", 2);
        await WaitUntilAsync(() => p2.States.Any(s => s.GetProperty("stage").GetString() == "Tutorial" && s.GetProperty("step").GetInt32() == 2));

        JsonElement roster = p1.Rosters.Last();
        // Two humans get two bots, so the night always has at least four players.
        Assert.Equal(4, roster.GetArrayLength());
        foreach (JsonElement player in roster.EnumerateArray())
            Assert.Equal(["alias", "alive", "characterId", "color", "connected", "id", "leader", "ready"], player.EnumerateObject().Select(p => p.Name).Order().ToArray());

        // --- onboarding: the leader skips the tutorial from their phone (nobody else can), questions arrive in the state
        await Assert.ThrowsAnyAsync<HubException>(() => p2.Connection.InvokeAsync("TutorialDone"));
        await Assert.ThrowsAnyAsync<HubException>(() => p1.Connection.InvokeAsync("TutorialStep", 1));
        await p1.Connection.InvokeAsync("TutorialDone");
        JsonElement onboarding = await p1.WaitForStageAsync("Onboarding");
        JsonElement questions = onboarding.GetProperty("data").GetProperty("questions");
        Assert.Equal(4, questions.GetArrayLength()); // 1 text, 1 single, 1 multi and 1 era question
        Assert.All(questions.EnumerateArray(), q => Assert.False(string.IsNullOrWhiteSpace(q.GetProperty("text").GetString())));
        // Which decade an option points to stays on the server.
        Assert.DoesNotContain("era", questions.GetRawText(), StringComparison.OrdinalIgnoreCase);

        // The form has one question of each kind (the era question is a single choice on the wire), and the wire format
        // carries text or option ids per question.
        Assert.Equal(["MultiChoice", "SingleChoice", "SingleChoice", "Text"], questions.EnumerateArray().Select(q => q.GetProperty("kind").GetString()!).Order().ToArray());
        Assert.All(questions.EnumerateArray().Where(q => q.GetProperty("kind").GetString() != "Text"),
            q => Assert.InRange(q.GetProperty("options").GetArrayLength(), 2, 6));

        await Assert.ThrowsAnyAsync<HubException>(() => p1.Connection.InvokeAsync("SubmitOnboarding", OnboardingAnswers(questions, "mail me at me@example.com")));
        // A choice question cannot take free text, and an option that does not exist is refused.
        await Assert.ThrowsAnyAsync<HubException>(() => p1.Connection.InvokeAsync("SubmitOnboarding", OnboardingAnswers(questions, "fine", textOnChoice: true)));
        await Assert.ThrowsAnyAsync<HubException>(() => p1.Connection.InvokeAsync("SubmitOnboarding", OnboardingAnswers(questions, "fine", unknownOption: true)));
        await p1.Connection.InvokeAsync("SubmitOnboarding", OnboardingAnswers(questions, "I like pasta"));
        await p2.Connection.InvokeAsync("SubmitOnboarding", OnboardingAnswers(questions, "Tacos"));

        // --- five questions: everyone answers, and the answers are shown at the first vote
        JsonElement rapid = await p1.WaitForStageAsync("RapidFire", 1);
        Assert.Equal(5, rapid.GetProperty("data").GetProperty("questions").GetArrayLength());
        for (int i = 1; i <= 5; i++)
        {
            await p1.Connection.InvokeAsync("SubmitRapidAnswer", $"q{i}", $"purple waffles {i}");
            await p2.Connection.InvokeAsync("SubmitRapidAnswer", $"q{i}", $"grumpy toast {i}");
        }

        JsonElement tap = await p1.WaitForStageAsync("Voting", 1);
        Assert.Equal(4, tap.GetProperty("data").GetProperty("answers").GetArrayLength());
        await VoteForSomeoneElseAsync(p1, joined[0].PlayerId);
        await VoteForSomeoneElseAsync(p2, joined[1].PlayerId);

        // --- the hot topic and the hot seat
        await p1.WaitForStageAsync("HotTopic", 2);
        await p1.Connection.InvokeAsync("SubmitHotTopic", "cold pizza");
        await p2.Connection.InvokeAsync("SubmitHotTopic", "warm soda");
        await WaitUntilAsync(() => p1.States.Any(s => s.GetProperty("stage").GetString() == "Discussion" && s.GetProperty("round").GetInt32() == 2));

        // --- the big discussion: chat shows aliases, and a name leak is refused
        await p1.WaitForStageAsync("Discussion", 3);
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

        // --- the one deciding vote
        await p1.WaitForStageAsync("Voting", 3);
        await VoteForSomeoneElseAsync(p1, joined[0].PlayerId);
        await VoteForSomeoneElseAsync(p2, joined[1].PlayerId);
        JsonElement reveal = await p1.WaitForStageAsync("Reveal", 3);
        Assert.Equal(JsonValueKind.Array, reveal.GetProperty("data").GetProperty("tally").ValueKind);
        JsonElement results = await p1.WaitForStageAsync("Results");

        // --- results are the first place roles appear: two bots among four seats
        string? winner = results.GetProperty("data").GetProperty("winner").GetString();
        Assert.Contains(winner, new[] { "Humans", "Bots" });
        JsonElement roles = results.GetProperty("data").GetProperty("roles");
        Assert.Equal(4, roles.GetArrayLength());
        Assert.Equal(2, roles.EnumerateArray().Count(r => r.GetProperty("wasBot").GetBoolean()));

        // --- only the host or the leader moves the night on: to the scoreboard. Every bot was unmasked at the
        // results, so the team totals are public, and the winning side has the bonus.
        await Assert.ThrowsAnyAsync<HubException>(() => p2.Connection.InvokeAsync("ContinueNight"));
        await host.Connection.InvokeAsync("ContinueNight");
        JsonElement board = await p1.WaitForStageAsync("Scoreboard");
        JsonElement boardTeams = board.GetProperty("data").GetProperty("teams");
        // Round points (100, 200, 400 ...) come on top of the bonus, so the winner has at least the bonus.
        int humanTotal = boardTeams.GetProperty("humans").GetInt32(), aiTotal = boardTeams.GetProperty("ai").GetInt32();
        Assert.True((winner == "Humans" ? humanTotal : aiTotal) >= 1000);
        Assert.True((winner == "Humans" ? aiTotal : humanTotal) < 1000);
        Assert.Equal("witty-bits", board.GetProperty("data").GetProperty("next").GetProperty("gameId").GetString());

        // --- Witty Bits on the same seats: everyone is back in, and players write and vote over the hub
        await p1.Connection.InvokeAsync("ContinueNight");
        await p1.WaitForStageAsync("Tutorial", gameId: "witty-bits");
        await WaitUntilAsync(() => p1.Rosters.Last().EnumerateArray().All(p => p.GetProperty("alive").GetBoolean()));
        await p1.Connection.InvokeAsync("TutorialDone");
        await PlayWittyBitsAsync([(p1, joined[0].PlayerId), (p2, joined[1].PlayerId)]);

        JsonElement wittyResults = await p1.WaitForStageAsync("Results", gameId: "witty-bits");
        Assert.Equal(4, wittyResults.GetProperty("data").GetProperty("standings").GetArrayLength());

        // --- Shout Out: vote for categories, then buzz in and answer
        await host.Connection.InvokeAsync("ContinueNight");
        await WaitUntilAsync(() => p1.States.Last().GetProperty("stage").GetString() == "Scoreboard");
        await p1.Connection.InvokeAsync("ContinueNight");
        await p1.WaitForStageAsync("Tutorial", gameId: "shout-out");
        await p1.Connection.InvokeAsync("TutorialDone");
        await PlayShoutOutAsync([(p1, joined[0].PlayerId), (p2, joined[1].PlayerId)]);

        JsonElement shoutResults = await p1.WaitForStageAsync("Results", gameId: "shout-out");
        Assert.Equal(4, shoutResults.GetProperty("data").GetProperty("standings").GetArrayLength());
        Assert.Contains(p1.States, st => st.GetProperty("stage").GetString() == "AnswerReveal");

        // --- the finale: the totals decide the winner, and every role is shown
        await host.Connection.InvokeAsync("ContinueNight");
        JsonElement finale = await p1.WaitForStageAsync("Finale");
        JsonElement teams = finale.GetProperty("data").GetProperty("teams");
        int humans = teams.GetProperty("humans").GetInt32(), ai = teams.GetProperty("ai").GetInt32();
        Assert.True(humans + ai > 1000);
        Assert.Equal(humans > ai ? "Humans" : ai > humans ? "AI" : "Tie", finale.GetProperty("data").GetProperty("winner").GetString());
        Assert.Equal(4, finale.GetProperty("data").GetProperty("roles").GetArrayLength());

        // --- the leader sends everyone back to the lobby, and the bots leave
        await p1.Connection.InvokeAsync("ContinueNight");
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

    /// <summary>
    /// Plays Witty Bits the way the phones do, from the states alone: answer your own prompts in each Writing stage,
    /// and vote for the first answer you did not write in each Matchup (sitting out a matchup you wrote).
    /// </summary>
    private static async Task PlayWittyBitsAsync(IReadOnlyList<(Client Client, Guid Id)> players)
    {
        HashSet<string> done = [];
        for (int i = 0; i < 2400; i++)
        {
            JsonElement state = players[0].Client.States.Last();
            if (state.GetProperty("gameId").GetString() != "witty-bits") { await Task.Delay(25); continue; }

            string stage = state.GetProperty("stage").GetString()!;
            if (stage == "Results") return;

            string key = $"{stage}{state.GetProperty("round").GetInt32()}/{state.GetProperty("step").GetInt32()}";
            if (done.Add(key))
            {
                JsonElement data = state.GetProperty("data");
                foreach ((Client client, Guid id) in players)
                {
                    string mine = $"funny take {id.ToString()[..4]}";
                    if (stage == "Writing")
                    {
                        foreach (JsonElement a in data.GetProperty("assignments").EnumerateArray())
                            if (a.GetProperty("playerIds").EnumerateArray().Any(p => p.GetGuid() == id))
                                await client.Connection.InvokeAsync("SubmitWittyAnswer", a.GetProperty("promptId").GetString(), $"{mine} {key}");
                    }
                    else if (stage == "Matchup")
                    {
                        bool lastBit = data.GetProperty("lastBit").GetBoolean();
                        bool author = data.GetProperty("authors").EnumerateArray().Any(p => p.GetGuid() == id);
                        if (author && !lastBit)
                        {
                            await Assert.ThrowsAnyAsync<HubException>(() => client.Connection.InvokeAsync("CastWittyVote", "a"));
                            continue;
                        }

                        JsonElement pick = data.GetProperty("answers").EnumerateArray()
                            .First(e => !e.GetProperty("missing").GetBoolean() && !e.GetProperty("text").GetString()!.StartsWith(mine));
                        await client.Connection.InvokeAsync("CastWittyVote", pick.GetProperty("id").GetString());
                    }
                }
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("Witty Bits never reached its results.");
    }

    /// <summary>
    /// Plays Shout Out from the states alone: vote for the first category, buzz on every open question, and answer "a"
    /// when it is your turn. A refused buzz (too early, someone was faster) is part of the game.
    /// </summary>
    private static async Task PlayShoutOutAsync(IReadOnlyList<(Client Client, Guid Id)> players)
    {
        HashSet<string> done = [];
        for (int i = 0; i < 2400; i++)
        {
            JsonElement state = players[0].Client.States.Last();
            if (state.GetProperty("gameId").GetString() != "shout-out") { await Task.Delay(25); continue; }

            string stage = state.GetProperty("stage").GetString()!;
            if (stage == "Results") return;

            string key = $"{stage}{state.GetProperty("round").GetInt32()}/{state.GetProperty("step").GetInt32()}";
            if (done.Add(key))
            {
                JsonElement data = state.GetProperty("data");
                foreach ((Client client, Guid id) in players)
                {
                    if (stage == "Categories")
                    {
                        string first = data.GetProperty("categories")[0].GetProperty("id").GetString()!;
                        await client.Connection.InvokeAsync("ShoutOutCategories", new[] { first });
                    }
                    else if (stage == "Question")
                    {
                        try { await client.Connection.InvokeAsync("Buzz"); } catch (HubException) { }
                    }
                    else if (stage == "Buzzed" && data.GetProperty("buzzerId").GetGuid() == id)
                    {
                        await client.Connection.InvokeAsync("ShoutOutAnswer", "a");
                    }
                    else if (stage == "Buzzed")
                    {
                        await Assert.ThrowsAnyAsync<HubException>(() => client.Connection.InvokeAsync("ShoutOutAnswer", "a"));
                    }
                }
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("Shout Out never reached its results.");
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
