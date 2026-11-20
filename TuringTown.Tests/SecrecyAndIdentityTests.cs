using System.Text.Json;
using System.Text.Json.Serialization;
using TuringTown.Models;
using TuringTown.Services.Ai;

namespace TuringTown.Tests;

public class SecrecyAndIdentityTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    // ---------------------------------------------------------------- the secret: who is a bot

    [Fact]
    public async Task Nothing_sent_to_clients_reveals_a_bot_before_the_reveal()
    {
        Harness h = new(humans: 3, o => o.BotMessagesPerRound = 2);
        h.Agent.Chat = _ => "a perfectly human line";
        Player a = h.Humans[0], b = h.Humans[1], c = h.Humans[2];

        await h.ThroughOnboardingAsync();
        await h.UntilAsync(() => h.Engine.GetTranscript().Count > 0, "bot chat");
        await h.ToAsync(Stage.Voting, 1);
        await h.VoteAsync(a, b.Id);
        await h.VoteAsync(b, c.Id);
        await h.VoteAsync(c, a.Id);
        await h.ToAsync(Stage.Reveal, 1);

        // Everything broadcast up to and including the tutorial, onboarding, discussion and voting.
        var beforeReveal = h.Messages.Where(m => m.StageAtSend is Stage.Lobby or Stage.Tutorial or Stage.Onboarding or Stage.Discussion or Stage.Voting).ToList();
        Assert.NotEmpty(beforeReveal);
        foreach (var message in beforeReveal)
        {
            Assert.DoesNotContain("bot", message.Json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("role", message.Json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task The_join_result_has_no_roles_and_no_tokens_and_a_late_joiner_sees_the_chat()
    {
        Harness h = new(humans: 2, o => o.BotMessagesPerRound = 1);
        h.Agent.Chat = _ => "hello from the table";
        await h.ThroughOnboardingAsync();
        await h.UntilAsync(() => h.Engine.GetTranscript().Count > 0, "bot chat");

        JoinResult result = new(h.Room.State, h.Room.GetRoster(), h.Room.GetRoster()[0], h.Engine.GetTranscript(), h.Time.GetUtcNow().ToUnixTimeMilliseconds(), h.Engine.GetUnmasked());
        string json = JsonSerializer.Serialize(result, Json);

        Assert.DoesNotContain("bot", json, StringComparison.OrdinalIgnoreCase);
        foreach (Player human in h.Humans)
            Assert.DoesNotContain(human.Token, json);
        Assert.Contains("hello from the table", json);
    }

    [Fact]
    public async Task The_roster_is_sorted_by_alias_so_seat_order_never_gives_a_bot_away()
    {
        Harness h = new(humans: 4);
        await h.SettleAsync();

        string[] aliases = h.Room.GetRoster().Select(p => p.Alias).ToArray();
        Assert.Equal(aliases.OrderBy(a => a, StringComparer.Ordinal), aliases);
    }

    [Fact]
    public async Task Every_role_is_revealed_with_the_results()
    {
        Harness h = new(humans: 2);
        await h.ThroughOnboardingAsync();
        await h.ToAsync(Stage.Voting, 1);
        foreach (Player human in h.Humans) await h.VoteAsync(human, h.BotId);
        await h.ToAsync(Stage.Results);

        ResultsData results = h.Results();
        Assert.Equal(3, results.Roles.Count);
        Assert.Equal(1, results.Roles.Count(r => r.WasBot));
        Assert.Equal(h.BotId, results.Roles.Single(r => r.WasBot).PlayerId);
    }

    // ---------------------------------------------------------------- identity

    [Fact]
    public void Aliases_and_characters_are_unique_across_a_full_room()
    {
        Room room = new(RoomCode.CreateRandomRoomCode(), new GameOptions { MaxPlayers = 8, MinPlayers = 2 });
        for (int i = 0; i < 8; i++)
            Assert.True(room.TryAddPlayer(out _, out _));

        PlayerInfo[] roster = room.GetRoster().ToArray();
        Assert.Equal(8, roster.Select(p => p.Alias).Distinct().Count());

        // There are 6 human-looking characters for 8 seats, so every one is used before any face repeats.
        Assert.Equal(Characters.All.Length, roster.Select(p => p.CharacterId).Distinct().Count());
        Assert.All(roster, p => Assert.Contains(p.CharacterId, Characters.All));
    }

    [Fact]
    public void Robots_are_never_handed_out_as_characters()
    {
        Assert.All(Characters.All, key => Assert.StartsWith("character_", key));
        Assert.DoesNotContain(Characters.All, key => key.Contains("robot"));
    }

    [Fact]
    public async Task A_bot_is_only_unmasked_once_its_reveal_has_been_announced()
    {
        Harness h = new(humans: 2);
        await h.ThroughOnboardingAsync();
        await h.ToAsync(Stage.Voting, 1);

        // Nobody has been found yet.
        Assert.Empty(h.Engine.GetUnmasked());

        foreach (Player human in h.Humans) await h.VoteAsync(human, h.BotId);
        await h.ToAsync(Stage.Reveal, 1);

        Assert.Equal([h.BotId], h.Engine.GetUnmasked());
    }

    [Fact]
    public async Task A_human_who_is_voted_out_is_never_unmasked_and_the_results_unmask_the_bots()
    {
        Harness h = new(humans: 3, o => o.MaxRounds = 1);
        Player a = h.Humans[0], b = h.Humans[1], c = h.Humans[2];
        h.Agent.Vote = _ => a.Id;

        await h.ThroughOnboardingAsync();
        await h.ToAsync(Stage.Voting, 1);
        await h.VoteAsync(a, b.Id);
        await h.VoteAsync(b, a.Id);
        await h.VoteAsync(c, a.Id);
        await h.ToAsync(Stage.Reveal, 1);

        // A is out, but A is human, so nothing is unmasked.
        Assert.Empty(h.Engine.GetUnmasked());

        // The round limit ends the game; now every bot is public.
        await h.ToAsync(Stage.Results);
        Assert.Equal(h.BotIds.OrderBy(id => id), h.Engine.GetUnmasked().OrderBy(id => id));
    }

    [Fact]
    public void An_alias_is_a_color_and_a_fruit_with_a_matching_hex_color()
    {
        for (int i = 0; i < 50; i++)
        {
            var (name, color) = Aliases.PickRandom([]);
            Assert.Equal(2, name.Split(' ').Length);
            Assert.Matches("^#[0-9a-f]{6}$", color);
        }
    }

    [Fact]
    public void A_collision_falls_back_to_a_numeric_suffix_instead_of_looping_forever()
    {
        // Take every possible alias, then ask for one more.
        HashSet<string> all = [];
        for (int i = 0; i < 20000 && all.Count < 160; i++)
            all.Add(Aliases.PickRandom([]).Name);

        var (name, _) = Aliases.PickRandom(all);
        Assert.DoesNotContain(name, all);
        Assert.EndsWith("2", name);
    }

    [Fact]
    public void Joining_checks_the_players_secret_token()
    {
        Room room = new(RoomCode.CreateRandomRoomCode(), new GameOptions());
        Assert.True(room.TryAddPlayer(out Player? player, out _));

        Assert.True(room.TryGetPlayer(player!.Id, player.Token, out _));
        Assert.False(room.TryGetPlayer(player.Id, "wrong", out _));
        Assert.False(room.TryGetPlayer(player.Id, null, out _));
        Assert.False(room.TryGetPlayer(Guid.NewGuid(), player.Token, out _));
    }

    [Fact]
    public void A_refresh_does_not_mark_the_player_offline_when_the_old_connection_closes_late()
    {
        Room room = new(RoomCode.CreateRandomRoomCode(), new GameOptions());
        Assert.True(room.TryAddPlayer(out Player? player, out _));

        room.SetConnected(player!.Id, "old");
        room.SetConnected(player.Id, "new");

        Assert.False(room.SetDisconnected(player.Id, "old"));
        Assert.True(room.GetRoster().Single().Connected);
        Assert.True(room.SetDisconnected(player.Id, "new"));
        Assert.False(room.GetRoster().Single().Connected);
    }

    [Fact]
    public void Nobody_can_join_once_the_game_has_started_and_the_player_limit_holds()
    {
        Room room = new(RoomCode.CreateRandomRoomCode(), new GameOptions { MaxPlayers = 2, MinPlayers = 2 });
        Assert.True(room.TryAddPlayer(out _, out _));
        Assert.True(room.TryAddPlayer(out _, out _));
        Assert.False(room.TryAddPlayer(out _, out RoomJoinError full));
        Assert.Equal(RoomJoinError.RoomFull, full);

        Assert.Equal(RoomStartError.None, room.TryStart(_ => 1));
        Assert.False(room.TryAddPlayer(out _, out RoomJoinError started));
        Assert.Equal(RoomJoinError.GameStarted, started);
        Assert.Equal(RoomStartError.NotInLobby, room.TryStart(_ => 1));
    }

    [Fact]
    public void The_game_needs_the_minimum_number_of_players_to_start()
    {
        Room room = new(RoomCode.CreateRandomRoomCode(), new GameOptions { MaxPlayers = 6, MinPlayers = 2 });
        Assert.True(room.TryAddPlayer(out _, out _));
        Assert.Equal(RoomStartError.NotEnoughPlayers, room.TryStart(_ => 1));
        Assert.Equal(Stage.Lobby, room.State.Stage);
    }

    // ---------------------------------------------------------------- the agent never stalls the game

    private sealed class BrokenAgent : IBotAgent
    {
        public Task<IReadOnlyList<OnboardingQuestion>> GenerateOnboardingQuestionsAsync(QuestionRequest r, CancellationToken ct) => throw new InvalidOperationException("down");
        public Task<IReadOnlyList<string>> AnswerOnboardingAsync(OnboardingAnswerRequest r, CancellationToken ct) => throw new InvalidOperationException("down");
        public Task<string?> NextChatMessageAsync(ChatRequest r, CancellationToken ct) => throw new InvalidOperationException("down");
        public Task<Guid?> CastVoteAsync(VoteRequest r, CancellationToken ct) => throw new InvalidOperationException("down");
    }

    private sealed class HangingAgent : IBotAgent
    {
        public async Task<IReadOnlyList<OnboardingQuestion>> GenerateOnboardingQuestionsAsync(QuestionRequest r, CancellationToken ct) { await Task.Delay(Timeout.Infinite, ct); return []; }
        public async Task<IReadOnlyList<string>> AnswerOnboardingAsync(OnboardingAnswerRequest r, CancellationToken ct) { await Task.Delay(Timeout.Infinite, ct); return []; }
        public async Task<string?> NextChatMessageAsync(ChatRequest r, CancellationToken ct) { await Task.Delay(Timeout.Infinite, ct); return null; }
        public async Task<Guid?> CastVoteAsync(VoteRequest r, CancellationToken ct) { await Task.Delay(Timeout.Infinite, ct); return null; }
    }

    [Fact]
    public async Task An_erroring_agent_is_answered_by_the_fallback()
    {
        FallbackBotAgent agent = new(new BrokenAgent(), new ScriptedBotAgent(), TimeSpan.FromSeconds(5));
        ContentPolicy policy = new(true);

        IReadOnlyList<OnboardingQuestion> questions = await agent.GenerateOnboardingQuestionsAsync(new QuestionRequest(policy, 3), default);
        Assert.Equal(3, questions.Count);

        Guid id = Guid.NewGuid();
        Guid? vote = await agent.CastVoteAsync(new VoteRequest("Red Apple", "", policy, 1, [new VoteCandidate(id, "Blue Kiwi")], []), default);
        Assert.Equal(id, vote);
    }

    [Fact]
    public async Task A_hanging_agent_times_out_into_the_fallback()
    {
        FallbackBotAgent agent = new(new HangingAgent(), new ScriptedBotAgent(), TimeSpan.FromMilliseconds(50));

        IReadOnlyList<string> answers = await agent
            .AnswerOnboardingAsync(new OnboardingAnswerRequest("Red Apple", new ContentPolicy(true), [new OnboardingQuestion("q1", "Why?", false)], []), default)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(answers);
    }

    [Fact]
    public async Task Cancelling_the_caller_is_not_swallowed_by_the_fallback()
    {
        FallbackBotAgent agent = new(new HangingAgent(), new ScriptedBotAgent(), TimeSpan.FromSeconds(30));
        using CancellationTokenSource cts = new();
        Task<IReadOnlyList<OnboardingQuestion>> call = agent.GenerateOnboardingQuestionsAsync(new QuestionRequest(new ContentPolicy(true), 3), cts.Token);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
    }

    [Fact]
    public async Task A_broken_agent_cannot_stop_the_game_from_finishing()
    {
        // An agent that throws on everything: the questions fall back to the built-in ones and the bots just abstain.
        Harness h = new(humans: 2, o => o.BotMessagesPerRound = 2, agent: new BrokenAgent());

        await h.ThroughOnboardingAsync();
        await h.ToAsync(Stage.Voting, 1);

        // The bot's failed vote counts as an abstention, so the vote ends as soon as the humans are done.
        DateTimeOffset before = h.Time.GetUtcNow();
        foreach (Player human in h.Humans) await h.VoteAsync(human, h.BotId);
        await h.ToAsync(Stage.Results);

        Assert.Equal("Humans", h.Results().Winner);
        Assert.True((h.Time.GetUtcNow() - before).TotalSeconds < h.Options.VotingSeconds + h.Options.RevealSeconds + 2);
    }
}
