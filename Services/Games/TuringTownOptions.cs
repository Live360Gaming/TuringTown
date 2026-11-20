namespace TuringTown.Services.Games;

/// <summary>
/// Timings and limits for a Turing Town game, bound from the "TuringTown" section of appsettings.json.
/// Every value has a default, so the section is optional.
/// </summary>
public sealed class TuringTownOptions
{
    public const string SectionName = "TuringTown";

    /// <summary>Safety limit for the tutorial. The host normally ends it earlier.</summary>
    public int TutorialMaxSeconds { get; set; } = 180;

    public int OnboardingSeconds { get; set; } = 60;
    public int DiscussionSeconds { get; set; } = 120;
    public int VotingSeconds { get; set; } = 30;
    public int RevealSeconds { get; set; } = 8;
    public int ResultsSeconds { get; set; } = 30;

    /// <summary>After this many rounds with a bot still alive, the bots win.</summary>
    public int MaxRounds { get; set; } = 5;

    public int QuestionCount { get; set; } = 3;
    public int AnswerMaxLength { get; set; } = 300;

    public int ChatMaxLength { get; set; } = 200;

    /// <summary>Minimum gap between one player's chat messages.</summary>
    public int ChatMinIntervalMs { get; set; } = 1500;

    /// <summary>Cost control: how many lines each bot may post per discussion.</summary>
    public int BotMessagesPerRound { get; set; } = 5;

    /// <summary>Bots wait a random time in this range before each chat line, so they type like people.</summary>
    public int BotChatMinDelayMs { get; set; } = 4000;
    public int BotChatMaxDelayMs { get; set; } = 12000;

    public int BotVoteMinDelayMs { get; set; } = 3000;
    public int BotVoteMaxDelayMs { get; set; } = 15000;

    /// <summary>How many recent chat lines a bot is shown when it decides what to say or who to vote for.</summary>
    public int BotContextLines { get; set; } = 12;

    /// <summary>The most chat lines kept for late joiners and bot context.</summary>
    public int TranscriptLimit { get; set; } = 300;
}
