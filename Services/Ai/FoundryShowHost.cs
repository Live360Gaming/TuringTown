using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TuringTown.Models;

namespace TuringTown.Services.Ai;

/// <summary>
/// The show host on Azure AI Foundry. It writes the run sheet and, from the room's public answers, the hot topic. The
/// engine validates both (<c>TuringTownEngine</c>), so a bad reply only means the built-in content. Wrap it in
/// <see cref="FallbackShowHost"/>.
/// </summary>
public sealed class FoundryShowHost(ILlmClient llm, BotPrompts prompts, IOptions<AiOptions> options) : IShowHost
{
    public async Task<RunSheet> PlanAsync(ShowRequest request, CancellationToken ct)
    {
        string system = SystemPrompt(request.Policy);
        string user = prompts.Render("show-task", new Dictionary<string, string>
        {
            ["beats"] = string.Join(", ", ShowBeats.All),
            ["seed"] = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4))
        });

        (string, JsonNode)[] lines = ShowBeats.All.Select(b => (b, (JsonNode)AgentJson.String())).ToArray();
        JsonObject schema = AgentJson.Object(
            ("questions", new JsonObject { ["type"] = "array", ["items"] = AgentJson.String() }),
            ("topic", AgentJson.String()),
            ("lines", AgentJson.Object(lines)));

        JsonElement root = AgentJson.Parse(await CallAsync("Show", system, user, "run_sheet", schema, options.Value.Calls.Show, ct));
        return new RunSheet(
            root.GetProperty("questions").EnumerateArray().Select(q => Clean(q.GetString(), 120)).ToArray(),
            Clean(root.GetProperty("topic").GetString(), 120),
            root.GetProperty("lines").EnumerateObject()
                .Select(p => (p.Name, Text: Clean(p.Value.GetString(), 140)))
                .Where(p => p.Text.Length > 0 && !BotOutputGuard.IsBad(p.Text, "", system))
                .ToDictionary(p => p.Name, p => p.Text));
    }

    public async Task<string?> TopicAsync(TopicRequest request, CancellationToken ct)
    {
        string system = SystemPrompt(request.Policy);
        string user = prompts.Render("show-topic-task", new Dictionary<string, string> { ["answers"] = PromptJson.Lines(request.Answers) });
        JsonObject schema = AgentJson.Object(("topic", AgentJson.String()));

        JsonElement root = AgentJson.Parse(await CallAsync("ShowTopic", system, user, "hot_topic", schema, options.Value.Calls.ShowTopic, ct));
        string topic = Clean(root.GetProperty("topic").GetString(), 120);
        return topic.Length == 0 || BotOutputGuard.IsBad(topic, "", system) ? null : topic;
    }

    private string SystemPrompt(ContentPolicy policy) =>
        prompts.Render("show-system", new Dictionary<string, string> { ["tone"] = prompts.Tone(policy) });

    private async Task<string> CallAsync(string kind, string system, string user, string name, JsonObject schema, AiCallOptions call, CancellationToken ct) =>
        (await llm.CompleteAsync(
            new LlmCall(kind, system, user, new LlmJsonSchema(name, schema.ToJsonString()), call.ReasoningEffort, call.MaxOutputTokens, call.Timeout), ct)).Text;

    private static string Clean(string? text, int max) =>
        PromptText.NormalizeDashes(PromptText.Sanitize(text?.ReplaceLineEndings(" "), max)).Trim();
}
