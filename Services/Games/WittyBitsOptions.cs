namespace TuringTown.Services.Games;

/// <summary>
/// Timings and scoring for a Witty Bits game, bound from the optional "WittyBits" section of appsettings.json.
/// See docs/witty-bits.md.
/// </summary>
public sealed class WittyBitsOptions
{
    public const string SectionName = "WittyBits";

    /// <summary>Safety limit for the tutorial. The host normally ends it earlier.</summary>
    public int TutorialMaxSeconds { get; set; } = 120;

    /// <summary>Time to write both answers in a paired round.</summary>
    public int WritingSeconds { get; set; } = 75;

    /// <summary>Time to write the one Last Bit answer.</summary>
    public int LastBitWritingSeconds { get; set; } = 50;

    public int MatchupSeconds { get; set; } = 25;
    public int MatchupResultSeconds { get; set; } = 7;
    public int ResultsSeconds { get; set; } = 25;

    /// <summary>Paired rounds before the Last Bit. Round <i>n</i> scores <i>n</i> times the points.</summary>
    public int PairRounds { get; set; } = 2;

    public int AnswerMaxLength { get; set; } = 80;

    /// <summary>Points a matchup is worth in round 1, shared by vote: an answer with 3 of 4 votes gets 750.</summary>
    public int MatchupPoints { get; set; } = 1000;

    /// <summary>Extra points (times the round) for an answer that gets every vote.</summary>
    public int SweepBonus { get; set; } = 250;

    /// <summary>Points (times the round) for the only answer in a matchup, when the other player did not answer.</summary>
    public int ForfeitPoints { get; set; } = 500;

    /// <summary>Points per vote in the Last Bit.</summary>
    public int LastBitPointsPerVote { get; set; } = 400;

    /// <summary>Bots take a random time to write, like people (the agent's own time counts towards it).</summary>
    public int BotAnswerMinDelayMs { get; set; } = 6000;
    public int BotAnswerMaxDelayMs { get; set; } = 25000;

    public int BotVoteMinDelayMs { get; set; } = 2000;
    public int BotVoteMaxDelayMs { get; set; } = 9000;

    public WittyBitsOptions Clone() => (WittyBitsOptions)MemberwiseClone();
}
