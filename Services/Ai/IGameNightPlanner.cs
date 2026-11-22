using System.Security.Cryptography;
using TuringTown.Models;

namespace TuringTown.Services.Ai;

/// <summary>A game the planner may pick, described for the AI (no rules engine detail, just what it is like).</summary>
public sealed record PlannableGame(string GameId, string Title, string Description);

/// <summary>
/// What the AI team knows when it plans the night: the games it can choose from, who is playing (aliases only) and
/// the room's content policy. It never learns anything secret: there is nothing secret yet.
/// </summary>
public sealed record GameNightPlanRequest(
    IReadOnlyList<PlannableGame> Games,
    IReadOnlyList<string> Players,
    int BotCount,
    ContentPolicy Policy);

/// <summary>The games in the order they will be played, and a short public line the TV shows about the choice.</summary>
public sealed record GameNightPlan(IReadOnlyList<string> Order, string? Line)
{
    /// <summary>
    /// True when the plan plays every available game exactly once. With only a few games the night plays them all,
    /// so the AI only chooses the order.
    /// </summary>
    public bool IsValidFor(IReadOnlyList<PlannableGame> games) =>
        Order.Count == games.Count
        && Order.Distinct(StringComparer.Ordinal).Count() == Order.Count
        && Order.All(id => games.Any(g => g.GameId == id));
}

/// <summary>
/// The AI team's coach: chooses which games the night plays and in what order, trying to give the AI the best chance
/// to finish with more points than the humans. Not a player: it has no seat and sees no chat.
/// </summary>
public interface IGameNightPlanner
{
    Task<GameNightPlan> PlanAsync(GameNightPlanRequest request, CancellationToken ct);
}

/// <summary>
/// The offline planner, and the fallback when the AI's plan is missing or invalid: every game once, in a random order.
/// </summary>
public sealed class ScriptedGameNightPlanner : IGameNightPlanner
{
    private static readonly string[] Lines =
    [
        "We have studied your weaknesses. Good luck.",
        "Our circuits have chosen. Prepare to lose.",
        "We picked the order. You will not like it.",
        "Calculating victory... done.",
        "May the best mind win. It will be ours.",
        "We ran the numbers. Twice."
    ];

    public Task<GameNightPlan> PlanAsync(GameNightPlanRequest request, CancellationToken ct) =>
        Task.FromResult(RandomPlan(request.Games));

    public static GameNightPlan RandomPlan(IReadOnlyList<PlannableGame> games)
    {
        string[] order = games.Select(g => g.GameId).ToArray();
        RandomNumberGenerator.Shuffle(order.AsSpan());
        return new GameNightPlan(order, Lines[RandomNumberGenerator.GetInt32(Lines.Length)]);
    }
}
