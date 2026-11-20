using System.Security.Cryptography;
using TuringTown.Models;

namespace TuringTown.Services.Ai;

/// <summary>
/// An offline bot with canned lines. It is the default agent for development and CI, and the fallback when the
/// real AI times out or fails, so a game can never stall waiting for it.
/// </summary>
public sealed class ScriptedBotAgent : IBotAgent
{
    private static readonly string[] FamilyQuestions =
    [
        "What is the best snack and why?",
        "Describe your perfect lazy Sunday.",
        "What is a small thing that always makes you smile?",
        "Which fictional place would you most like to visit?",
        "What would your superpower be, and what would you use it for?",
        "What is the worst advice you have ever received?",
        "If you could have any pet, what would it be?",
        "What is a hobby you would love to try?"
    ];

    private static readonly string[] MatureQuestions =
    [
        "What is your most irrational pet peeve?",
        "What is the most chaotic thing you have done for a dare?",
        "Which food do you defend that everyone else hates?",
        "What is the pettiest hill you will die on?"
    ];

    private static readonly string[] FamilyAnswers =
    [
        "Probably something simple, I am easy to please.",
        "Depends on the day honestly.",
        "I would have to think about that one.",
        "Anything with good music and no plans."
    ];

    private static readonly string[] FamilyChat =
    [
        "honestly not sure what to think yet",
        "who else feels a little suspicious?",
        "that last answer felt very rehearsed",
        "lol same",
        "I would watch who stays quiet",
        "hmm interesting take",
        "can someone explain that one?",
        "just trying to keep up tbh",
        "hard to tell yet, keep talking",
        "that sounded oddly perfect"
    ];

    private static readonly string[] MatureChat =
    [
        "ok that was kinda unhinged lol",
        "bro what",
        "nah that is sus as hell",
        "I do not trust that at all"
    ];

    /// <summary>
    /// Used directly by the engine when an agent returns questions that fail validation.
    /// </summary>
    public static IReadOnlyList<OnboardingQuestion> BuiltInQuestions(ContentPolicy policy, int count)
    {
        List<OnboardingQuestion> pool = FamilyQuestions.Select(t => new OnboardingQuestion("", t, false)).ToList();
        if (!policy.FamilyFriendly)
            pool.AddRange(MatureQuestions.Select(t => new OnboardingQuestion("", t, true)));

        return pool.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue))
            .Take(count)
            .Select((q, i) => q with { Id = $"q{i + 1}" })
            .ToArray();
    }

    public Task<IReadOnlyList<OnboardingQuestion>> GenerateOnboardingQuestionsAsync(QuestionRequest request, CancellationToken ct) =>
        Task.FromResult(BuiltInQuestions(request.Policy, request.Count));

    public Task<IReadOnlyList<string>> AnswerOnboardingAsync(OnboardingAnswerRequest request, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(request.Questions.Select(_ => Pick(FamilyAnswers)).ToArray());

    public Task<string?> NextChatMessageAsync(ChatRequest request, CancellationToken ct)
    {
        // Sometimes stay quiet, like a person reading along.
        if (RandomNumberGenerator.GetInt32(100) < 25) return Task.FromResult<string?>(null);

        string[] pool = request.Policy.FamilyFriendly ? FamilyChat : [.. FamilyChat, .. MatureChat];
        return Task.FromResult<string?>(Pick(pool));
    }

    public Task<Guid?> CastVoteAsync(VoteRequest request, CancellationToken ct)
    {
        if (request.Candidates.Count == 0) return Task.FromResult<Guid?>(null);
        return Task.FromResult<Guid?>(request.Candidates[RandomNumberGenerator.GetInt32(request.Candidates.Count)].Id);
    }

    private static string Pick(string[] pool) => pool[RandomNumberGenerator.GetInt32(pool.Length)];
}
