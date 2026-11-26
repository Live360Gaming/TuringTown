using System.Text.Json;
using TuringTown.Models;
using TuringTown.Services.Games;

namespace TuringTown.Tests;

/// <summary>
/// Turing Town's secret Imposter: one human who scores by being voted out as the AI. Only they are told, and nobody
/// else learns it before the Results.
/// </summary>
public class ImposterTests
{
    private static async Task<Harness> GameAsync(int humans, Action<TuringTownOptions>? configure = null)
    {
        Harness h = new(humans, o => { o.RapidFireSeconds = 30; configure?.Invoke(o); });
        await h.ThroughOnboardingAsync();
        return h;
    }

    [Fact]
    public async Task Only_the_imposter_is_told_and_nothing_broadcast_says_who_it_is_until_the_results()
    {
        Harness h = await GameAsync(humans: 3);
        (Guid imposter, SecretInfo secret) = Assert.Single(h.Secrets);
        Assert.Contains(h.Humans, p => p.Id == imposter);
        Assert.Equal("imposter", secret.Kind);
        Assert.DoesNotContain("bot", JsonSerializer.Serialize(secret), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(secret, h.Engine.SecretFor(imposter));
        Assert.All(h.Humans.Where(p => p.Id != imposter), p => Assert.Null(h.Engine.SecretFor(p.Id)));

        await h.ToAsync(Stage.Results);
        RoleReveal role = Assert.Single(h.Results().Roles, r => r.Imposter);
        Assert.Equal(imposter, role.PlayerId);
        Assert.False(role.WasBot);
        Assert.All(h.Messages.Where(m => m.StageAtSend != Stage.Results && m.StageAtSend != Stage.Lobby),
            m => Assert.DoesNotContain("\"imposter\":true", m.Json, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public async Task There_is_no_imposter_in_a_small_room_or_when_it_is_turned_off(int humans, bool enabled)
    {
        Harness h = await GameAsync(humans, o => o.Imposter = enabled);
        Assert.Empty(h.Secrets);
        await h.ToAsync(Stage.Results);
        Assert.DoesNotContain(h.Results().Roles, r => r.Imposter);
    }

    [Fact]
    public async Task Being_suspected_and_voted_out_earns_the_imposter_points_and_loses_the_game_for_the_humans()
    {
        Harness h = await GameAsync(humans: 3);
        Guid imposter = Assert.Single(h.Secrets).PlayerId;
        Player[] others = h.Humans.Where(p => p.Id != imposter).ToArray();
        Player me = h.Humans.Single(p => p.Id == imposter);

        await h.ToAsync(Stage.Voting, 1);
        foreach (Player p in others) await h.VoteAsync(p, imposter);
        await h.VoteAsync(me, h.BotId);
        await h.UntilAsync(() => h.Engine.SeatBonusTotals.GetValueOrDefault(imposter) == h.Options.ImposterSuspectPoints, "the suspicion bonus");

        await h.UntilAsync(() => h.Stage == Stage.Voting && h.Room.State.Round > 1, "the final vote");
        foreach (Player p in others) await h.VoteAsync(p, imposter);
        await h.VoteAsync(me, h.BotId);
        await h.ToAsync(Stage.Results);

        Assert.Equal("Bots", h.Results().Winner);
        Assert.Equal(h.Options.ImposterSuspectPoints + h.Options.ImposterEliminatedPoints, h.Engine.SeatBonusTotals[imposter]);
        // The rounds' value still goes to the AI team (the top suspect was a human); the bonus is the Imposter's alone.
        Assert.Equal(0, h.Engine.RoundPointTotals.Humans);
    }
}
