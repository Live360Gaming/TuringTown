namespace TuringTown.Services.Ai;

/// <summary>A JSON Schema a reply must follow. Sent as a strict structured-output format.</summary>
public sealed record LlmJsonSchema(string Name, string Json);

/// <summary>
/// One call to a model. Nothing here is specific to a game: the system and user text come from the caller's prompt
/// templates. <paramref name="Scope"/> groups calls for limits (a game id, opaque to the client).
/// </summary>
/// <param name="Kind">What the call is for ("Chat", "Vote", ...). Used for metrics and per-kind settings only.</param>
/// <param name="Schema">When set, the reply must be JSON matching it. When null the reply is plain text.</param>
/// <param name="ReasoningEffort">Sent as <c>reasoning_effort</c>. Empty sends nothing.</param>
public sealed record LlmCall(
    string Kind,
    string System,
    string User,
    LlmJsonSchema? Schema,
    string ReasoningEffort,
    int MaxOutputTokens,
    TimeSpan Timeout,
    string? Scope = null);

public sealed record LlmResult(string Text, int InputTokens, int CachedTokens, int OutputTokens, int ReasoningTokens);

/// <summary>
/// The only door to a language model. The real implementation talks to Azure AI Foundry; tests use a fake. Any mini game's
/// agent can use it.
/// </summary>
public interface ILlmClient
{
    Task<LlmResult> CompleteAsync(LlmCall call, CancellationToken ct);
}

/// <summary>A call was refused locally (a budget, a cap or the circuit breaker), without reaching the model.</summary>
public sealed class AiUnavailableException(string reason) : Exception($"The AI is not available: {reason}.")
{
    public string Reason { get; } = reason;
}

/// <summary>The model's own safety filter stopped the request or the reply.</summary>
public sealed class LlmBlockedException(string message) : Exception(message);
