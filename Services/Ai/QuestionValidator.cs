using System.Text.RegularExpressions;
using TuringTown.Models;

namespace TuringTown.Services.Ai;

/// <summary>
/// Checks onboarding questions returned by a generator before the game uses them. They are the "content pack" of
/// docs/game-data.md, and must keep players anonymous, so no question or option may ask for or offer names or
/// personal details. There is never a correct answer, so nothing here scores anything.
/// </summary>
public static partial class QuestionValidator
{
    public const int MaxTextLength = 120;
    public const int MaxOptionLength = 40;
    public const int MinChoiceOptions = 2;
    public const int MinMultiOptions = 3;
    public const int MaxOptions = 6;

    [GeneratedRegex(@"\b(your (full |real |first |last |sur)?name|how old|your age|birthday|where (do|did) you (live|work|go)|your (home )?address|phone|e-?mail|social media|username|instagram|which school|where do you work|years? old|your (job|city|town|school|employer)|job title|which (city|town)|\d{1,2}\s*-\s*\d{1,2}\s*(years|yrs)?|grade \d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PersonalDetail { get; }

    /// <summary>
    /// Shape only: the right number of valid questions of any kind.
    /// </summary>
    public static bool IsValid(IReadOnlyList<OnboardingQuestion>? questions, ContentPolicy policy, int expectedCount) =>
        Problem(questions, policy, expectedCount) is null;

    /// <summary>
    /// Shape, and exactly the requested number of questions of each kind.
    /// </summary>
    public static bool IsValid(IReadOnlyList<OnboardingQuestion>? questions, ContentPolicy policy, QuestionMix mix) =>
        Problem(questions, policy, mix) is null;

    /// <summary>Why <see cref="IsValid(IReadOnlyList{OnboardingQuestion}?, ContentPolicy, int)"/> fails, or null when it passes.</summary>
    public static string? Problem(IReadOnlyList<OnboardingQuestion>? questions, ContentPolicy policy, int expectedCount)
    {
        if (questions is null) return "no questions";
        if (questions.Count != expectedCount) return $"expected {expectedCount} questions, got {questions.Count}";
        return AllProblem(questions, policy);
    }

    /// <summary>Why <see cref="IsValid(IReadOnlyList{OnboardingQuestion}?, ContentPolicy, QuestionMix)"/> fails, or null when it passes. For logs.</summary>
    public static string? Problem(IReadOnlyList<OnboardingQuestion>? questions, ContentPolicy policy, QuestionMix mix)
    {
        if (questions is null) return "no questions";
        if (questions.Count != mix.Total) return $"expected {mix.Total} questions, got {questions.Count}";
        int text = questions.Count(q => q?.Kind == QuestionKind.Text);
        int single = questions.Count(q => q?.Kind == QuestionKind.SingleChoice);
        int multi = questions.Count(q => q?.Kind == QuestionKind.MultiChoice);
        if (text != mix.Text || single != mix.SingleChoice || multi != mix.MultiChoice)
            return $"wrong mix: expected {mix.Text} text/{mix.SingleChoice} single/{mix.MultiChoice} multi, got {text}/{single}/{multi}";
        return AllProblem(questions, policy);
    }

    private static string? AllProblem(IReadOnlyList<OnboardingQuestion> questions, ContentPolicy policy)
    {
        HashSet<string> ids = [];
        foreach (OnboardingQuestion? q in questions)
        {
            if (q is null) return "a question is null";
            if (string.IsNullOrWhiteSpace(q.Id) || !ids.Add(q.Id)) return $"missing or duplicate id '{q.Id}'";
            if (string.IsNullOrWhiteSpace(q.Text)) return $"{q.Id}: empty text";
            if (q.Text.Length > MaxTextLength) return $"{q.Id}: text is {q.Text.Length} chars (max {MaxTextLength}): \"{q.Text}\"";
            if (PersonalDetail.IsMatch(q.Text)) return $"{q.Id}: asks for a personal detail: \"{q.Text}\"";
            // A family-friendly room must never receive a question flagged as mature.
            if (policy.FamilyFriendly && q.Mature) return $"{q.Id}: flagged mature in a family-friendly room: \"{q.Text}\"";
            if (OptionsProblem(q, policy) is { } problem) return $"{q.Id} ({q.Kind}): {problem}: \"{q.Text}\"";
        }
        return null;
    }

    private static string? OptionsProblem(OnboardingQuestion q, ContentPolicy policy)
    {
        IReadOnlyList<QuestionOption> options = q.Options ?? [];

        switch (q.Kind)
        {
            case QuestionKind.Text:
                return options.Count == 0 && q.MinSelect == 0 && q.MaxSelect == 0 ? null : "a text question has options or selection limits";
            case QuestionKind.SingleChoice:
                if (options.Count is < MinChoiceOptions or > MaxOptions) return $"{options.Count} options (allowed {MinChoiceOptions}-{MaxOptions})";
                if (q.MinSelect != 1 || q.MaxSelect != 1) return $"select {q.MinSelect}..{q.MaxSelect} (must be 1..1)";
                break;
            case QuestionKind.MultiChoice:
                if (options.Count is < MinMultiOptions or > MaxOptions) return $"{options.Count} options (allowed {MinMultiOptions}-{MaxOptions})";
                if (q.MinSelect < 1 || q.MaxSelect < q.MinSelect || q.MaxSelect > options.Count || q.MaxSelect < 2)
                    return $"select {q.MinSelect}..{q.MaxSelect} with {options.Count} options (max must be 2..options)";
                break;
            default:
                return "unknown kind";
        }

        HashSet<string> ids = [];
        HashSet<string> texts = new(StringComparer.OrdinalIgnoreCase);
        foreach (QuestionOption option in options)
        {
            if (option.Id is not { Length: 1 } || option.Id[0] is < 'a' or > 'f' || !ids.Add(option.Id)) return $"bad option id '{option.Id}'";
            if (string.IsNullOrWhiteSpace(option.Text)) return "empty option";
            if (option.Text.Length > MaxOptionLength) return $"option \"{option.Text}\" is {option.Text.Length} chars (max {MaxOptionLength})";
            if (!texts.Add(option.Text.Trim())) return $"duplicate option \"{option.Text}\"";
            if (PersonalDetail.IsMatch(option.Text)) return $"option \"{option.Text}\" asks for a personal detail";
            if (policy.FamilyFriendly && option.Mature) return $"option \"{option.Text}\" flagged mature in a family-friendly room";
        }
        return null;
    }
}
