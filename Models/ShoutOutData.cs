namespace TuringTown.Models;

// The stage data of Shout Out, the buzz-in trivia game (see docs/shout-out.md). Like every stage's data it is sent to
// every screen, so it never says which seat is a bot.

/// <summary>A trivia category the room can vote for.</summary>
public readonly record struct TriviaCategory(string Id, string Name);

/// <summary>One answer to a trivia question. Ids are <c>a</c>..<c>d</c>.</summary>
public readonly record struct TriviaOption(string Id, string Text);

/// <summary>The Categories stage: pick up to <paramref name="MaxPicks"/> of the categories.</summary>
public readonly record struct CategoriesData(IReadOnlyList<TriviaCategory> Categories, int MaxPicks);

/// <summary>
/// A question that is up (the Question stage) or being answered (the Buzzed stage). Buzzing opens at
/// <paramref name="BuzzOpensAtUtcMs"/>, after a moment to read; a buzz before then locks the player out for a second.
/// <paramref name="Out"/> are the players who already got this one wrong (they cannot buzz again).
/// <paramref name="BuzzerId"/> and <paramref name="BuzzerAlias"/> are set in the Buzzed stage.
/// </summary>
public sealed record TriviaQuestionData(
    int Index,
    int Count,
    bool Double,
    string Category,
    string Text,
    IReadOnlyList<TriviaOption> Options,
    long BuzzOpensAtUtcMs,
    IReadOnlyList<Guid> Out,
    Guid? BuzzerId = null,
    string? BuzzerAlias = null);

/// <summary>One buzz on a question and how it went.</summary>
public readonly record struct TriviaAttempt(Guid PlayerId, string Alias, string? OptionId, bool Correct, int Points);

/// <summary>The AnswerReveal stage: the right answer and every attempt, in buzz order.</summary>
public sealed record AnswerRevealData(
    int Index,
    int Count,
    string Category,
    string Text,
    IReadOnlyList<TriviaOption> Options,
    string CorrectId,
    IReadOnlyList<TriviaAttempt> Attempts,
    IReadOnlyList<Standing> Leaders);

/// <summary>The end of Shout Out: everyone's points this game.</summary>
public readonly record struct ShoutOutResultsData(IReadOnlyList<Standing> Standings);
