using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace TuringTown.Services.Ai;

/// <summary>JSON helpers shared by the game-night agents.</summary>
internal static class AgentJson
{
    public static JsonElement Parse(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The model did not return valid JSON.", ex);
        }
    }

    public static JsonObject Object(params (string Name, JsonNode Schema)[] properties) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(properties.Select(p => KeyValuePair.Create(p.Name, (JsonNode?)p.Schema))),
        ["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Name)).ToArray()),
        ["additionalProperties"] = false
    };

    public static JsonObject String() => new() { ["type"] = "string" };

    public static JsonObject Enum(IEnumerable<string> values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
}

/// <summary>
/// The AI team's coach on Azure AI Foundry: picks the order of the night's games and a line for the TV. The night
/// checks the order (every game once) and falls back to a random one, so a bad reply never stops a night.
/// </summary>
public sealed class FoundryGameNightPlanner(ILlmClient llm, BotPrompts prompts, IOptions<AiOptions> options) : IGameNightPlanner
{
    public async Task<GameNightPlan> PlanAsync(GameNightPlanRequest request, CancellationToken ct)
    {
        string system = prompts.Render("planner-system", new Dictionary<string, string> { ["tone"] = prompts.Tone(request.Policy) });
        string user = prompts.Render("planner-task", new Dictionary<string, string>
        {
            ["games"] = PromptJson.Serialize(request.Games.Select(g => new { id = g.GameId, title = g.Title, description = g.Description })),
            ["players"] = PromptJson.Serialize(request.Players.Select(p => PromptText.Sanitize(p, 40))),
            ["bot_count"] = request.BotCount.ToString()
        });

        string[] ids = request.Games.Select(g => g.GameId).ToArray();
        JsonObject schema = AgentJson.Object(("order", new JsonObject { ["type"] = "array", ["items"] = AgentJson.Enum(ids) }), ("line", AgentJson.String()));

        AiCallOptions call = options.Value.Calls.Plan;
        LlmResult result = await llm.CompleteAsync(
            new LlmCall("Plan", system, user, new LlmJsonSchema("night_plan", schema.ToJsonString()), call.ReasoningEffort, call.MaxOutputTokens, call.Timeout), ct);

        JsonElement root = AgentJson.Parse(result.Text);
        string[] order = root.GetProperty("order").EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
        string? line = root.GetProperty("line").GetString();
        if (line is not null && BotOutputGuard.IsBad(line, "", system)) line = null;
        return new GameNightPlan(order, line);
    }
}

/// <summary>
/// Writes Witty Bits prompts with a language model. Not a player: no persona and no game context, only the tone, the
/// count and a variety seed. The engine validates the result (<see cref="WittyPromptValidator"/>) and uses the
/// built-in prompts if it is not valid. Wrap it in <see cref="FallbackWittyPromptGenerator"/>.
/// </summary>
public sealed class FoundryWittyPromptGenerator(ILlmClient llm, BotPrompts prompts, IOptions<AiOptions> options) : IWittyPromptGenerator
{
    public async Task<IReadOnlyList<WittyPrompt>> GenerateAsync(WittyPromptRequest request, CancellationToken ct)
    {
        string system = prompts.Render("witty-prompts-system", new Dictionary<string, string> { ["tone"] = prompts.Tone(request.Policy) });
        string user = prompts.Render("witty-prompts-task", new Dictionary<string, string>
        {
            ["count"] = request.Count.ToString(),
            ["tone_requirement"] = request.Policy.FamilyFriendly
                ? "Every prompt must be suitable for the requested tone."
                : "Include a few cheeky prompts marked mature, while following the safety rules.",
            ["seed"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(4))
        });

        JsonObject prompt = AgentJson.Object(("text", AgentJson.String()), ("mature", new JsonObject { ["type"] = "boolean" }));
        JsonObject schema = AgentJson.Object(("prompts", new JsonObject { ["type"] = "array", ["items"] = prompt }));

        AiCallOptions call = options.Value.Calls.WittyPrompts;
        LlmResult result = await llm.CompleteAsync(
            new LlmCall("WittyPrompts", system, user, new LlmJsonSchema("witty_prompts", schema.ToJsonString()), call.ReasoningEffort, call.MaxOutputTokens, call.Timeout), ct);

        return Parse(result.Text);
    }

