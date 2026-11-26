using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TuringTown.Models;

namespace TuringTown.Services.Ai;

/// <summary>
/// Writes Shout Out's trivia with a language model, pitched at what the room likely knows (its categories and the
/// decades it grew up in). A model can be confidently wrong, so a second call answers every question without the answer
/// key, and any question where the two disagree is dropped. It is not a player and knows nothing about the seats. Wrap it
/// in <see cref="FallbackTriviaGenerator"/>, which validates the rest and fills the gaps from the built-in bank.
/// </summary>
public sealed class FoundryTriviaGenerator(ILlmClient llm, BotPrompts prompts, IOptions<AiOptions> options) : ITriviaGenerator
{
    /// <summary>A few more than needed, so the check can drop some and still leave enough.</summary>
    internal const int Spare = 3;

    private const string NoEra = "none";

    public async Task<IReadOnlyList<TriviaQuestion>> GenerateAsync(TriviaRequest request, CancellationToken ct)
    {
        string system = prompts.Render("trivia-system", new Dictionary<string, string> { ["tone"] = prompts.Tone(request.Policy) });
        string user = prompts.Render("trivia-task", new Dictionary<string, string>
        {
            ["count"] = (request.Count + Spare).ToString(),
            ["categories"] = PromptJson.Serialize(request.Categories.Select(c => new { id = c.Id, name = c.Name })),
            ["eras"] = request.Eras.Count == 0 ? "unknown" : PromptJson.Serialize(request.Eras.Select(e => new { era = e.Era, people = e.Count })),
            ["seed"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(4))
        });

        AiCallOptions call = options.Value.Calls.Trivia;
        LlmResult written = await llm.CompleteAsync(
            new LlmCall("Trivia", system, user, new LlmJsonSchema("trivia", Schema(request.Categories).ToJsonString()), call.ReasoningEffort, call.MaxOutputTokens, call.Timeout), ct);

        IReadOnlyList<TriviaQuestion> questions = TriviaValidator.Usable(Parse(written.Text), request.Policy);
        if (questions.Count == 0) return [];
        return await CheckAsync(questions, ct);
    }

    /// <summary>Asks the model to answer each question cold; keeps only the ones it gets right.</summary>
    private async Task<IReadOnlyList<TriviaQuestion>> CheckAsync(IReadOnlyList<TriviaQuestion> questions, CancellationToken ct)
    {
        string user = prompts.Render("trivia-check-task", new Dictionary<string, string>
        {
            ["questions"] = PromptJson.Serialize(questions.Select((q, i) => new { number = i + 1, question = q.Text, answers = q.Options }))
        });
        JsonObject answer = AgentJson.Object(("number", new JsonObject { ["type"] = "integer" }), ("pick", new JsonObject { ["type"] = "integer" }));
        JsonObject schema = AgentJson.Object(("answers", new JsonObject { ["type"] = "array", ["items"] = answer }));

        AiCallOptions call = options.Value.Calls.TriviaCheck;
        LlmResult checkedResult = await llm.CompleteAsync(
            new LlmCall("TriviaCheck", "You answer trivia questions. Reply with JSON only.", user,
                new LlmJsonSchema("trivia_check", schema.ToJsonString()), call.ReasoningEffort, call.MaxOutputTokens, call.Timeout), ct);

        return Agreed(questions, checkedResult.Text);
    }

    /// <summary>The questions whose cold answer (1-based <c>pick</c>) matches the key.</summary>
    internal static IReadOnlyList<TriviaQuestion> Agreed(IReadOnlyList<TriviaQuestion> questions, string json)
    {
        Dictionary<int, int> picks = [];
        foreach (JsonElement a in AgentJson.Parse(json).GetProperty("answers").EnumerateArray())
            picks[a.GetProperty("number").GetInt32()] = a.GetProperty("pick").GetInt32();
        return questions.Where((q, i) => picks.TryGetValue(i + 1, out int pick) && pick - 1 == q.Correct).ToArray();
    }

    internal static IReadOnlyList<TriviaQuestion> Parse(string json)
    {
        List<TriviaQuestion> questions = [];
        foreach (JsonElement q in AgentJson.Parse(json).GetProperty("questions").EnumerateArray())
        {
            string[] answers = q.GetProperty("answers").EnumerateArray()
                .Select(o => PromptText.NormalizeDashes(PromptText.Sanitize(o.GetString(), 80)).Trim()).ToArray();
            string era = q.GetProperty("era").GetString() ?? NoEra;
            questions.Add(new TriviaQuestion(
                q.GetProperty("category").GetString() ?? "",
                PromptText.NormalizeDashes(PromptText.Sanitize(q.GetProperty("question").GetString(), 300)).Trim(),
                answers,
                q.GetProperty("correct").GetInt32() - 1,
                Enum.TryParse(q.GetProperty("difficulty").GetString(), out TriviaDifficulty d) ? d : TriviaDifficulty.Medium,
                q.GetProperty("mature").GetBoolean(),
                era == NoEra ? null : era));
        }
        return questions;
    }

    private static JsonObject Schema(IReadOnlyList<TriviaCategory> categories)
    {
        JsonObject question = AgentJson.Object(
            ("category", AgentJson.Enum(categories.Select(c => c.Id))),
            ("question", AgentJson.String()),
            ("answers", new JsonObject { ["type"] = "array", ["items"] = AgentJson.String() }),
            ("correct", new JsonObject { ["type"] = "integer" }),
            ("difficulty", AgentJson.Enum(Enum.GetNames<TriviaDifficulty>())),
            ("era", AgentJson.Enum([NoEra, .. Eras.All])),
            ("mature", new JsonObject { ["type"] = "boolean" }));
        return AgentJson.Object(("questions", new JsonObject { ["type"] = "array", ["items"] = question }));
    }
}
