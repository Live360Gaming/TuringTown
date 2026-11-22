using System.Text.Json.Serialization;

namespace TuringTown.Services.Ai;

/// <summary>A player as the whole room sees them tonight: alias and points so far. No role.</summary>
public sealed record PlayerNote(string Alias, int Points);

/// <summary>
/// What the AI team knows about the game night, shared by every bot in every game of the night: the goal, the games
/// and their order, the score, and what happened in public (Witty Bits answers and votes, Turing Town reveals).
///
/// It holds only what the room has seen. The team totals are in it only once they are public (every bot unmasked,
/// as on the Scoreboard), so a bot cannot work out which seat is its teammate from the totals. It never says which
/// seat is a bot. Player text in <see cref="Highlights"/> is untrusted and only ever reaches a prompt as quoted data.
/// </summary>
public sealed record TeamContext(
    string Goal,
    IReadOnlyList<string> Games,
    string? CurrentGame,
    int GameNumber,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TeamScore? Score,
    IReadOnlyList<PlayerNote> Players,
    IReadOnlyList<string> Highlights)
{
    public const string DefaultGoal = "Finish the game night with more points than the humans. Every game counts.";

    public static TeamContext Empty { get; } = new(DefaultGoal, [], null, 0, null, [], []);
}

/// <summary>The Humans vs AI totals, once they are public.</summary>
public sealed record TeamScore(int Humans, int Ai);
