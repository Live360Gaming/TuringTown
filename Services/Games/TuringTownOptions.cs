using TuringTown.Models;

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

    /// <summary>How long to write a Quick Fire answer. 0 skips Quick Fire, so round 1 is a Hot Seat.</summary>
    public int QuickFireSeconds { get; set; } = 30;

    public int VotingSeconds { get; set; } = 30;
    public int RevealSeconds { get; set; } = 8;
    public int ResultsSeconds { get; set; } = 30;

    /// <summary>How many rounds a game has. The last one is the Final, whose vote alone decides the winner.</summary>
    public int MaxRounds { get; set; } = 3;

    /// <summary>
    /// What round 1 is worth. Each round doubles it (100, 200, 400 ...). The round's value goes to the side that gains from
    /// its top suspect: the humans if that player is a bot, otherwise the AI team. A tie earns nobody anything.
    /// </summary>
    public int RoundPoints { get; set; } = 100;

    /// <summary>How many onboarding questions of each kind a game asks (a fixed mix, so the form length is predictable).</summary>
    public int TextQuestions { get; set; } = 1;
    public int SingleChoiceQuestions { get; set; } = 1;
    public int MultiChoiceQuestions { get; set; } = 1;

    /// <summary>A copy, so one game can be tuned (for example by the agent it runs on) without changing the shared settings.</summary>
    public TuringTownOptions Clone() => (TuringTownOptions)MemberwiseClone();

    public QuestionMix Mix => new(TextQuestions, SingleChoiceQuestions, MultiChoiceQuestions);

    public int QuestionCount => Mix.Total;

    public int AnswerMaxLength { get; set; } = 300;

    public int ChatMaxLength { get; set; } = 200;

    /// <summary>Minimum gap between one player's chat messages.</summary>
    public int ChatMinIntervalMs { get; set; } = 1500;

    /// <summary>Cost control: how many lines each bot may post per discussion.</summary>
    public int BotMessagesPerRound { get; set; } = 5;

    /// <summary>Bots wait a random time in this range before each chat line, so they type like people.</summary>
    public int BotChatMinDelayMs { get; set; } = 4000;
    public int BotChatMaxDelayMs { get; set; } = 12000;

    /// <summary>A bot "types" for this long per character before its line appears, up to <see cref="BotTypingMaxMs"/>.
    /// The model's own thinking time counts towards it.</summary>
    public int BotTypingMsPerChar { get; set; } = 120;

    public int BotTypingMaxMs { get; set; } = 4000;

    /// <summary>
    /// Percent of @mentions a bot does not answer at once: it gets to them on its next normal turn instead (answering
    /// every mention immediately is a tell).
    /// </summary>
    public int BotMentionIgnorePercent { get; set; } = 15;

    /// <summary>
    /// When true a bot never says much more than the humans do (at most about 1.3x a human's lines this round, and
    /// always at least one), still capped by <see cref="BotMessagesPerRound"/>. Left unset it is on for a game that runs on
    /// the real AI and off for the scripted bots.
    /// </summary>
    public bool? BotPaceToHumans { get; set; }

    /// <summary>How many of its own latest lines a bot is shown, so it stays consistent.</summary>
    public int BotOwnLinesContext { get; set; } = 5;

    /// <summary>
    /// Most players one message may @mention (more are ignored), so a message cannot be used to spam everyone.
    /// </summary>
    public int MaxMentionsPerMessage { get; set; } = 3;

    /// <summary>
    /// A bot that is @mentioned answers after a short pause in this range, instead of waiting for its usual turn.
    /// </summary>
    public int BotMentionReplyMinDelayMs { get; set; } = 1500;
    public int BotMentionReplyMaxDelayMs { get; set; } = 5000;

    public int BotVoteMinDelayMs { get; set; } = 3000;
    public int BotVoteMaxDelayMs { get; set; } = 15000;

    /// <summary>How many recent chat lines a bot is shown when it decides what to say or who to vote for.</summary>
    public int BotContextLines { get; set; } = 25;

    /// <summary>The most chat lines kept for late joiners and bot context.</summary>
    public int TranscriptLimit { get; set; } = 300;
}
