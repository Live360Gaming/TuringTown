using System.Globalization;
using System.Text.Json.Serialization;
using TuringTown.Models;

namespace TuringTown.Services.Ai;

/// <summary>
/// Identifies one bot in one game, so each bot has its own memory and nothing is shared between two bots. It is only
/// a storage key: it is never put into a prompt and never sent to a client.
/// </summary>
public readonly record struct BotKey(Guid GameId, Guid BotId);

/// <summary>
/// One of the bot's own onboarding answers, written out so a prompt can read it ("picked: Pizza, Tacos").
/// </summary>
public sealed record PersonaAnswer(string Question, QuestionKind Kind, string? Text, IReadOnlyList<string> ChosenOptions);

/// <summary>
/// What a finished round looked like, copied from the public Reveal (so it holds no more than every player saw).
/// <paramref name="WasBot"/> is the role of the player who was voted out, which the Reveal announces to everyone.
/// </summary>
public sealed record RoundOutcome(int Round, string? EliminatedAlias, bool? WasBot, IReadOnlyList<VoteCount> Tally);

/// <param name="Counts">How many humans picked each option.</param>
/// <param name="MedianPicked">The typical number of options picked (1 for a single choice).</param>
public sealed record ChoiceStyle(string Question, IReadOnlyList<OptionCount> Counts, int MedianPicked);

public readonly record struct OptionCount(string Option, int Count);

/// <summary>What the humans wrote for one free-text question: sanitised, capped quoted samples (untrusted player text).</summary>
public sealed record QuestionSamples(string Question, IReadOnlyList<string> Answers);

/// <summary>
/// How the humans wrote and what they picked on the onboarding form, as numbers plus a few short quoted samples,
/// so a bot can blend in. The samples are untrusted player text: they are sanitised and capped, and must only ever
/// reach a prompt as quoted data.
/// </summary>
public sealed record StyleProfile(
    int MedianLength,
    double LowercaseShare,
    double PunctuationRate,
    double EmojiShare,
    IReadOnlyList<string> Samples,
    IReadOnlyList<ChoiceStyle> Choices)
{
    public const int MaxSamples = 5;
    public const int MaxSampleLength = 120;

    /// <summary>
    /// The humans' answers to each free-text question, so a bot answers "the same question the way people here did".
    /// Left out of the style JSON (the onboarding prompt carries it on its own) so chat prompts stay short.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<QuestionSamples> TextAnswers { get; init; } = [];

    public static StyleProfile Empty { get; } = new(0, 0, 0, 0, [], []);

    /// <summary>
    /// The writing-style numbers only: no human text and no picks. Answers are private, so this is all a bot gets once
    /// the form is over, and it can never quote or count anyone else's answers in the chat.
    /// </summary>
    public StyleProfile WithoutAnswers() => this with { Samples = [], Choices = [], TextAnswers = [] };

    /// <summary>
    /// Summarises the humans' answers. Carries no ids and no names.
    /// </summary>
    public static StyleProfile From(IReadOnlyList<OnboardingQuestion> questions, IReadOnlyList<OnboardingAnswer> humanAnswers)
    {
        Dictionary<string, OnboardingQuestion> byId = questions.ToDictionary(q => q.Id);

        string[] texts = humanAnswers
            .Where(a => byId.TryGetValue(a.QuestionId, out OnboardingQuestion? q) && q.Kind == QuestionKind.Text)
            .Select(a => PromptText.Sanitize(a.Text, MaxSampleLength))
            .Where(t => t.Length > 0)
            .ToArray();

        int totalChars = texts.Sum(t => t.Length);
        double lowercase = texts.Length == 0 ? 0 : texts.Count(t => !t.Any(char.IsUpper)) / (double)texts.Length;
        double punctuation = totalChars == 0 ? 0 : texts.Sum(t => t.Count(char.IsPunctuation)) / (double)totalChars;
        double emoji = texts.Length == 0 ? 0 : texts.Count(HasEmoji) / (double)texts.Length;

        List<ChoiceStyle> choices = [];
        foreach (OnboardingQuestion q in questions.Where(q => q.Kind != QuestionKind.Text))
        {
            List<string[]> picks = humanAnswers
                .Where(a => a.QuestionId == q.Id && a.OptionIds is { Count: > 0 })
                .Select(a => a.OptionIds!.ToArray())
                .ToList();

            OptionCount[] counts = q.Options
                .Select(o => new OptionCount(o.Text, picks.Count(p => p.Contains(o.Id))))
                .ToArray();
            choices.Add(new ChoiceStyle(q.Text, counts, Median(picks.Select(p => p.Length).ToArray())));
        }

        QuestionSamples[] perQuestion = questions
            .Where(q => q.Kind == QuestionKind.Text)
            .Select(q => new QuestionSamples(q.Text, humanAnswers
                .Where(a => a.QuestionId == q.Id)
                .Select(a => PromptText.Sanitize(a.Text, MaxSampleLength))
                .Where(t => t.Length > 0)
                .Distinct()
                .Take(MaxSamples)
                .ToArray()))
            .ToArray();

        return new StyleProfile(
            Median(texts.Select(t => t.Length).ToArray()),
            lowercase,
            punctuation,
            emoji,
            texts.Distinct().Take(MaxSamples).ToArray(),
            choices)
        {
            TextAnswers = perQuestion
        };
    }

    private static int Median(int[] values) => values.Length == 0 ? 0 : values.Order().ElementAt(values.Length / 2);

    private static bool HasEmoji(string text) =>
        text.Any(c => char.IsSurrogate(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.OtherSymbol);
}

/// <summary>
/// Everything one bot is given on a call, and nothing else: its own role (implied by being asked), how many bots
/// there are, its own persona and lines, and what every player can see. <b>No field says whether any other player is
/// a bot.</b> With two bots, neither is told which player is its teammate.
/// </summary>
/// <param name="BotCount">How many bots are in the room, including this one.</param>
/// <param name="AlivePlayers">Aliases of the other living players (never this bot's own alias).</param>
/// <param name="History">The finished rounds, as the Reveal showed them.</param>
/// <param name="OwnLines">This bot's own latest lines, so it stays consistent.</param>
/// <param name="ChatMaxLength">The longest chat line the game accepts.</param>
public sealed record BotContext(
    BotKey Key,
    string BotAlias,
    int BotCount,
    IReadOnlyList<PersonaAnswer> Persona,
    int Round,
    IReadOnlyList<string> AlivePlayers,
    IReadOnlyList<RoundOutcome> History,
    IReadOnlyList<ChatLine> OwnLines,
    StyleProfile Style,
    int ChatMaxLength = 200);
