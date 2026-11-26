namespace TuringTown.Services.Ai;

/// <summary>
/// The agents one game night runs on: Turing Town's bot and question generator, and Witty Bits' bot and prompt
/// generator. <paramref name="Lease"/> (when set) must be disposed when the night ends. <paramref name="Planner"/> is the
/// AI's planner for the night; null uses the default one.
/// </summary>
public sealed record AgentSet(
    IBotAgent Agent,
    IQuestionGenerator Questions,
    IWittyBotAgent WittyAgent,
    IWittyPromptGenerator WittyPrompts,
    bool UsesAi,
    IDisposable? Lease = null,
    IGameNightPlanner? Planner = null,
    IShowHost? Host = null,
    ITriviaGenerator? Trivia = null);

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
