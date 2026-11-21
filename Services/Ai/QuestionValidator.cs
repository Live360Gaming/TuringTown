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
        questions is not null && questions.Count == expectedCount && AllValid(questions, policy);

    /// <summary>
    /// Shape, and exactly the requested number of questions of each kind.
    /// </summary>
    public static bool IsValid(IReadOnlyList<OnboardingQuestion>? questions, ContentPolicy policy, QuestionMix mix)
    {
        if (questions is null || questions.Count != mix.Total) return false;
        return questions.Count(q => q?.Kind == QuestionKind.Text) == mix.Text
               && questions.Count(q => q?.Kind == QuestionKind.SingleChoice) == mix.SingleChoice
               && questions.Count(q => q?.Kind == QuestionKind.MultiChoice) == mix.MultiChoice
               && AllValid(questions, policy);
    }

    private static bool AllValid(IReadOnlyList<OnboardingQuestion> questions, ContentPolicy policy)
    {
        HashSet<string> ids = [];
        foreach (OnboardingQuestion? q in questions)
        {
            if (q is null || string.IsNullOrWhiteSpace(q.Id) || !ids.Add(q.Id)) return false;
            if (string.IsNullOrWhiteSpace(q.Text) || q.Text.Length > MaxTextLength) return false;
            if (PersonalDetail.IsMatch(q.Text)) return false;
            // A family-friendly room must never receive a question flagged as mature.
            if (policy.FamilyFriendly && q.Mature) return false;
            if (!OptionsValid(q, policy)) return false;
        }
        return true;
    }

    private static bool OptionsValid(OnboardingQuestion q, ContentPolicy policy)
    {
        IReadOnlyList<QuestionOption> options = q.Options ?? [];

        switch (q.Kind)
        {
            case QuestionKind.Text:
                return options.Count == 0 && q.MinSelect == 0 && q.MaxSelect == 0;
            case QuestionKind.SingleChoice:
                if (options.Count is < MinChoiceOptions or > MaxOptions) return false;
                if (q.MinSelect != 1 || q.MaxSelect != 1) return false;
                break;
            case QuestionKind.MultiChoice:
                if (options.Count is < MinMultiOptions or > MaxOptions) return false;
                if (q.MinSelect < 1 || q.MaxSelect < q.MinSelect || q.MaxSelect > options.Count || q.MaxSelect < 2) return false;
                break;
            default:
                return false;
        }

        HashSet<string> ids = [];
        HashSet<string> texts = new(StringComparer.OrdinalIgnoreCase);
        foreach (QuestionOption option in options)
        {
            if (option.Id is not { Length: 1 } || option.Id[0] is < 'a' or > 'f' || !ids.Add(option.Id)) return false;
            if (string.IsNullOrWhiteSpace(option.Text) || option.Text.Length > MaxOptionLength) return false;
            if (!texts.Add(option.Text.Trim())) return false;
            if (PersonalDetail.IsMatch(option.Text)) return false;
            if (policy.FamilyFriendly && option.Mature) return false;
        }
        return true;
    }
}
