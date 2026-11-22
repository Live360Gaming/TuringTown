using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using TuringTown.Models;

namespace TuringTown.Services.Ai;

// ---------------------------------------------------------------- prompts

/// <summary>A Witty Bits prompt: a fill-in or a question that invites a funny one-line answer. Never has a right answer.</summary>
public sealed record WittyPrompt(string Text, bool Mature);

public sealed record WittyPromptRequest(int Count, ContentPolicy Policy);

/// <summary>
/// Writes the prompts for one Witty Bits game. It is <b>not</b> a player: it has no persona and no game context.
/// Runs during the tutorial so its latency is hidden. See docs/witty-bits.md.
/// </summary>
public interface IWittyPromptGenerator
{
    Task<IReadOnlyList<WittyPrompt>> GenerateAsync(WittyPromptRequest request, CancellationToken ct);
}

/// <summary>
/// Checks generated prompts before a game uses them: the right number, distinct, a sensible length, no personal
/// details asked for, and nothing mature in a family room.
/// </summary>
public static class WittyPromptValidator
{
    public const int MinLength = 8;
    public const int MaxLength = 140;

    public static bool IsValid(IReadOnlyList<WittyPrompt>? prompts, ContentPolicy policy, int count) => Problem(prompts, policy, count) is null;

    /// <summary>Why the prompts are not usable, or null when they are. For logs.</summary>
    public static string? Problem(IReadOnlyList<WittyPrompt>? prompts, ContentPolicy policy, int count)
    {
        if (prompts is null) return "no prompts";
        if (prompts.Count != count) return $"expected {count} prompts, got {prompts.Count}";

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (WittyPrompt? prompt in prompts)
        {
            if (prompt is null || string.IsNullOrWhiteSpace(prompt.Text)) return "an empty prompt";
            string text = prompt.Text.Trim();
            if (text.Length is < MinLength or > MaxLength) return $"prompt is {text.Length} chars (allowed {MinLength}-{MaxLength}): \"{text}\"";
            if (!seen.Add(text)) return $"duplicate prompt \"{text}\"";
            if (QuestionValidator.AsksForPersonalDetail(text)) return $"asks for a personal detail: \"{text}\"";
            if (policy.FamilyFriendly && prompt.Mature) return $"flagged mature in a family-friendly room: \"{text}\"";
        }
        return null;
    }
}

/// <summary>
/// The built-in prompts, each with a few answers the scripted bots can give. Works offline, and is the fallback
/// when generated prompts are missing or invalid.
/// </summary>
public sealed class BuiltInWittyPrompts : IWittyPromptGenerator
{
    private sealed record Entry(string Text, bool Mature, string[] BotAnswers);

