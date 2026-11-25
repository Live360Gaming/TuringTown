using TuringTown.Services.Ai;

namespace TuringTown.Tests;

public class VoteChoiceTests
{
    [Fact]
    public void Every_candidate_keeps_a_chance_and_suspicion_raises_it()
    {
        Assert.Equal(VoteChoice.Floor, VoteChoice.Weight(0));
        Assert.True(VoteChoice.Weight(0.9) > VoteChoice.Weight(0.5));
        Assert.Equal(VoteChoice.Weight(1), VoteChoice.Weight(3));
        Assert.Equal(VoteChoice.Weight(0), VoteChoice.Weight(-1));
    }

    [Fact]
    public void The_roll_walks_the_weights_in_order()
    {
        double[] suspicion = [0, 1];
        double total = VoteChoice.Weight(0) + VoteChoice.Weight(1);

        Assert.Equal(0, VoteChoice.Pick(suspicion, 0));
        Assert.Equal(0, VoteChoice.Pick(suspicion, VoteChoice.Weight(0) / total - 0.001));
        Assert.Equal(1, VoteChoice.Pick(suspicion, VoteChoice.Weight(0) / total + 0.001));
        Assert.Equal(1, VoteChoice.Pick(suspicion, 0.9999999));
    }

    [Fact]
    public void Equal_suspicion_is_a_fair_draw()
    {
        Random rng = new(7);
        int[] counts = new int[3];
        for (int i = 0; i < 3000; i++) counts[VoteChoice.Pick([0.4, 0.4, 0.4], rng.NextDouble())]++;

        Assert.All(counts, c => Assert.InRange(c, 850, 1150));
    }

    [Fact]
    public void There_must_be_someone_to_vote_for() =>
        Assert.Throws<ArgumentException>(() => VoteChoice.Pick([], 0.5));
}
