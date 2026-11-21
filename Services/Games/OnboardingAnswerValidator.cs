using TuringTown.Models;

namespace TuringTown.Services.Games;

/// <summary>
/// Checks a player's (or a bot's) onboarding answers against the questions that were actually asked. The server is
/// authoritative: unknown options, the wrong number of picks, free text on a choice question and over-long text are
/// all refused. The messages are shown to the player.
/// </summary>
public static class OnboardingAnswerValidator
{
    /// <summary>
    /// Checks one answer. <paramref name="cleaned"/> is the answer to store: trimmed text, or the ordered option ids.
    /// </summary>
    public static string? ValidateOne(OnboardingQuestion question, int number, OnboardingAnswer? answer, int maxTextLength, out OnboardingAnswer cleaned)
    {
        cleaned = new OnboardingAnswer(question.Id, "", []);
        if (answer is null) return "Please answer every question.";

        string rawText = answer.Text ?? "";
        IReadOnlyList<string> picked = answer.OptionIds ?? [];

        switch (question.Kind)
        {
            case QuestionKind.Text:
            {
                if (picked.Count > 0) return $"Question {number} takes a written answer.";
                ChatCheck check = ChatFilter.Check(rawText, maxTextLength, out string text);
                // An empty text answer is allowed, as it always was.
                if (check is not (ChatCheck.Ok or ChatCheck.Empty)) return ChatFilter.Describe(check);
                cleaned = new OnboardingAnswer(question.Id, text, []);
                return null;
            }

            case QuestionKind.SingleChoice:
            case QuestionKind.MultiChoice:
            {
                if (rawText.Trim().Length > 0) return $"Question {number} takes a choice, not text.";
                if (picked.Count < question.MinSelect || picked.Count > question.MaxSelect)
                {
                    return question.MinSelect == question.MaxSelect
                        ? $"Pick {Plural(question.MinSelect)} for question {number}."
                        : $"Pick {question.MinSelect} to {question.MaxSelect} options for question {number}.";
                }

                HashSet<string> known = question.Options.Select(o => o.Id).ToHashSet();
                HashSet<string> seen = [];
                foreach (string? id in picked)
                {
                    if (id is null || !known.Contains(id)) return $"That option is not available (question {number}).";
                    if (!seen.Add(id)) return $"Each option can be picked once (question {number}).";
                }

                // Keep the question's own order, so the stored answer does not depend on tap order.
                cleaned = new OnboardingAnswer(question.Id, "", question.Options.Where(o => seen.Contains(o.Id)).Select(o => o.Id).ToArray());
                return null;
            }

            default:
                return "Unknown question.";
        }
    }

    /// <summary>
    /// Checks a whole submission: every question answered exactly once, in any order, and nothing extra.
    /// </summary>
    public static string? Validate(IReadOnlyList<OnboardingQuestion> questions, IReadOnlyList<OnboardingAnswer?>? answers, int maxTextLength, out OnboardingAnswer[] cleaned)
    {
        cleaned = [];
        if (answers is null || answers.Count != questions.Count) return "Please answer every question.";

        Dictionary<string, OnboardingAnswer> byQuestion = [];
        foreach (OnboardingAnswer? answer in answers)
        {
            if (answer?.QuestionId is null || questions.All(q => q.Id != answer.QuestionId)) return "That question does not exist.";
            if (!byQuestion.TryAdd(answer.QuestionId, answer)) return "Answer each question once.";
        }

        OnboardingAnswer[] result = new OnboardingAnswer[questions.Count];
        for (int i = 0; i < questions.Count; i++)
        {
            string? error = ValidateOne(questions[i], i + 1, byQuestion.GetValueOrDefault(questions[i].Id), maxTextLength, out result[i]);
            if (error is not null) return error;
        }

        cleaned = result;
        return null;
    }

    private static string Plural(int n) => n == 1 ? "one option" : $"{n} options";

    /// <summary>
    /// True when a bot's free-text answer is (almost) a copy of a human's answer to the same question. Everyone sees the
    /// answer board, so two near-identical answers point straight at the bot. Compares letters and digits only, ignoring
    /// case; an answer of three or more words is a copy when nearly all of its words appear in one human answer.
    /// </summary>
    public static bool IsNearCopy(string? botText, IEnumerable<string?> humanTexts)
    {
        string bot = Normalize(botText);
        if (bot.Length == 0) return false;
        string[] botWords = bot.Split(' ');

        foreach (string? human in humanTexts)
        {
            string other = Normalize(human);
            if (other.Length == 0) continue;
            if (other == bot) return true;
            if (botWords.Length < 3) continue;

            HashSet<string> words = [.. other.Split(' ')];
            int shared = botWords.Count(words.Contains);
            if (shared >= botWords.Length * 0.8) return true;
        }
        return false;
    }

    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        char[] chars = text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