    private static readonly Entry[] Entries =
    [
        new("The worst thing to find in your sandwich", false, ["A tiny receipt for the sandwich", "Another, smaller sandwich", "A note that says 'help'"]),
        new("A terrible name for a pet goldfish", false, ["Lunch", "Mr. Flushy", "Gerald The Disposable"]),
        new("The real reason dinosaurs went extinct", false, ["They forgot their passwords", "Bad Wi-Fi", "Tiny arms, no high fives"]),
        new("What your houseplant thinks about you", false, ["Water me or face the consequences", "Nice try with the singing", "I've seen things"]),
        new("A rejected flavor of ice cream", false, ["Wet Sock Swirl", "Broccoli Ripple", "Mystery Puddle"]),
        new("The most useless superpower", false, ["Knowing what time it is, roughly", "Turning into a slightly taller you", "Talking to spoons"]),
        new("A bad slogan for a dentist", false, ["We'll get to the root of your wallet", "Open wide, no refunds", "Brace yourself"]),
        new("Something you should never say at a wedding", false, ["Is the cake free?", "Same time next year?", "I object... to the music"]),
        new("A sign that your robot vacuum has become self-aware", false, ["It asked for weekends off", "It started a podcast", "It hides under the couch and judges you"]),
        new("The worst possible theme for a party", false, ["Tax season", "Waiting room", "Grandpa's toenails"]),
        new("What the moon does when nobody is looking", false, ["Practices its full face", "Moonwalks, obviously", "Eats cheese"]),
        new("A terrible thing to hear from your pilot", false, ["Anyone know where the brakes are?", "Fun fact: this is my first day", "Does this look like Paris?"]),
        new("The secret ingredient in grandma's cookies", false, ["Vengeance", "Glitter", "Pure butter and lies"]),
        new("A weird thing to collect", false, ["Other people's grocery lists", "Left socks only", "Dramatic sighs"]),
        new("The worst advice a fortune cookie could give", false, ["Lick the next doorknob you see", "Your lucky number is no", "Run"]),
        new("What cats are secretly plotting", false, ["World domination, one keyboard at a time", "A sock heist", "To knock everything off everything"]),
        new("A new Olympic sport nobody asked for", false, ["Competitive napping", "Synchronized complaining", "Extreme couch sitting"]),
        new("The worst possible name for a boat", false, ["The Leaky Lady", "Titanic Two", "Glub Glub"]),
        new("A bad excuse for being late", false, ["My shadow got lost", "A goose blocked my car", "I was early yesterday"]),
        new("What aliens would complain about on Earth", false, ["Too many stairs", "No decent parking for saucers", "The pineapple pizza debate"]),
        new("A rejected name for a new planet", false, ["Planet McPlanetface", "Kevin", "Spare Rock"]),
        new("The worst thing to shout in a library", false, ["SPOILER ALERT!", "WHO TOOK MY SANDWICH?", "Shhhhh!"]),
        new("A job robots will never want", false, ["Untangling headphones", "Kids' party clown", "Explaining the offside rule"]),
        new("The most dramatic way to announce you are going to bed", false, ["Fog machine and slow fade out", "A farewell speech", "Trumpets"]),
        new("A suspicious thing to find in your fridge", false, ["A tiny door", "A carton labeled 'do not'", "Someone else's cake with your name on it"]),
        new("What a snowman would put on its dating profile", false, ["Looking for someone who melts my heart. Slowly.", "Cool, not cold", "Carrot nose, great listener"]),
        new("A bad name for a superhero", false, ["Captain Mild", "The Damp Avenger", "Sir Sneezes-a-Lot"]),
        new("The worst gift to give a teacher", false, ["A pop quiz", "A whistle for every student", "Homework, but for them"]),
        new("The real reason the chicken crossed the road", false, ["Better Wi-Fi on the other side", "Witness protection", "It owed the duck money"]),
        new("Something a pirate would put on a shopping list", false, ["Eye patches, bulk", "Parrot snacks", "One (1) map, treasure-marked"]),
        new("A terrible new holiday", false, ["National Wet Socks Day", "Nobody-Gets-Cake Day", "Give Back The Gift Day"]),
        new("The worst thing your GPS could say", false, ["Recalculating... your life choices", "You have arrived. Probably.", "Turn left into the lake"]),
        new("A movie title that would be ruined by one letter", false, ["Jurassic Pork", "The Lion Kink... I mean King", "Star Wars: The Phantom Mendace"]),
        new("What your phone would say if it could talk back", false, ["Charge me, coward", "Stop dropping me", "You have 4,000 unread messages and that's on you"]),
        new("A weird thing to bring to a picnic", false, ["A full-size fridge", "A goat", "Opinions"]),
        new("The worst thing to find out about your new neighbor", false, ["They practice the bagpipes at 3am", "They're a goose", "They have your old couch"]),
        new("A product nobody needs but everyone would buy", false, ["Self-stirring cereal", "A toaster that compliments you", "Glow-in-the-dark socks"]),
        new("The opening line of the world's most boring novel", false, ["It was a Tuesday. Nothing happened.", "Gerald checked the mail again.", "Chapter one: beige."]),
        new("What dogs think humans do all day", false, ["Stare at glowing rectangles and refuse to throw the ball", "Hunt in the big cold box", "Leave. For no reason."]),
        new("A bad thing to name your Wi-Fi network", false, ["FBI Van 3", "Virus Download Center", "Neighbor Stop Stealing"]),
        new("The worst flavor for toothpaste", false, ["Garlic Breeze", "Pickle Fresh", "Last Week's Pizza"]),
        new("A rejected rule for a board game", false, ["Whoever cries first wins", "Roll the dice into the lake", "No one may move, ever"]),
        new("The most awkward thing to say on a first date", true, ["So... do you like soup?", "My mom said you'd be taller", "I've already named our kids"]),
        new("The worst thing to text your boss by accident", true, ["love u bye", "Is the boss asleep yet?", "On my way!! (still in bed)"]),
        new("A bad name for a perfume", false, ["Eau de Gym Bag", "Wet Dog No. 5", "Regret"]),
        new("The worst thing to hear during a haircut", false, ["Oops.", "Have you considered hats?", "It'll grow back. Eventually."]),
    ];

    private static readonly string[] Generic =
    [
        "Honestly, a goose",
        "Three raccoons in a trench coat",
        "Grandma, but faster",
        "A strongly worded letter",
        "Nothing good, that's for sure",
        "Socks with sandals",
        "A suspiciously damp sandwich",
        "The sound of a kazoo",
        "Tuesday",
        "An angry potato",
        "Way too much glitter",
        "A tiny, furious hamster"
    ];

    public Task<IReadOnlyList<WittyPrompt>> GenerateAsync(WittyPromptRequest request, CancellationToken ct) =>
        Task.FromResult(Pick(request.Policy, request.Count));

    /// <summary>
    /// <paramref name="count"/> distinct built-in prompts the room may see, in a random order. If the list is too
    /// short for the room, prompts repeat in a fresh order rather than running out.
    /// </summary>
    public static IReadOnlyList<WittyPrompt> Pick(ContentPolicy policy, int count)
    {
        Entry[] allowed = Entries.Where(e => !policy.FamilyFriendly || !e.Mature).ToArray();
        List<WittyPrompt> picked = [];
        while (picked.Count < count)
        {
            Entry[] round = allowed.ToArray();
            RandomNumberGenerator.Shuffle(round.AsSpan());
            picked.AddRange(round.Take(count - picked.Count).Select(e => new WittyPrompt(e.Text, e.Mature)));
        }
        return picked;
    }

