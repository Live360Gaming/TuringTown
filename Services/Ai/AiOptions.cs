using Microsoft.Extensions.Options;

namespace TuringTown.Services.Ai;

public enum AiProvider
{
    /// <summary>Offline canned bots and built-in questions. The default: no keys, no network.</summary>
    Scripted,

    /// <summary>Real bots and questions from Azure AI Foundry. Failures fall back to Scripted.</summary>
    Foundry
}

/// <summary>How one kind of model call behaves. All of it is configuration, so a different model needs no code change.</summary>
public sealed class AiCallOptions
{
    /// <summary>Passed to the model as <c>reasoning_effort</c> (for example minimal, low, medium). Empty sends nothing.</summary>
    public string ReasoningEffort { get; set; } = "low";

    /// <summary>Passed as <c>max_completion_tokens</c>. For a reasoning model this counts the hidden reasoning too.</summary>
    public int MaxOutputTokens { get; set; } = 300;

    public int TimeoutMs { get; set; } = 6000;

    public TimeSpan Timeout => TimeSpan.FromMilliseconds(TimeoutMs);
}

public sealed class AiCalls
{
    public AiCallOptions Chat { get; set; } = new() { ReasoningEffort = "low", MaxOutputTokens = 1000, TimeoutMs = 8000 };
    public AiCallOptions Vote { get; set; } = new() { ReasoningEffort = "low", MaxOutputTokens = 1000, TimeoutMs = 8000 };
    public AiCallOptions Onboarding { get; set; } = new() { ReasoningEffort = "low", MaxOutputTokens = 1500, TimeoutMs = 15000 };
    // Medium: the form is written with no context from earlier games, so the extra reasoning buys variety. It runs during
    // the tutorial, so the latency is hidden.
    public AiCallOptions Questions { get; set; } = new() { ReasoningEffort = "medium", MaxOutputTokens = 5000, TimeoutMs = 30000 };
    public AiCallOptions RoundNotes { get; set; } = new() { ReasoningEffort = "low", MaxOutputTokens = 1500, TimeoutMs = 15000 };

    // The game night: the AI's plan (during the Planning stage), Witty Bits prompts (during its tutorial), and a Witty
    // Bits bot's answer and vote (each inside a timed stage, with the scripted bot as the fallback).
    public AiCallOptions Plan { get; set; } = new() { ReasoningEffort = "low", MaxOutputTokens = 800, TimeoutMs = 12000 };
    public AiCallOptions WittyPrompts { get; set; } = new() { ReasoningEffort = "medium", MaxOutputTokens = 4000, TimeoutMs = 30000 };
    public AiCallOptions WittyAnswer { get; set; } = new() { ReasoningEffort = "low", MaxOutputTokens = 600, TimeoutMs = 12000 };
    public AiCallOptions WittyVote { get; set; } = new() { ReasoningEffort = "minimal", MaxOutputTokens = 300, TimeoutMs = 8000 };

    public IEnumerable<(string Name, AiCallOptions Options)> All =>
    [
        (nameof(Chat), Chat), (nameof(Vote), Vote), (nameof(Onboarding), Onboarding), (nameof(Questions), Questions), (nameof(RoundNotes), RoundNotes),
        (nameof(Plan), Plan), (nameof(WittyPrompts), WittyPrompts), (nameof(WittyAnswer), WittyAnswer), (nameof(WittyVote), WittyVote)
    ];
}

/// <summary>Cost and abuse limits. Over a limit a new game runs on the scripted bots, and calls during a game fall back.</summary>
public sealed class AiLimits
{
    public int MaxConcurrentCalls { get; set; } = 8;
    public int MaxCallsPerGame { get; set; } = 150;

    /// <summary>Tokens (input plus output) per UTC day, counted from the model's own usage. 5M is about $5 on a small model.</summary>
    public long DailyTokenBudget { get; set; } = 5_000_000;

    /// <summary>Games running on the real AI at once.</summary>
    public int MaxAiGames { get; set; } = 20;

    /// <summary>This many failed calls in a row open the circuit breaker...</summary>
    public int CircuitFailures { get; set; } = 5;

    /// <summary>...and no call is made for this long.</summary>
    public int CircuitOpenSeconds { get; set; } = 60;
}

/// <summary>
/// The tunables of the AI, from the "Ai" section of appsettings.json. Secrets are not here: the endpoint, deployment and
/// key come from <see cref="FoundryOptions"/> (user secrets in development, environment variables in production).
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    public AiProvider Provider { get; set; } = AiProvider.Scripted;

    /// <summary>The scope of the Entra ID token used when there is no API key (managed identity in production).</summary>
    public string TokenScope { get; set; } = "https://ai.azure.com/.default";

    public AiCalls Calls { get; set; } = new();
    public AiLimits Limits { get; set; } = new();
}

/// <summary>
/// Where the model lives: the "Foundry" section. These three keys already exist as .NET user secrets in development and
/// are the same names as environment variables (<c>Foundry__Endpoint</c>) in production. Never put them in appsettings.json,
/// and never log their values.
/// </summary>
public sealed class FoundryOptions
{
    public const string SectionName = "Foundry";

    /// <summary>The resource (https://name.openai.azure.com) or project endpoint. "/openai/v1/" is added if missing.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>The deployment name, which is what the API calls the model.</summary>
    public string Deployment { get; set; } = "";

    /// <summary>Development only. Empty means managed identity (DefaultAzureCredential), which is what production uses.</summary>
    public string ApiKey { get; set; } = "";
}

/// <summary>
/// Checks the AI settings when the app starts. Messages name the key that is wrong, never its value.
/// </summary>
public sealed class AiOptionsValidator(IOptions<FoundryOptions> foundry) : IValidateOptions<AiOptions>
{
    public ValidateOptionsResult Validate(string? name, AiOptions options)
    {
        List<string> errors = [];

        foreach ((string call, AiCallOptions o) in options.Calls.All)
        {
            if (o.MaxOutputTokens <= 0) errors.Add($"Ai:Calls:{call}:MaxOutputTokens must be greater than 0.");
            if (o.TimeoutMs <= 0) errors.Add($"Ai:Calls:{call}:TimeoutMs must be greater than 0.");
        }

        AiLimits l = options.Limits;
        if (l.MaxConcurrentCalls <= 0) errors.Add("Ai:Limits:MaxConcurrentCalls must be greater than 0.");
        if (l.MaxCallsPerGame <= 0) errors.Add("Ai:Limits:MaxCallsPerGame must be greater than 0.");
        if (l.DailyTokenBudget <= 0) errors.Add("Ai:Limits:DailyTokenBudget must be greater than 0.");
        if (l.MaxAiGames <= 0) errors.Add("Ai:Limits:MaxAiGames must be greater than 0.");
        if (l.CircuitFailures <= 0) errors.Add("Ai:Limits:CircuitFailures must be greater than 0.");

        if (options.Provider == AiProvider.Foundry)
        {
            FoundryOptions f = foundry.Value;
            if (!Uri.TryCreate(f.Endpoint, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
                errors.Add("Foundry:Endpoint is missing or is not an absolute https URL (set it in user secrets or as Foundry__Endpoint).");
            if (string.IsNullOrWhiteSpace(f.Deployment))
                errors.Add("Foundry:Deployment is missing (set it in user secrets or as Foundry__Deployment).");
            if (string.IsNullOrWhiteSpace(options.TokenScope))
                errors.Add("Ai:TokenScope is empty.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
