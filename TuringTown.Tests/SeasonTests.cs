using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TuringTown.Services;

namespace TuringTown.Tests;

public class SeasonTests
{
    private static Season At(SeasonMode mode, int month)
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, month, 15, 12, 0, 0, TimeSpan.Zero));
        return new SeasonProvider(Options.Create(new SeasonOptions { Mode = mode }), clock).Current;
    }

    [Theory]
    [InlineData(12, Season.Christmas)]
    [InlineData(11, Season.Default)]
    [InlineData(1, Season.Default)]
    public void Auto_follows_the_month(int month, Season expected) => Assert.Equal(expected, At(SeasonMode.Auto, month));

    [Fact]
    public void Forced_modes_ignore_the_date()
    {
        Assert.Equal(Season.Christmas, At(SeasonMode.Christmas, 6));
        Assert.Equal(Season.Default, At(SeasonMode.Off, 12));
    }
}
