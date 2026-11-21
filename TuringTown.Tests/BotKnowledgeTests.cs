using System.Text.Json;
using System.Text.Json.Serialization;
using TuringTown.Models;
using TuringTown.Services.Ai;

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
    public async Task No_request_to_a_bot_carries_any_role()
    {
        Harness h = new(humans: 5, o => o.BotMessagesPerRound = 2);
        h.Agent.Chat = _ => null;

        await h.ThroughOnboardingAsync();
        await h.ToAsync(Stage.Voting, 1);
        await h.UntilAsync(() => { lock (h.Agent.VoteRequests) return h.Agent.VoteRequests.Count >= 2; }, "both bots' vote requests");

        List<string> sent = [];
        lock (h.Agent.OnboardingRequests) sent.AddRange(h.Agent.OnboardingRequests.Select(r => JsonSerializer.Serialize(r, Json)));
        lock (h.Agent.ChatRequests) sent.AddRange(h.Agent.ChatRequests.Select(r => JsonSerializer.Serialize(r, Json)));
        lock (h.Agent.VoteRequests) sent.AddRange(h.Agent.VoteRequests.Select(r => JsonSerializer.Serialize(r, Json)));

        Assert.NotEmpty(sent);
        foreach (string request in sent)
        {
            // No role flag in any shape.
            Assert.DoesNotContain("isbot", request, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("wasbot", request, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("role", request, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("teammate", request, StringComparison.OrdinalIgnoreCase);
        }

        // The other players in a chat request are bare aliases, humans and the other bot alike.
        lock (h.Agent.ChatRequests)
        {
            foreach (ChatRequest chat in h.Agent.ChatRequests)
            {
                string[] everyoneElse = h.Room.GetSeats().Where(s => s.Alias != chat.BotAlias).Select(s => s.Alias).Order().ToArray();
                Assert.Equal(everyoneElse, chat.OtherPlayers.Order().ToArray());
            }
        }
    }
}
