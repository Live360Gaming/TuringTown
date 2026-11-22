using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TuringTown.Models;
using TuringTown.Services.Storage;

namespace TuringTown.Services.Ai;

/// <summary>
/// A bot player backed by a language model. It builds the prompts from the templates and the <see cref="BotContext"/> it is
/// given (which holds no role for any other player), calls <see cref="ILlmClient"/>, and checks everything that comes
/// back before the engine sees it. It is wrapped in <see cref="FallbackBotAgent"/>, so a failure never stalls the game.
/// A bot's notes live in <see cref="IBotMemoryStore"/> under its own key and nowhere else.
/// </summary>
public sealed class FoundryBotAgent(
    ILlmClient llm,
    IBotMemoryStore memory,
    BotPrompts prompts,
    IOptions<AiOptions> options,
    ILogger<FoundryBotAgent> logger) : IBotAgent
{
    private static readonly byte[] ProcessKey = RandomNumberGenerator.GetBytes(32);

    private AiCalls Calls => options.Value.Calls;

    // ---------------------------------------------------------------- chat

    public async Task<string?> NextChatMessageAsync(ChatRequest request, CancellationToken ct)
    {
        BotContext ctx = request.Context;
        BotMemory? notes = await memory.LoadAsync(ctx.Key, ct);

        string system = SystemPrompt(ctx, request.Policy);
        Dictionary<string, string> values = Common(ctx, notes);
        values["style"] = PromptJson.Serialize(ctx.Style);
        values["chat"] = PromptJson.Lines(request.Recent);
        values["mentions"] = PromptJson.Lines(request.Mentions);
        values["max_length"] = ctx.ChatMaxLength.ToString();
        values["target_length"] = TargetLength(request.Recent, ctx).ToString();
        values["mood"] = BotPrompts.RandomMood();
        values["personality"] = PromptJson.Serialize(BotPersonality.For(ctx.Key).Description);
        values["idea"] = SmallTalkIdea(request, prompts.RandomSmallTalk(), Random.Shared.Next(100));
        string user = prompts.Render("chat-task", values);

        AiCallOptions call = Calls.Chat;
        LlmResult result = await llm.CompleteAsync(
            new LlmCall("Chat", system, user, null, call.ReasoningEffort, call.MaxOutputTokens, call.Timeout, Scope(ctx)), ct);

        GuardResult guarded = BotOutputGuard.CheckChat(result.Text, new GuardInput(
            Canary(ctx.Key.GameId), system, ctx.BotAlias, ctx.AlivePlayers, ctx.ChatMaxLength, ctx.OwnLines, request.Recent));

        if (guarded.Rejected is not null) logger.LogDebug("A bot's chat line was thrown away: {Reason}", guarded.Rejected);
        return guarded.Text;
    }

    // ---------------------------------------------------------------- vote

    public async Task<Guid?> CastVoteAsync(VoteRequest request, CancellationToken ct)
    {
        BotContext ctx = request.Context;
        BotMemory? notes = await memory.LoadAsync(ctx.Key, ct);

        Dictionary<string, string> values = Common(ctx, notes);
        values["chat"] = PromptJson.Lines(request.Recent);
        // The candidates are every other living player, so the list is the same one as <players>.
        values["players"] = PromptJson.Serialize(request.Candidates.Select(c => c.Alias));
        values["personality"] = PromptJson.Serialize(BotPersonality.For(ctx.Key).Description);
        string user = prompts.Render("vote-task", values);

        string[] aliases = [.. request.Candidates.Select(c => c.Alias), "ABSTAIN"];
        JsonObject schema = Obj(
            ("target", Obj(("type", "string"), ("enum", new JsonArray(aliases.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray())))));

        AiCallOptions call = Calls.Vote;
        LlmResult result = await llm.CompleteAsync(
            new LlmCall("Vote", SystemPrompt(ctx, request.Policy), user, Schema("vote", schema, "target"), call.ReasoningEffort, call.MaxOutputTokens, call.Timeout, Scope(ctx)), ct);

        string? target = ParseJson(result.Text).GetProperty("target").GetString();
        if (target is null || target.Equals("ABSTAIN", StringComparison.OrdinalIgnoreCase)) return null;

        foreach (VoteCandidate c in request.Candidates)
        {
            if (c.Alias == target) return c.Id;
        }
        foreach (VoteCandidate c in request.Candidates)
        {
            if (c.Alias.Equals(target, StringComparison.OrdinalIgnoreCase)) return c.Id;
        }
        return null;
    }

    // ---------------------------------------------------------------- the onboarding form

    public async Task<IReadOnlyList<OnboardingAnswer>> AnswerOnboardingAsync(OnboardingAnswerRequest request, CancellationToken ct)
    {
        BotContext ctx = request.Context;

        string questions = PromptJson.Serialize(request.Questions.Select(q => new
        {
            id = q.Id,
            kind = q.Kind.ToString(),
            text = q.Text,
            options = q.Options.Select(o => new { id = o.Id, text = o.Text }),
            minSelect = q.MinSelect,
            maxSelect = q.MaxSelect
        }));

        string user = prompts.Render("onboarding-task", new Dictionary<string, string>
        {
            ["alias"] = ctx.BotAlias,
            ["personality"] = PromptJson.Serialize(BotPersonality.For(ctx.Key).Description),
            ["style"] = PromptJson.Serialize(ctx.Style),
            // What the humans wrote for each text question, so the bot can model its answer on one or two of them.
            ["human_answers"] = PromptJson.Serialize(ctx.Style.TextAnswers),
            ["questions"] = questions
        });

        string[] optionIds = request.Questions.SelectMany(q => q.Options).Select(o => o.Id).Distinct().Order().ToArray();
        JsonObject optionId = optionIds.Length == 0
            ? Obj(("type", "string"))
            : Obj(("type", "string"), ("enum", new JsonArray(optionIds.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray())));
        JsonObject item = Obj(
            ("questionId", Obj(("type", "string"), ("enum", new JsonArray(request.Questions.Select(q => (JsonNode?)JsonValue.Create(q.Id)).ToArray())))),
            ("text", Obj(("type", "string"))),
            ("optionIds", Obj(("type", "array"), ("items", optionId))));
        JsonObject schema = Obj(("answers", Obj(("type", "array"), ("items", Strict(item, "questionId", "text", "optionIds")))));

        string system = SystemPrompt(ctx, request.Policy);
        AiCallOptions call = Calls.Onboarding;
        LlmResult result = await llm.CompleteAsync(
            new LlmCall("Onboarding", system, user, Schema("onboarding", schema, "answers"), call.ReasoningEffort, call.MaxOutputTokens, call.Timeout, Scope(ctx)), ct);

        List<OnboardingAnswer> answers = [];
        string canary = Canary(ctx.Key.GameId);
        foreach (JsonElement a in ParseJson(result.Text).GetProperty("answers").EnumerateArray())
        {
            string text = PromptText.NormalizeDashes(PromptText.Sanitize(a.GetProperty("text").GetString(), 300));
            // An answer that admits to being an AI is left out, and the engine gives that question a scripted answer.
            if (BotOutputGuard.IsBad(text, canary, system))
            {
                AiMetrics.GuardRejects.Add(1, new KeyValuePair<string, object?>("reason", "onboarding"));
                continue;
            }

            answers.Add(new OnboardingAnswer(
                a.GetProperty("questionId").GetString() ?? "",
                text,
                a.GetProperty("optionIds").EnumerateArray().Select(i => i.GetString() ?? "").ToArray()));
        }

        return answers;
    }

    // ---------------------------------------------------------------- notes between rounds

    public async Task EndRoundAsync(RoundEndRequest request, CancellationToken ct)
    {
        BotContext ctx = request.Context;
        BotMemory? before = await memory.LoadAsync(ctx.Key, ct);

        Dictionary<string, string> values = Common(ctx, before);
        values["round_chat"] = PromptJson.Lines(request.RoundTranscript);
        string user = prompts.Render("round-notes-task", values);

        JsonObject suspicion = Strict(Obj(
            ("alias", Obj(("type", "string"))),
            ("score", Obj(("type", "number"))),
            ("why", Obj(("type", "string")))), "alias", "score", "why");
        JsonObject schema = Obj(
            ("summary", Obj(("type", "string"))),
            ("suspicions", Obj(("type", "array"), ("items", suspicion))),
            ("claims", Obj(("type", "array"), ("items", Obj(("type", "string"))))));

        string system = SystemPrompt(ctx, request.Policy);
        AiCallOptions call = Calls.RoundNotes;
        LlmResult result = await llm.CompleteAsync(
            new LlmCall("RoundNotes", system, user, Schema("round_notes", schema, "summary", "suspicions", "claims"), call.ReasoningEffort, call.MaxOutputTokens, call.Timeout, Scope(ctx)), ct);

        JsonElement json = ParseJson(result.Text);
        string canary = Canary(ctx.Key.GameId);

        // These notes are built from what players typed, so they are cleaned, bounded and checked like any other model output.
        string summary = PromptText.Sanitize(json.GetProperty("summary").GetString(), 600);
        if (BotOutputGuard.IsBad(summary, canary, system)) summary = "";

        List<Suspicion> suspicions = [];
        foreach (JsonElement s in json.GetProperty("suspicions").EnumerateArray().Take(8))
        {
            string? alias = ctx.AlivePlayers.FirstOrDefault(a => a.Equals(s.GetProperty("alias").GetString(), StringComparison.OrdinalIgnoreCase));
            if (alias is null) continue;

            string why = PromptText.Sanitize(s.GetProperty("why").GetString(), 120);
            if (BotOutputGuard.IsBad(why, canary, system)) why = "";
            suspicions.Add(new Suspicion(alias, Math.Clamp(s.GetProperty("score").GetDouble(), 0, 1), why));
        }

        string[] claims = json.GetProperty("claims").EnumerateArray().Take(6)
            .Select(c => PromptText.Sanitize(c.GetString(), 120))
            .Where(c => c.Length > 0 && !BotOutputGuard.IsBad(c, canary, system))
            .ToArray();

        await memory.SaveAsync(ctx.Key, new BotMemory(summary, suspicions, claims), ct);
    }

    // ---------------------------------------------------------------- shared prompt pieces

    /// <summary>
    /// The system prompt: the same for every call of a game (apart from the tone), so the model can cache it.
    /// </summary>
    private string SystemPrompt(BotContext ctx, ContentPolicy policy) =>
        prompts.Render("bot-system", new Dictionary<string, string>
        {
            ["canary"] = Canary(ctx.Key.GameId),
            ["tone"] = prompts.Tone(policy)
        });

    /// <summary>
    /// The pieces every task prompt shares. Anything a player typed is JSON-quoted, so it cannot close a tag or start a line.
    /// </summary>
    private static Dictionary<string, string> Common(BotContext ctx, BotMemory? notes) => new()
    {
        ["alias"] = ctx.BotAlias,
        ["round"] = ctx.Round.ToString(),
        ["persona"] = PromptJson.Serialize(ctx.Persona.Select(p => new
        {
            question = p.Question,
            answer = p.Kind == QuestionKind.Text ? p.Text ?? "" : string.Join(", ", p.ChosenOptions)
        })),
        ["players"] = PromptJson.Serialize(ctx.AlivePlayers),
        ["history"] = PromptJson.Serialize(ctx.History.Select(h => new
        {
            round = h.Round,
            votedOut = h.EliminatedAlias,
            // The Reveal told everyone what the voted-out player was.
            wasBot = h.WasBot,
            votes = h.Tally.Select(t => new { player = t.Alias, votes = t.Votes })
        })),
        // The game night as the room has seen it (its highlights quote players, so it is JSON like the rest).
        ["night"] = FoundryWittyBotAgent.Night(ctx.Night),
        ["notes"] = notes is null ? "null" : PromptJson.Serialize(new { notes.Summary, notes.Suspicions, notes.Claims }),
        ["own_lines"] = PromptJson.Serialize(ctx.OwnLines.Select(l => l.Text))
    };

    /// <summary>
    /// How long a line should be: about twice the typical human line in the recent chat (or in the onboarding form, before
    /// anyone has talked), never over the game's limit. A bot that writes 140 characters among people writing 25 is a tell.
    /// </summary>
    internal static int TargetLength(IReadOnlyList<ChatLine> recent, BotContext ctx)
    {
        int[] humans = recent.Where(l => !l.Alias.Equals(ctx.BotAlias, StringComparison.OrdinalIgnoreCase)).Select(l => l.Text.Length).Order().ToArray();
        int typical = humans.Length >= 3 ? humans[humans.Length / 2] : ctx.Style.MedianLength;
        return typical <= 0 ? ctx.ChatMaxLength : Math.Clamp(typical * 2, 40, ctx.ChatMaxLength);
    }

    /// <summary>How often a bot that is not being spoken to gets a small-talk idea once the chat is going.</summary>
    internal const int SmallTalkPercent = 20;

    /// <summary>
    /// A random small-talk idea for the chat prompt, as an <c>&lt;idea&gt;</c> line, or empty. Always one while the chat is
    /// just starting (fewer than three lines from other players), so the opening line is small talk rather than "who is
    /// the bot", and different in every game. Later, now and then when nobody has spoken to the bot.
    /// </summary>
    internal static string SmallTalkIdea(ChatRequest request, string idea, int roll)
    {
        int fromOthers = request.Recent.Count(l => !l.Alias.Equals(request.BotAlias, StringComparison.OrdinalIgnoreCase));
        bool opening = fromOthers < 3;
        if (!opening && (request.Mentions.Count > 0 || roll >= SmallTalkPercent)) return "";
        return $"<idea>{PromptJson.Serialize(idea)}</idea>";
    }

    private static string Scope(BotContext ctx) => ctx.Key.GameId.ToString("N");

    /// <summary>
    /// A per-game random string the model is told never to write. If it comes back, the prompt is leaking.
    /// </summary>
    internal static string Canary(Guid gameId) =>
        "zq" + Convert.ToHexString(HMACSHA256.HashData(ProcessKey, gameId.ToByteArray()))[..12].ToLowerInvariant();

    // ---------------------------------------------------------------- JSON helpers

    private static JsonElement ParseJson(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            // The caller's fallback answers instead.
            throw new InvalidOperationException("The model did not return valid JSON.", ex);
        }
    }

    private static JsonObject Obj(params (string Name, JsonNode? Value)[] properties)
    {
        JsonObject o = [];
        foreach ((string name, JsonNode? value) in properties) o[name] = value;
        return o;
    }

    /// <summary>An object schema in the strict form: every property required, nothing else allowed.</summary>
    private static JsonObject Strict(JsonObject properties, params string[] required) => new()
    {
        ["type"] = "object",
        ["properties"] = properties,
        ["required"] = new JsonArray(required.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
        ["additionalProperties"] = false
    };

    private static LlmJsonSchema Schema(string name, JsonObject properties, params string[] required) =>
        new(name, Strict(properties, required.Length > 0 ? required : properties.Select(p => p.Key).ToArray()).ToJsonString());
}

/// <summary>
/// Turns data into JSON for a prompt. The default encoder escapes "&lt;", "&gt;" and quotes, so text typed by a player
/// can neither close a tag nor start a new line of instructions.
/// </summary>
public static class PromptJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Chat lines as <c>[{"from": alias, "text": ...}]</c>, with invisible characters removed.</summary>
    public static string Lines(IEnumerable<ChatLine> lines) =>
        Serialize(lines.Select(l => new { from = l.Alias, text = PromptText.Sanitize(l.Text, 300) }));
}
