using System.Text.RegularExpressions;
using TuringTown.Models;

namespace TuringTown.Services.Ai;

/// <summary>
/// Checks onboarding questions returned by an agent before the game uses them. They are the "content pack" of
/// docs/game-data.md, and must keep players anonymous, so none may ask for names or personal details.
/// </summary>
public static partial class QuestionValidator
{
    public const int MaxTextLength = 120;

    [GeneratedRegex(@"\b(your (full |real |first |last |sur)?name|how old|your age|birthday|where (do|did) you (live|work|go)|your (home )?address|phone|e-?mail|social media|username|instagram|which school|where do you work)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PersonalDetail { get; }

    public static bool IsValid(IReadOnlyList<OnboardingQuestion>? questions, ContentPolicy policy, int expectedCount)
    {
        if (questions is null || questions.Count != expectedCount) return false;

        HashSet<string> ids = [];
        foreach (OnboardingQuestion q in questions)
        {
            if (string.IsNullOrWhiteSpace(q.Id) || !ids.Add(q.Id)) return false;
            if (string.IsNullOrWhiteSpace(q.Text) || q.Text.Length > MaxTextLength) return false;
            if (PersonalDetail.IsMatch(q.Text)) return false;
            // A family-friendly room must never receive a question flagged as mature.
            if (policy.FamilyFriendly && q.Mature) return false;
        }
        return true;
    }
}
