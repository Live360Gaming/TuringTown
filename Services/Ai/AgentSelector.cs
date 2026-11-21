namespace TuringTown.Services.Ai;

/// <summary>
/// The agents one game runs on. <paramref name="Lease"/> (when set) must be disposed when the game ends.
/// </summary>
public sealed record AgentSet(IBotAgent Agent, IQuestionGenerator Questions, bool UsesAi, IDisposable? Lease = null);

/// <summary>
/// Picks the agents for each new game: the real AI while the limits allow, otherwise the offline scripted ones. A game
/// that starts on the scripted agents stays on them, so a budget running out never changes the bots mid-game.
/// </summary>
public sealed class AgentSelector(AgentSet scripted, AgentSet? ai, AiBudget budget)
{
    public AgentSet Select()
    {
        if (ai is not null && budget.TryReserveGame(out IDisposable? lease))
            return ai with { Lease = lease };

        return scripted;
    }
}
