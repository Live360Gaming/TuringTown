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

    private sealed record ChoiceTemplate(string Text, bool Mature, string[] Options, int MaxSelect);

    private static readonly ChoiceTemplate[] FamilySingle =
    [
        new("Pick a lazy Sunday.", false, ["Movie marathon", "Long nap", "Video games", "Walk outside"], 1),
        new("Best pizza topping?", false, ["Pepperoni", "Pineapple", "Mushrooms", "Plain cheese"], 1),
        new("Pick a superpower.", false, ["Flying", "Invisibility", "Mind reading", "Time travel"], 1),
        new("Which pet would you adopt?", false, ["Dog", "Cat", "Parrot", "Tiny dragon"], 1),
        new("Pick a holiday for tonight.", false, ["Beach", "Mountain cabin", "Big city", "Theme park"], 1)
    ];

    private static readonly ChoiceTemplate[] MatureSingle =
    [
        new("Pick the pettiest hill to die on.", true, ["Pineapple on pizza", "Cereal before milk", "Reply-all emails", "Loud chewing"], 1),
        new("Pick your guilty pleasure.", true, ["Trashy reality TV", "Fast food at 2am", "Karaoke disasters", "Doomscrolling"], 1)
    ];

    private static readonly ChoiceTemplate[] FamilyMulti =
    [
        new("What goes in your road trip bag? Pick up to 3.", false, ["Snacks", "Playlist", "Blanket", "Board games", "Camera"], 3),
        new("Which hobbies sound fun? Pick up to 3.", false, ["Baking", "Hiking", "Painting", "Gaming", "Gardening", "Chess"], 3),
        new("Which movie genres do you enjoy? Pick up to 3.", false, ["Comedy", "Horror", "Sci-fi", "Romance", "Documentary"], 3)
    ];

    private static readonly ChoiceTemplate[] MatureMulti =
    [
        new("Which questionable habits do you own up to? Pick up to 3.", true, ["Cereal for dinner", "Talking to yourself", "Rewatching one show", "Ignoring texts for days", "Online shopping at night"], 3),
        new("Which snacks would you steal from a friend? Pick up to 3.", true, ["Chips", "Chocolate", "Leftover pizza", "Candy", "Ice cream"], 3)
    ];

    /// <summary>
    /// Used by <see cref="BuiltInQuestionGenerator"/>, and directly by the engine when a generator returns questions
    /// that fail validation. Hand-written questions of every kind, picked at random, in a random order.
    /// </summary>
    public static IReadOnlyList<OnboardingQuestion> BuiltInQuestions(ContentPolicy policy, QuestionMix mix)
    {
        List<OnboardingQuestion> picked = [];

        string[] textPool = policy.FamilyFriendly ? FamilyQuestions : [.. FamilyQuestions, .. MatureQuestions];
        foreach (string text in Draw(textPool, mix.Text))
            picked.Add(new OnboardingQuestion("", QuestionKind.Text, text, !FamilyQuestions.Contains(text), [], 0, 0));

        ChoiceTemplate[] singles = policy.FamilyFriendly ? FamilySingle : [.. FamilySingle, .. MatureSingle];
        foreach (ChoiceTemplate t in Draw(singles, mix.SingleChoice))
            picked.Add(ToQuestion(t, QuestionKind.SingleChoice));

        ChoiceTemplate[] multis = policy.FamilyFriendly ? FamilyMulti : [.. FamilyMulti, .. MatureMulti];
        foreach (ChoiceTemplate t in Draw(multis, mix.MultiChoice))
            picked.Add(ToQuestion(t, QuestionKind.MultiChoice));

        return picked
            .OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue))
            .Select((q, i) => q with { Id = $"q{i + 1}" })
            .ToArray();
    }

    private static OnboardingQuestion ToQuestion(ChoiceTemplate t, QuestionKind kind)
    {
        QuestionOption[] options = t.Options
            .OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue))
            .Select((text, i) => new QuestionOption(((char)('a' + i)).ToString(), text, false))
            .ToArray();
        return new OnboardingQuestion("", kind, t.Text, t.Mature, options, 1, kind == QuestionKind.SingleChoice ? 1 : Math.Min(t.MaxSelect, options.Length));
    }

    /// <summary>
    /// Takes <paramref name="count"/> distinct items at random, starting over with a fresh shuffle if the pool is
    /// smaller than the count (a game configured with more questions than there are built in).
    /// </summary>
    private static List<T> Draw<T>(T[] pool, int count)
    {
        List<T> result = [];
        while (result.Count < count && pool.Length > 0)
            result.AddRange(pool.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).Take(count - result.Count));
        return result;
    }

    public Task<IReadOnlyList<OnboardingAnswer>> AnswerOnboardingAsync(OnboardingAnswerRequest request, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<OnboardingAnswer>>(request.Questions.Select(q => ScriptedAnswer(q, request.HumanAnswers)).ToArray());

    /// <summary>
    /// A valid answer to one question. A choice is drawn from what the humans picked (so it looks like theirs), or at
    /// random when nobody has answered yet. Also the engine's replacement for an agent answer that fails validation.
    /// </summary>
    public static OnboardingAnswer ScriptedAnswer(OnboardingQuestion question, IReadOnlyList<OnboardingAnswer> humanAnswers)
    {
        if (question.Kind == QuestionKind.Text)
            return new OnboardingAnswer(question.Id, Pick(FamilyAnswers), []);

        List<string[]> humanPicks = humanAnswers
            .Where(a => a.QuestionId == question.Id && a.OptionIds is { Count: > 0 })
            .Select(a => a.OptionIds!.ToArray())
            .ToList();

        // How many to pick: about what the humans did, within the allowed range.
        int count = question.MaxSelect;
        if (question.Kind == QuestionKind.MultiChoice)
        {
            count = humanPicks.Count > 0
                ? humanPicks.Select(p => p.Length).Order().ElementAt(humanPicks.Count / 2)
                : RandomNumberGenerator.GetInt32(question.MinSelect, question.MaxSelect + 1);
            count = Math.Clamp(count, question.MinSelect, question.MaxSelect);
        }

        // Prefer options at least one human picked (a pick is drawn from the humans' own distribution), then anything else.
        List<string> chosen = [];
        string[] pool = humanPicks.SelectMany(p => p).Where(id => question.Options.Any(o => o.Id == id)).ToArray();
        while (chosen.Count < count && pool.Length > 0 && pool.Any(id => !chosen.Contains(id)))
        {
            string id = pool[RandomNumberGenerator.GetInt32(pool.Length)];
            if (!chosen.Contains(id)) chosen.Add(id);
        }

        foreach (QuestionOption option in question.Options.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)))
        {
            if (chosen.Count >= count) break;
            if (!chosen.Contains(option.Id)) chosen.Add(option.Id);
        }

        return new OnboardingAnswer(question.Id, "", question.Options.Where(o => chosen.Contains(o.Id)).Select(o => o.Id).ToArray());
    }

    private static readonly string[] MentionReplies =
    [
        "who, me? I am just chatting",
        "not sure what you mean",
        "why are you asking me?",
        "haha fair question",
        "I was wondering the same about you"
    ];

    private static readonly string[] MentionOpeners =
    [
        "you have been quiet",
        "what do you think?",
        "anything to add?",
        "I have a feeling about you"
    ];

    public Task<string?> NextChatMessageAsync(ChatRequest request, CancellationToken ct)
    {
        // Spoken to: answer the most recent person, addressing them by name.
        if (request.Mentions.Count > 0)
            return Task.FromResult<string?>($"@{request.Mentions[^1].Alias} {Pick(MentionReplies)}");

        // Sometimes stay quiet, like a person reading along.
        if (RandomNumberGenerator.GetInt32(100) < 25) return Task.FromResult<string?>(null);

        // Now and then, talk to someone directly.
        if (request.OtherPlayers.Count > 0 && RandomNumberGenerator.GetInt32(100) < 20)
            return Task.FromResult<string?>($"@{request.OtherPlayers[RandomNumberGenerator.GetInt32(request.OtherPlayers.Count)]} {Pick(MentionOpeners)}");

        string[] pool = request.Policy.FamilyFriendly ? FamilyChat : [.. FamilyChat, .. MatureChat];
        return Task.FromResult<string?>(Pick(pool));
    }

    public Task EndRoundAsync(RoundEndRequest request, CancellationToken ct) => Task.CompletedTask;

    public Task<Guid?> CastVoteAsync(VoteRequest request, CancellationToken ct)
    {
        if (request.Candidates.Count == 0) return Task.FromResult<Guid?>(null);
        return Task.FromResult<Guid?>(request.Candidates[RandomNumberGenerator.GetInt32(request.Candidates.Count)].Id);
    }

    private static string Pick(string[] pool) => pool[RandomNumberGenerator.GetInt32(pool.Length)];
}
