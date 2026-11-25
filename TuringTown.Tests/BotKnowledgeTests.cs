using System.Text.Json;
using System.Text.Json.Serialization;
using TuringTown.Models;
using TuringTown.Services.Ai;
using TuringTown.Services.Storage;

namespace TuringTown.Tests;

/// <summary>
/// The knowledge rule: a bot knows it is a bot, the rules and the room's public information, and nothing about which
/// other player is human or bot (not even its teammate).
/// </summary>
public class BotKnowledgeTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// A request must carry no role for anyone still in the game. The only role that may appear is a player the Reveal
    /// already unmasked, in the round history (which every player saw too).
    /// </summary>
    private static void AssertNoRoleForLivingPlayers(string requestJson)
    {
        using JsonDocument doc = JsonDocument.Parse(requestJson);

        void Walk(JsonElement element, string path)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        string name = property.Name.ToLowerInvariant();
                        Assert.NotEqual("isbot", name);
                        Assert.DoesNotContain("role", name);
                        Assert.DoesNotContain("teammate", name);
                        if (name == "wasbot") Assert.Contains("history", path, StringComparison.OrdinalIgnoreCase);
                        Walk(property.Value, $"{path}.{property.Name}");
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (JsonElement item in element.EnumerateArray()) Walk(item, path + "[]");
                    break;
            }
        }

        Walk(doc.RootElement, "");

        // And a revealed role only ever belongs to someone who is already out.
        JsonElement context = doc.RootElement.GetProperty("context");
        string[] alive = context.GetProperty("alivePlayers").EnumerateArray().Select(a => a.GetString()!).ToArray();
        foreach (JsonElement outcome in context.GetProperty("history").EnumerateArray())
        {
            if (outcome.GetProperty("eliminatedAlias").GetString() is { } out_)
            {
                Assert.DoesNotContain(out_, alive);
                Assert.NotEqual(context.GetProperty("botAlias").GetString(), out_);
            }
        }
    }

    private static string[] AllRequests(Harness h)
    {
        List<string> sent = [];
        lock (h.Agent.OnboardingRequests) sent.AddRange(h.Agent.OnboardingRequests.Select(r => JsonSerializer.Serialize(r, Json)));
        lock (h.Agent.ChatRequests) sent.AddRange(h.Agent.ChatRequests.Select(r => JsonSerializer.Serialize(r, Json)));
        lock (h.Agent.VoteRequests) sent.AddRange(h.Agent.VoteRequests.Select(r => JsonSerializer.Serialize(r, Json)));
        lock (h.Agent.RoundEndRequests) sent.AddRange(h.Agent.RoundEndRequests.Select(r => JsonSerializer.Serialize(r, Json)));
        return sent.ToArray();
    }

    [Fact]
    public async Task Every_bot_may_vote_for_every_living_player_but_itself_including_its_teammate()
    {
        // Five humans means two bots.
        Harness h = new(humans: 5);
        Guid[] bots = h.BotIds.ToArray();
        Assert.Equal(2, bots.Length);

        await h.ThroughOnboardingAsync();
        await h.ToAsync(Stage.Voting, 1);
        await h.UntilAsync(() => { lock (h.Agent.VoteRequests) return h.Agent.VoteRequests.Count >= 2; }, "both bots' vote requests");

        VoteRequest[] requests;
        lock (h.Agent.VoteRequests) requests = h.Agent.VoteRequests.ToArray();

        foreach (PlayerSeat bot in h.Room.GetSeats().Where(s => s.IsBot))
        {
            VoteRequest request = requests.Single(r => r.BotAlias == bot.Alias);
            Guid[] candidates = request.Candidates.Select(c => c.Id).Order().ToArray();
            Guid[] everyoneElse = h.Room.GetSeats().Where(s => s.Id != bot.Id).Select(s => s.Id).Order().ToArray();

            Assert.Equal(everyoneElse, candidates);
            Assert.DoesNotContain(bot.Id, candidates);
            // The other bot is simply one of the candidates, indistinguishable from a human.
            Assert.Contains(bots.Single(b => b != bot.Id), candidates);
        }
    }

    [Fact]
    public async Task A_bot_can_vote_for_its_teammate_without_knowing_it()
    {
        Harness h = new(humans: 5);
        // Each bot votes for the other one.
        h.Agent.Vote = r =>
        {
            Guid self = h.Room.GetSeats().First(s => s.Alias == r.BotAlias).Id;
            return h.BotIds.Single(b => b != self);
        };

        await h.ThroughOnboardingAsync();
        await h.ToAsync(Stage.Reveal);

        // The humans did not vote: the two bots voted for each other, so one vote each and a tie.
        RevealData reveal = h.LastReveal();
        string[] botAliases = h.Room.GetSeats().Where(s => s.IsBot).Select(s => s.Alias).Order().ToArray();

        Assert.Equal(botAliases, reveal.Tally.Select(t => t.Alias).Order().ToArray());
        Assert.All(reveal.Tally, t => Assert.Equal(1, t.Votes));
        Assert.Null(reveal.Eliminated);
    }

    [Fact]
    public async Task No_request_to_a_bot_carries_any_role_for_a_living_player()
    {
        Harness h = new(humans: 5, o => o.BotMessagesPerRound = 2);
        h.Agent.Chat = _ => null;

        await h.ThroughOnboardingAsync();
        await h.ToAsync(Stage.Voting, 1);
        await h.UntilAsync(() => { lock (h.Agent.VoteRequests) return h.Agent.VoteRequests.Count >= 2; }, "both bots' vote requests");

        string[] sent = AllRequests(h);
        Assert.NotEmpty(sent);
        Assert.All(sent, AssertNoRoleForLivingPlayers);

        // The other players in a chat request are bare aliases, humans and the other bot alike.
        lock (h.Agent.ChatRequests)
        {
            foreach (ChatRequest chat in h.Agent.ChatRequests)
            {
                string[] everyoneElse = h.Room.GetSeats().Where(s => s.Alias != chat.BotAlias).Select(s => s.Alias).Order().ToArray();
                Assert.Equal(everyoneElse, chat.OtherPlayers.Order().ToArray());
                Assert.Equal(everyoneElse, chat.Context.AlivePlayers.Order().ToArray());
            }
        }
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    public async Task A_bot_gets_its_own_key_and_alias(int humans, int bots)
    {
        Harness h = new(humans, o => o.BotMessagesPerRound = 1);
        h.Agent.Chat = _ => null;

        await h.ThroughOnboardingAsync();
        await h.ToAsync(Stage.Voting, 1);
        await h.UntilAsync(() => { lock (h.Agent.VoteRequests) return h.Agent.VoteRequests.Count >= bots; }, "the bots' vote requests");

        VoteRequest[] requests;
        lock (h.Agent.VoteRequests) requests = h.Agent.VoteRequests.ToArray();

        foreach (PlayerSeat bot in h.Room.GetSeats().Where(s => s.IsBot))
        {
            BotContext context = requests.First(r => r.BotAlias == bot.Alias).Context;
            Assert.Equal(h.Engine.GameId, context.Key.GameId);
            Assert.Equal(bot.Id, context.Key.BotId);
            Assert.Equal(bot.Alias, context.BotAlias);
            Assert.DoesNotContain(bot.Alias, context.AlivePlayers);
        }
    }

    [Fact]
    public async Task Two_bots_share_nothing_with_each_other()
    {
        Harness h = new(humans: 5, o => o.BotMessagesPerRound = 1);
        h.Agent.Chat = _ => null;
        // Each bot's text answer carries its own marker, so any leak across bots is visible.
        h.Agent.Answer = r => r.Questions
            .Select(q => q.Kind == QuestionKind.Text
                ? new OnboardingAnswer(q.Id, $"marker of {r.BotAlias}", [])
                : ScriptedBotAgent.ScriptedAnswer(q, r.HumanAnswers))
            .ToArray();

        await h.ThroughOnboardingAsync();
        await h.ToAsync(Stage.Voting, 1);
        await h.UntilAsync(() => { lock (h.Agent.VoteRequests) return h.Agent.VoteRequests.Count >= 2; }, "both bots' vote requests");

        PlayerSeat[] bots = h.Room.GetSeats().Where(s => s.IsBot).ToArray();
        Assert.NotEqual(bots[0].Alias, bots[1].Alias);

        VoteRequest[] requests;
        lock (h.Agent.VoteRequests) requests = h.Agent.VoteRequests.ToArray();

        foreach (PlayerSeat bot in bots)
        {
            PlayerSeat other = bots.Single(b => b.Id != bot.Id);
            BotContext context = requests.Single(r => r.BotAlias == bot.Alias).Context;
            string text = JsonSerializer.Serialize(context, Json);

            // Its own persona, never the other bot's.
            Assert.Contains($"marker of {bot.Alias}", text);
            Assert.DoesNotContain($"marker of {other.Alias}", text);
            Assert.Equal(bot.Id, context.Key.BotId);
        }
    }

    [Fact]
    public async Task A_bot_is_shown_the_finished_rounds_as_the_reveal_showed_them()
    {
        Harness h = new(humans: 3, o => { o.BotMessagesPerRound = 1; o.MaxRounds = 2; });
        h.Agent.Chat = _ => null;
        await h.ThroughOnboardingAsync();
        await h.ToAsync(Stage.Voting, 1);

        // Everyone suspects the first human. An early vote eliminates nobody.
        Player out1 = h.Humans[0];
        foreach (Player human in h.Humans.Skip(1)) await h.VoteAsync(human, out1.Id);
        await h.VoteAsync(h.Humans[0], h.Humans[1].Id);
        await h.ToAsync(Stage.Discussion, 2);
        await h.UntilAsync(() => { lock (h.Agent.ChatRequests) return h.Agent.ChatRequests.Any(r => r.Round == 2); }, "a round 2 chat request");

        ChatRequest request;
        lock (h.Agent.ChatRequests) request = h.Agent.ChatRequests.First(r => r.Round == 2);
        RoundOutcome outcome = Assert.Single(request.Context.History);

        Assert.Equal(1, outcome.Round);
        Assert.Null(outcome.EliminatedAlias);
        Assert.Null(outcome.WasBot);
        Assert.Equal(h.LastReveal().Tally.Select(t => (t.Alias, t.Votes)), outcome.Tally.Select(t => (t.Alias, t.Votes)));
        // The suspected player is still in the room.
        Assert.Contains(out1.Alias, request.Context.AlivePlayers);
        AssertNoRoleForLivingPlayers(JsonSerializer.Serialize(request, Json));
    }
}
