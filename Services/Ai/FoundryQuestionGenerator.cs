using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TuringTown.Models;

namespace TuringTown.Services.Ai;

/// <summary>
/// Writes the onboarding form with a language model. It is not a player: its prompt has no bot framing and no game context,
/// only the tone, the mix of question kinds and a variety seed. The server assigns every id, and the engine validates the
/// result (<see cref="QuestionValidator"/>) and uses the built-in questions if it is not valid. Wrap it in
/// <see cref="FallbackQuestionGenerator"/>.
/// </summary>
public sealed class FoundryQuestionGenerator(ILlmClient llm, BotPrompts prompts, IOptions<AiOptions> options) : IQuestionGenerator
{
    public async Task<IReadOnlyList<OnboardingQuestion>> GenerateAsync(QuestionRequest request, CancellationToken ct)
    {
        string system = prompts.Render("questions-system", new Dictionary<string, string> { ["tone"] = prompts.Tone(request.Policy) });
        string user = prompts.Render("questions-task", new Dictionary<string, string>
        {
            ["text"] = request.Mix.Text.ToString(),
            ["single"] = request.Mix.SingleChoice.ToString(),
            ["multi"] = request.Mix.MultiChoice.ToString(),
            ["tone_requirement"] = !request.Policy.FamilyFriendly && request.Mix.Total > 0
                ? "Include at least one cheeky question marked mature, while following the safety rules."
                : "Every question and option must be suitable for the requested tone.",
            ["seed"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(4))
        });

        AiCallOptions call = options.Value.Calls.Questions;
        LlmResult result = await llm.CompleteAsync(
            new LlmCall("Questions", system, user, Schema(), call.ReasoningEffort, call.MaxOutputTokens, call.Timeout), ct);

        return Parse(result.Text);
    }

    /// <summary>
    /// Builds the questions from the model's JSON. Ids are assigned here (q1.., a..), never taken from the model, and the
    /// selection limits are set from the kind. Anything that does not fit is left for the validator to reject.
    /// </summary>
    internal static IReadOnlyList<OnboardingQuestion> Parse(string json)
    {
        JsonElement root;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The model did not return valid JSON.", ex);
        }

        List<OnboardingQuestion> questions = [];
        int number = 0;
        foreach (JsonElement q in root.GetProperty("questions").EnumerateArray())
        {
            number++;
            QuestionKind kind = Enum.TryParse(q.GetProperty("kind").GetString(), out QuestionKind k) ? k : throw new InvalidOperationException("Unknown question kind.");
            string text = PromptText.NormalizeDashes(PromptText.Sanitize(q.GetProperty("text").GetString(), 300));
            bool mature = q.GetProperty("mature").GetBoolean();

            QuestionOption[] options = kind == QuestionKind.Text
                ? []
                : q.GetProperty("options").EnumerateArray()
                    .Select((o, i) => new QuestionOption(((char)('a' + i)).ToString(), PromptText.NormalizeDashes(PromptText.Sanitize(o.GetProperty("text").GetString(), 100)), o.GetProperty("mature").GetBoolean()))
                    .ToArray();

            int max = kind switch
            {
                QuestionKind.Text => 0,
                QuestionKind.SingleChoice => 1,
                _ => q.GetProperty("maxSelect").GetInt32()
            };
            questions.Add(new OnboardingQuestion($"q{number}", kind, text, mature, options, kind == QuestionKind.Text ? 0 : 1, max));
        }

        return questions;
    }

    private static LlmJsonSchema Schema()
    {
        JsonObject option = new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" }, ["mature"] = new JsonObject { ["type"] = "boolean" } },
            ["required"] = new JsonArray("text", "mature"),
            ["additionalProperties"] = false
        };

        JsonObject question = new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("Text", "SingleChoice", "MultiChoice") },
                ["text"] = new JsonObject { ["type"] = "string" },
                ["mature"] = new JsonObject { ["type"] = "boolean" },
                ["options"] = new JsonObject { ["type"] = "array", ["items"] = option },
                ["maxSelect"] = new JsonObject { ["type"] = "integer" }
            },
            ["required"] = new JsonArray("kind", "text", "mature", "options", "maxSelect"),
            ["additionalProperties"] = false
        };

        JsonObject root = new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["questions"] = new JsonObject { ["type"] = "array", ["items"] = question } },
            ["required"] = new JsonArray("questions"),
            ["additionalProperties"] = false
        };

        return new LlmJsonSchema("onboarding_questions", root.ToJsonString());
    }
}