    internal static IReadOnlyList<WittyPrompt> Parse(string json) =>
        AgentJson.Parse(json).GetProperty("prompts").EnumerateArray()
            .Select(p => new WittyPrompt(PromptText.NormalizeDashes(PromptText.Sanitize(p.GetProperty("text").GetString(), 200)).Trim(), p.GetProperty("mature").GetBoolean()))
            .ToArray();
}

/// <summary>
/// A Witty Bits bot on Azure AI Foundry. It writes in the room's sense of humour (from the night's highlights) and
/// votes for what the room will laugh at. Its answers pass the same checks as a human's, plus the leak guard. Wrap it
/// in <see cref="FallbackWittyBotAgent"/>.
/// </summary>
public sealed class FoundryWittyBotAgent(ILlmClient llm, BotPrompts prompts, IOptions<AiOptions> options) : IWittyBotAgent
{
    public async Task<string?> WriteAnswerAsync(WittyAnswerRequest request, CancellationToken ct)
    {
        string system = System(request.Key, request.Policy);
        string user = prompts.Render("witty-answer-task", new Dictionary<string, string>
        {
            ["alias"] = request.Alias,
            ["night"] = Night(request.Night),
            ["taken"] = PromptJson.Serialize(request.Taken.TakeLast(30).Select(t => PromptText.Sanitize(t, 100))),
            ["prompt"] = PromptJson.Serialize(request.Prompt),
            ["max_length"] = request.MaxLength.ToString()
        });

        AiCallOptions call = options.Value.Calls.WittyAnswer;
        LlmResult result = await llm.CompleteAsync(new LlmCall("WittyAnswer", system, user,
            new LlmJsonSchema("witty_answer", AgentJson.Object(("answer", AgentJson.String())).ToJsonString()),
            call.ReasoningEffort, call.MaxOutputTokens, call.Timeout, request.Key.GameId.ToString("N")), ct);

        string? answer = AgentJson.Parse(result.Text).GetProperty("answer").GetString();
        if (string.IsNullOrWhiteSpace(answer)) return null;

        answer = PromptText.NormalizeDashes(answer).Trim().Trim('"', '\'').Trim();
        return BotOutputGuard.IsBad(answer, FoundryBotAgent.Canary(request.Key.GameId), system) ? null : answer;
    }

    public async Task<string?> VoteAsync(WittyVoteRequest request, CancellationToken ct)
    {
        string[] ids = request.Answers.Where(a => !a.Missing).Select(a => a.Id).ToArray();
        if (ids.Length == 0) return null;

        string system = System(request.Key, request.Policy);
        string user = prompts.Render("witty-vote-task", new Dictionary<string, string>
        {
            ["alias"] = request.Alias,
            ["night"] = Night(request.Night),
            ["prompt"] = PromptJson.Serialize(request.Prompt),
            ["answers"] = PromptJson.Serialize(request.Answers.Where(a => !a.Missing).Select(a => new { id = a.Id, text = PromptText.Sanitize(a.Text, 100) }))
        });

        AiCallOptions call = options.Value.Calls.WittyVote;
        LlmResult result = await llm.CompleteAsync(new LlmCall("WittyVote", system, user,
            new LlmJsonSchema("witty_vote", AgentJson.Object(("choice", AgentJson.Enum(ids))).ToJsonString()),
            call.ReasoningEffort, call.MaxOutputTokens, call.Timeout, request.Key.GameId.ToString("N")), ct);

        string? choice = AgentJson.Parse(result.Text).GetProperty("choice").GetString();
        return ids.Contains(choice) ? choice : null;
    }

    private string System(BotKey key, Models.ContentPolicy policy) => prompts.Render("witty-system", new Dictionary<string, string>
    {
        ["canary"] = FoundryBotAgent.Canary(key.GameId),
        ["tone"] = prompts.Tone(policy)
    });

    /// <summary>The night as quoted JSON (its highlights are player text), or null outside a game night.</summary>
    internal static string Night(TeamContext? night) => night is null ? "null" : PromptJson.Serialize(night);
}
