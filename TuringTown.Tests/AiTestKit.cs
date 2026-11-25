using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TuringTown.Models;
using TuringTown.Services.Ai;
using TuringTown.Services.Storage;

namespace TuringTown.Tests;

/// <summary>
/// A model that answers from a function, so CI never needs a real one. It records every call it is given.
/// </summary>
internal sealed class FakeLlmClient : ILlmClient
{
    public List<LlmCall> Calls { get; } = [];

    /// <summary>What the "model" says. The default is a valid, harmless reply for every kind of call.</summary>
    public Func<LlmCall, string> Reply { get; set; } = DefaultReply;

    public Task<LlmResult> CompleteAsync(LlmCall call, CancellationToken ct)
    {
        lock (Calls) Calls.Add(call);
        return Task.FromResult(new LlmResult(Reply(call), 100, 20, 30, 5));
    }

    public LlmCall[] Of(string kind)
    {
        lock (Calls)
            return Calls.Where(c => c.Kind == kind).ToArray();
    }

    public static string DefaultReply(LlmCall call) => call.Kind switch
    {
        "Chat" => "hmm not sure yet",
        "Vote" => $"{{\"ratings\":[{{\"alias\":\"{EnumAt(call.Schema!, "properties", "ratings", "items", "properties", "alias", "enum")[0]}\",\"score\":0.5,\"why\":\"quiet\"}}]}}",
        "Onboarding" => "{\"answers\":[]}",
        "RoundNotes" => "{\"summary\":\"a quiet round\",\"suspicions\":[],\"claims\":[]}",
        "Questions" => ValidQuestions,
        _ => throw new InvalidOperationException($"No default reply for {call.Kind}.")
    };

    public const string ValidQuestions = """
        {"questions":[
          {"kind":"Text","text":"What snack could you eat forever?","mature":false,"options":[],"maxSelect":0},
          {"kind":"SingleChoice","text":"Pick a weekend plan.","mature":false,"options":[
            {"text":"Hike","mature":false},{"text":"Nap","mature":false},{"text":"Games","mature":false},{"text":"Brunch","mature":false}],"maxSelect":1},
          {"kind":"MultiChoice","text":"Which toppings belong on a pizza?","mature":false,"options":[
            {"text":"Cheese","mature":false},{"text":"Mushrooms","mature":false},{"text":"Olives","mature":false},{"text":"Corn","mature":false}],"maxSelect":3}
        ]}
        """;

    /// <summary>Reads a list of strings out of a schema, for example <c>properties.target.enum</c>.</summary>
    public static string[] EnumAt(LlmJsonSchema schema, params string[] path)
    {
        using JsonDocument doc = JsonDocument.Parse(schema.Json);
        JsonElement element = doc.RootElement;
        foreach (string step in path) element = element.GetProperty(step);
        return element.EnumerateArray().Select(e => e.GetString()!).ToArray();
    }
}

internal static class AiKit
{
    public static readonly ContentPolicy Family = new(true);
    public static readonly ContentPolicy Mature = new(false);

    public static IOptions<AiOptions> Options(Action<AiOptions>? configure = null)
    {
        AiOptions o = new();
        configure?.Invoke(o);
        return Microsoft.Extensions.Options.Options.Create(o);
    }

    public static FoundryBotAgent Agent(FakeLlmClient llm, IBotMemoryStore? store = null, Action<AiOptions>? configure = null) =>
        new(llm, store ?? new InMemoryBotMemoryStore(), new BotPrompts(), Options(configure), NullLogger<FoundryBotAgent>.Instance);

    /// <summary>A bot's context with a persona and some company. Each call gets its own game and bot id unless given.</summary>
    public static BotContext Ctx(string alias = "Red Apple", string[]? others = null, Guid? game = null, Guid? bot = null) =>
        TestContext.For(alias) with
        {
            Key = new BotKey(game ?? Guid.NewGuid(), bot ?? Guid.NewGuid()),
            AlivePlayers = others ?? ["Blue Kiwi", "Green Pear"],
            Round = 1,
            Persona = [new PersonaAnswer("What snack could you eat forever?", QuestionKind.Text, "salty popcorn", [])]
        };

    public static ChatRequest Chat(BotContext ctx, IReadOnlyList<ChatLine>? recent = null, IReadOnlyList<ChatLine>? mentions = null, ContentPolicy? policy = null) =>
        new(ctx.BotAlias, "", policy ?? Family, ctx.Round, recent ?? [new ChatLine("Blue Kiwi", "who is the bot here?")], mentions ?? [], ctx.AlivePlayers, ctx);

    public static VoteRequest Vote(BotContext ctx, params VoteCandidate[] candidates) =>
        new(ctx.BotAlias, "", Family, ctx.Round,
            candidates.Length > 0 ? candidates : [new VoteCandidate(Guid.NewGuid(), "Blue Kiwi"), new VoteCandidate(Guid.NewGuid(), "Green Pear")],
            [new ChatLine("Blue Kiwi", "I think it is you")], ctx);
}
