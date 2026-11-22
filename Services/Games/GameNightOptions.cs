namespace TuringTown.Services.Games;

/// <summary>
/// Timings and scoring for a game night, bound from the optional "GameNight" section of appsettings.json.
/// </summary>
public sealed class GameNightOptions
{
    public const string SectionName = "GameNight";

    /// <summary>The longest the AI may take to choose the games. After this the order is random.</summary>
    public int PlanningMaxSeconds { get; set; } = 15;

    /// <summary>How long the TV shows the chosen order before the first game starts.</summary>
    public int PlanRevealSeconds { get; set; } = 6;

    /// <summary>The Scoreboard between games (the host or leader can continue sooner).</summary>
    public int ScoreboardSeconds { get; set; } = 20;

    /// <summary>The Finale (the host or leader can return to the lobby sooner).</summary>
    public int FinaleSeconds { get; set; } = 45;

    /// <summary>Points the winning side of a Turing Town game adds to its team total.</summary>
    public int TuringTownWinPoints { get; set; } = 1000;

    public GameNightOptions Clone() => (GameNightOptions)MemberwiseClone();
}
