namespace TuringTown.Services.Games;

/// <summary>
/// Timings, scoring and bot behaviour for a Shout Out game, bound from the optional "ShoutOut" section of
/// appsettings.json. See docs/shout-out.md.
/// </summary>
public sealed class ShoutOutOptions
{
    public const string SectionName = "ShoutOut";

    /// <summary>Safety limit for the tutorial. The host normally ends it earlier.</summary>
    public int TutorialMaxSeconds { get; set; } = 90;

    /// <summary>The category vote.</summary>
    public int CategoriesSeconds { get; set; } = 20;

    /// <summary>How many categories are offered, and how many each player may pick.</summary>
    public int CategoryChoices { get; set; } = 6;
    public int MaxCategoryPicks { get; set; } = 2;

    /// <summary>The questions in a game; the last <see cref="DoubleQuestions"/> are worth double.</summary>
    public int Questions { get; set; } = 10;
    public int DoubleQuestions { get; set; } = 3;

    /// <summary>Time to read a question before buzzing opens. A buzz before then locks the player out for <see cref="EarlyBuzzLockoutMs"/>.</summary>
    public int ReadSeconds { get; set; } = 3;
    public int EarlyBuzzLockoutMs { get; set; } = 1000;

    /// <summary>How long buzzing stays open, and how long it reopens for after a wrong answer.</summary>
    public int BuzzSeconds { get; set; } = 10;
    public int RebuzzSeconds { get; set; } = 6;

    /// <summary>The buzzer's time to pick an answer.</summary>
    public int AnswerSeconds { get; set; } = 6;

    public int RevealSeconds { get; set; } = 5;
    public int ResultsSeconds { get; set; } = 25;

    /// <summary>A right answer earns this (twice in the double round); a wrong one or a timeout costs <see cref="WrongPoints"/>.</summary>
    public int CorrectPoints { get; set; } = 500;
    public int WrongPoints { get; set; } = 250;

    /// <summary>When a bot casts its category vote.</summary>
    public int BotCategoryMinDelayMs { get; set; } = 2000;
    public int BotCategoryMaxDelayMs { get; set; } = 9000;

    /// <summary>
    /// How often a bot knows an answer, drawn once per bot per game between these two. Bots are meant to play like a
    /// decent human, never like an encyclopedia: they miss, they guess, and they are not always first.
    /// </summary>
    public double BotKnowledgeMin { get; set; } = 0.55;
    public double BotKnowledgeMax { get; set; } = 0.75;

    public ShoutOutOptions Clone() => (ShoutOutOptions)MemberwiseClone();
}
