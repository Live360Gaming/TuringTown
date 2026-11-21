namespace TuringTown.Services;

/// <summary>The look the clients use. <see cref="Default"/> is the normal art; a season swaps in its own where it has some.</summary>
public enum Season
{
    Default,
    Christmas
}

/// <summary>How the season is chosen: from the date, or forced on or off.</summary>
public enum SeasonMode
{
    /// <summary>Christmas in December, the default look otherwise.</summary>
    Auto,
    Off,
    Christmas
}

/// <summary>The "Season" section of appsettings.json.</summary>
public sealed class SeasonOptions
{
    public const string SectionName = "Season";

    public SeasonMode Mode { get; set; } = SeasonMode.Auto;
}

/// <summary>Decides which season the clients should show, now.</summary>
public sealed class SeasonProvider(Microsoft.Extensions.Options.IOptions<SeasonOptions> options, TimeProvider clock)
{
    public Season Current => options.Value.Mode switch
    {
        SeasonMode.Christmas => Season.Christmas,
        SeasonMode.Off => Season.Default,
        _ => clock.GetUtcNow().Month == 12 ? Season.Christmas : Season.Default
    };
}