    /// <summary>A canned answer to <paramref name="prompt"/> that is not in <paramref name="taken"/> (a generic one for an unknown prompt).</summary>
    public static string ScriptedAnswer(string prompt, IReadOnlyCollection<string> taken)
    {
        Entry? entry = Entries.FirstOrDefault(e => string.Equals(e.Text, prompt, StringComparison.OrdinalIgnoreCase));
        string[] own = (entry?.BotAnswers ?? []).Where(a => !taken.Contains(a, StringComparer.OrdinalIgnoreCase)).ToArray();
        string[] pool = own.Length > 0 ? own : Generic.Where(a => !taken.Contains(a, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (pool.Length == 0) pool = Generic;
        return pool[RandomNumberGenerator.GetInt32(pool.Length)];
    }

    public static int Count(ContentPolicy policy) => Entries.Count(e => !policy.FamilyFriendly || !e.Mature);
}

/// <summary>Wraps a generator so a slow, failing or invalid result is replaced by the built-in prompts.</summary>
public sealed class FallbackWittyPromptGenerator(IWittyPromptGenerator inner, TimeSpan timeout, ILogger logger) : IWittyPromptGenerator
{
    public async Task<IReadOnlyList<WittyPrompt>> GenerateAsync(WittyPromptRequest request, CancellationToken ct)
    {
        try
        {
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            IReadOnlyList<WittyPrompt> prompts = await inner.GenerateAsync(request, limit.Token).WaitAsync(timeout, ct);
            if (WittyPromptValidator.Problem(prompts, request.Policy, request.Count) is not { } problem) return prompts;
            logger.LogWarning("Generated Witty Bits prompts were rejected: {Problem}", problem);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Witty Bits prompts could not be generated");
        }

        return BuiltInWittyPrompts.Pick(request.Policy, request.Count);
    }
}

// ---------------------------------------------------------------- the bots

/// <summary>
/// What a bot is told when it writes a Witty Bits answer. <paramref name="Taken"/> are the answers already written
/// this game (public once their matchups are shown), so it does not repeat one.
/// </summary>
public sealed record WittyAnswerRequest(BotKey Key, string Alias, string Prompt, IReadOnlyList<string> Taken, int MaxLength, ContentPolicy Policy, TeamContext? Night = null);

/// <summary>A bot votes in a matchup it did not write. It is never told who wrote which answer.</summary>
public sealed record WittyVoteRequest(BotKey Key, string Alias, string Prompt, IReadOnlyList<WittyEntry> Answers, ContentPolicy Policy, TeamContext? Night = null);

/// <summary>
/// A bot playing Witty Bits: write a funny answer, vote for the funniest answer. Every answer goes through the same
/// filters as a human's. See docs/witty-bits.md.
/// </summary>
public interface IWittyBotAgent
{
    Task<string?> WriteAnswerAsync(WittyAnswerRequest request, CancellationToken ct);

    /// <summary>The id of the answer the bot votes for, or null to abstain.</summary>
    Task<string?> VoteAsync(WittyVoteRequest request, CancellationToken ct);
}

/// <summary>The offline bot: canned answers for the built-in prompts, and a random vote.</summary>
public sealed class ScriptedWittyBotAgent : IWittyBotAgent
{
    public Task<string?> WriteAnswerAsync(WittyAnswerRequest request, CancellationToken ct) =>
        Task.FromResult<string?>(BuiltInWittyPrompts.ScriptedAnswer(request.Prompt, request.Taken));

    public Task<string?> VoteAsync(WittyVoteRequest request, CancellationToken ct)
    {
        WittyEntry[] choices = request.Answers.Where(a => !a.Missing).ToArray();
        return Task.FromResult(choices.Length == 0 ? null : choices[RandomNumberGenerator.GetInt32(choices.Length)].Id);
    }
}

/// <summary>Wraps a real agent: a failed or slow answer gets a scripted one, and a failed vote a scripted vote.</summary>
public sealed class FallbackWittyBotAgent(IWittyBotAgent inner, IWittyBotAgent fallback, TimeSpan answerTimeout, TimeSpan voteTimeout, ILogger logger) : IWittyBotAgent
{
    public Task<string?> WriteAnswerAsync(WittyAnswerRequest request, CancellationToken ct) =>
        CallAsync(c => inner.WriteAnswerAsync(request, c), c => fallback.WriteAnswerAsync(request, c), answerTimeout, ct);

    public Task<string?> VoteAsync(WittyVoteRequest request, CancellationToken ct) =>
        CallAsync(c => inner.VoteAsync(request, c), c => fallback.VoteAsync(request, c), voteTimeout, ct);

    private async Task<string?> CallAsync(Func<CancellationToken, Task<string?>> call, Func<CancellationToken, Task<string?>> safe, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            string? result = await call(limit.Token).WaitAsync(timeout, ct);
            if (!string.IsNullOrWhiteSpace(result)) return result;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "A Witty Bits bot call failed, using the scripted bot");
        }

        return await safe(ct);
    }
}
