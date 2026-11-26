using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using TuringTown.Models;

namespace TuringTown.Services.Ai;

// The trivia behind Shout Out (see docs/shout-out.md): the categories, the questions, their checks, the built-in bank and
// the generator that writes questions for the room. The generator is not a player: it has no persona, and it never
// learns who is a bot.

public enum TriviaDifficulty
{
    Easy,
    Medium,
    Hard
}

/// <summary>
/// One trivia question with four answers, exactly one right (<paramref name="Correct"/> is its index). <paramref name="Era"/>
/// is the decade it belongs to (see <see cref="Eras"/>), or null for something timeless.
/// </summary>
public sealed record TriviaQuestion(
    string CategoryId,
    string Text,
    IReadOnlyList<string> Options,
    int Correct,
    TriviaDifficulty Difficulty,
    bool Mature,
    string? Era = null);

/// <summary>
/// What the generator is asked for: <paramref name="Count"/> questions from the room's <paramref name="Categories"/>,
/// pitched at what people who grew up in <paramref name="Eras"/> are likely to know (empty when unknown).
/// </summary>
public sealed record TriviaRequest(IReadOnlyList<TriviaCategory> Categories, IReadOnlyList<EraCount> Eras, int Count, ContentPolicy Policy);

public interface ITriviaGenerator
{
    Task<IReadOnlyList<TriviaQuestion>> GenerateAsync(TriviaRequest request, CancellationToken ct);
}

/// <summary>The categories the room votes on. Broad genres, so the built-in bank and the AI both fill any of them.</summary>
public static class TriviaCategories
{
    public static readonly TriviaCategory[] All =
    [
        new("disney", "Disney & animation"),
        new("movies", "Movies"),
        new("music", "Music"),
        new("games", "Video games"),
        new("tv", "TV & cartoons"),
        new("sports", "Sports"),
        new("food", "Food & drink"),
        new("science", "Science & nature")
    ];

    public static TriviaCategory? Find(string? id) => All.Cast<TriviaCategory?>().FirstOrDefault(c => c!.Value.Id == id);

    public static string NameOf(string id) => Find(id)?.Name ?? id;
}

/// <summary>
/// Checks one trivia question before a game uses it: four distinct answers, a valid right answer, sensible lengths, a
/// known category, nothing personal and nothing mature in a family room.
/// </summary>
public static class TriviaValidator
{
    public const int OptionCount = 4;
    public const int MinTextLength = 10;
    public const int MaxTextLength = 160;
    public const int MaxOptionLength = 40;

    public static string? Problem(TriviaQuestion? q, ContentPolicy policy)
    {
        if (q is null) return "no question";
        if (TriviaCategories.Find(q.CategoryId) is null) return $"unknown category '{q.CategoryId}'";
        string text = q.Text?.Trim() ?? "";
        if (text.Length is < MinTextLength or > MaxTextLength) return $"question is {text.Length} chars: \"{text}\"";
        if (QuestionValidator.AsksForPersonalDetail(text)) return $"asks for a personal detail: \"{text}\"";
        if (policy.FamilyFriendly && q.Mature) return $"mature in a family room: \"{text}\"";
        if (q.Options is not { Count: OptionCount }) return $"{q.Options?.Count ?? 0} answers (needs {OptionCount}): \"{text}\"";
        if (q.Correct is < 0 or >= OptionCount) return $"right answer {q.Correct} is out of range: \"{text}\"";
        if (q.Era is not null && !Eras.IsValid(q.Era)) return $"unknown era '{q.Era}'";

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string option in q.Options)
        {
            string o = option?.Trim() ?? "";
            if (o.Length is 0 or > MaxOptionLength) return $"answer \"{o}\" is {o.Length} chars: \"{text}\"";
            if (!seen.Add(o)) return $"duplicate answer \"{o}\": \"{text}\"";
            if (QuestionValidator.AsksForPersonalDetail(o)) return $"answer \"{o}\" asks for a personal detail";
        }
        return null;
    }

    public static bool IsValid(TriviaQuestion? q, ContentPolicy policy) => Problem(q, policy) is null;

    /// <summary>The usable questions, without repeats, in their order.</summary>
    public static IReadOnlyList<TriviaQuestion> Usable(IEnumerable<TriviaQuestion?> questions, ContentPolicy policy)
    {
        HashSet<string> texts = new(StringComparer.OrdinalIgnoreCase);
        return questions.Where(q => IsValid(q, policy) && texts.Add(q!.Text.Trim())).Select(q => q!).ToArray();
    }
}

/// <summary>
/// Hand-written questions in every category, mostly easy, tagged with their decade. Works offline, and fills in when the
/// AI's questions are missing, wrong or too few.
/// </summary>
public sealed class BuiltInTrivia : ITriviaGenerator
{
    private static TriviaQuestion Q(string category, string text, string right, string w1, string w2, string w3, string? era = null,
        TriviaDifficulty difficulty = TriviaDifficulty.Easy) =>
        new(category, text, [right, w1, w2, w3], 0, difficulty, false, era);

    // The right answer is always first here; Pick shuffles the answers.
    internal static readonly TriviaQuestion[] Bank =
    [
        Q("disney", "In The Lion King, what is Simba's father called?", "Mufasa", "Scar", "Rafiki", "Zazu", "1990s"),
        Q("disney", "What kind of animal is Dumbo?", "An elephant", "A mouse", "A deer", "A rabbit"),
        Q("disney", "In Frozen, what is the snowman called?", "Olaf", "Sven", "Kristoff", "Hans", "2010s"),
        Q("disney", "Which Pixar film follows a little robot cleaning up Earth?", "WALL-E", "Cars", "Up", "Ratatouille", "2000s"),
        Q("disney", "In Finding Nemo, what kind of fish is Dory?", "A blue tang", "A clownfish", "A pufferfish", "A goldfish", "2000s", TriviaDifficulty.Medium),
        Q("disney", "In Encanto, which sister is super strong?", "Luisa", "Isabela", "Mirabel", "Dolores", "2020s", TriviaDifficulty.Medium),
        Q("disney", "Which princess has a pet tiger named Rajah?", "Jasmine", "Ariel", "Belle", "Mulan", "1990s"),

        Q("movies", "Which movie has the line \"I'll be back\"?", "The Terminator", "Rocky", "Jaws", "Top Gun", "1980s"),
        Q("movies", "In Back to the Future, which car becomes the time machine?", "A DeLorean", "A Mustang", "A Beetle", "A Corvette", "1980s"),
        Q("movies", "Which ship sinks in the 1997 film starring Leonardo DiCaprio?", "Titanic", "Lusitania", "Britannic", "Poseidon", "1990s"),
        Q("movies", "Who is Harry Potter's red-haired best friend?", "Ron Weasley", "Neville Longbottom", "Draco Malfoy", "Cedric Diggory", "2000s"),
        Q("movies", "In Toy Story, what is the cowboy called?", "Woody", "Buzz", "Rex", "Hamm", "1990s"),
        Q("movies", "Which movie features a shark terrorizing Amity Island?", "Jaws", "Alien", "Grease", "Rocky", "1970s"),

        Q("music", "Which band sang \"Hey Jude\"?", "The Beatles", "The Rolling Stones", "Queen", "ABBA", "1960s"),
        Q("music", "Who is known as the King of Pop?", "Michael Jackson", "Elvis Presley", "Prince", "Justin Bieber", "1980s"),
        Q("music", "Which group sang \"Wannabe\" in 1996?", "Spice Girls", "Destiny's Child", "TLC", "All Saints", "1990s"),
        Q("music", "Which singer released the album \"1989\"?", "Taylor Swift", "Adele", "Katy Perry", "Lady Gaga", "2010s"),
        Q("music", "How many strings does a standard guitar have?", "Six", "Four", "Five", "Eight"),
        Q("music", "Which K-pop group sang \"Dynamite\" in 2020?", "BTS", "BLACKPINK", "EXO", "TWICE", "2020s"),

        Q("games", "What is the name of Mario's brother?", "Luigi", "Wario", "Toad", "Yoshi", "1980s"),
        Q("games", "In Pac-Man, what chases Pac-Man around the maze?", "Ghosts", "Robots", "Zombies", "Bats", "1980s"),
        Q("games", "What color is Sonic the Hedgehog?", "Blue", "Red", "Green", "Yellow", "1990s"),
        Q("games", "Which game has Creepers that explode?", "Minecraft", "Fortnite", "Halo", "Tetris", "2010s"),
        Q("games", "What kind of creature is Pikachu?", "A Pokemon", "A Digimon", "A Tamagotchi", "A Furby", "1990s"),
        Q("games", "In which game do 100 players drop onto an island to battle?", "Fortnite", "Tetris", "Pong", "Animal Crossing", "2010s"),

        Q("tv", "Who lives in a pineapple under the sea?", "SpongeBob", "Patrick", "Squidward", "Mr. Krabs", "2000s"),
        Q("tv", "What is the name of the Simpsons' son?", "Bart", "Homer", "Ned", "Milhouse", "1990s"),
        Q("tv", "In Friends, what is the coffee shop called?", "Central Perk", "The Grind", "Java Hut", "Brew Crew", "1990s", TriviaDifficulty.Medium),
        Q("tv", "What kind of dog is Bluey?", "A Blue Heeler", "A Poodle", "A Beagle", "A Husky", "2020s"),
        Q("tv", "Which show is set in the town of Hawkins?", "Stranger Things", "Riverdale", "Lost", "Glee", "2010s"),
        Q("tv", "What does Scooby-Doo love to eat?", "Scooby Snacks", "Pizza", "Tacos", "Pancakes", "1970s"),

        Q("sports", "How many players does a soccer team have on the field?", "11", "9", "10", "12"),
        Q("sports", "In which sport do you hit a shuttlecock?", "Badminton", "Tennis", "Squash", "Golf"),
        Q("sports", "How many rings are on the Olympic flag?", "Five", "Four", "Six", "Seven"),
        Q("sports", "In basketball, how many points is a shot from behind the arc?", "Three", "Two", "Four", "One"),
        Q("sports", "Which sport is played at Wimbledon?", "Tennis", "Cricket", "Golf", "Rugby"),

        Q("food", "What is guacamole mainly made from?", "Avocado", "Peas", "Cucumber", "Spinach"),
        Q("food", "Which country does sushi come from?", "Japan", "China", "Thailand", "Korea"),
        Q("food", "What is the main ingredient in hummus?", "Chickpeas", "Lentils", "Black beans", "Peas", null, TriviaDifficulty.Medium),
        Q("food", "Which fruit wears its seeds on the outside?", "Strawberry", "Blueberry", "Cherry", "Grape"),
        Q("food", "What makes bread dough rise?", "Yeast", "Salt", "Sugar", "Butter"),

        Q("science", "Which planet is known as the Red Planet?", "Mars", "Venus", "Jupiter", "Mercury"),
        Q("science", "What sweet food do bees make?", "Honey", "Milk", "Silk", "Butter"),
        Q("science", "What is the largest ocean on Earth?", "The Pacific", "The Atlantic", "The Indian", "The Arctic"),
        Q("science", "How many legs does a spider have?", "Eight", "Six", "Ten", "Four"),
        Q("science", "What do caterpillars turn into?", "Butterflies", "Bees", "Beetles", "Spiders"),
    ];

    public Task<IReadOnlyList<TriviaQuestion>> GenerateAsync(TriviaRequest request, CancellationToken ct) =>
        Task.FromResult(Pick(request, request.Count));

    /// <summary>
    /// <paramref name="count"/> questions: from the chosen categories first, preferring the room's decades (and timeless
    /// ones), then from anywhere. The answers are shuffled. Repeats only if the bank runs out.
    /// </summary>
    public static IReadOnlyList<TriviaQuestion> Pick(TriviaRequest request, int count, IEnumerable<string>? exclude = null)
    {
        HashSet<string> skip = new(exclude ?? [], StringComparer.OrdinalIgnoreCase);
        HashSet<string> chosen = request.Categories.Select(c => c.Id).ToHashSet();
        HashSet<string> eras = request.Eras.Select(e => e.Era).ToHashSet();

        int Rank(TriviaQuestion q) =>
            (chosen.Contains(q.CategoryId) ? 0 : 2) + (q.Era is null || eras.Count == 0 || eras.Contains(q.Era) ? 0 : 1);

        TriviaQuestion[] ordered = Bank
            .Where(q => !skip.Contains(q.Text) && TriviaValidator.IsValid(q, request.Policy))
            .OrderBy(Rank).ThenBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue))
            .ToArray();
        if (ordered.Length == 0) return [];

        return Enumerable.Range(0, count).Select(i => Shuffle(ordered[i % ordered.Length])).ToArray();
    }

    /// <summary>The same question with its answers in a random order (the right one moves with them).</summary>
    public static TriviaQuestion Shuffle(TriviaQuestion q)
    {
        int[] order = Enumerable.Range(0, q.Options.Count).ToArray();
        RandomNumberGenerator.Shuffle(order.AsSpan());
        return q with { Options = order.Select(i => q.Options[i]).ToArray(), Correct = Array.IndexOf(order, q.Correct) };
    }
}

/// <summary>
/// Wraps the AI's trivia generator: a time limit, the validator, and the built-in bank to fill in whatever is missing,
/// so a game always has its questions.
/// </summary>
public sealed class FallbackTriviaGenerator(ITriviaGenerator inner, TimeSpan timeout, ILogger logger) : ITriviaGenerator
{
    public async Task<IReadOnlyList<TriviaQuestion>> GenerateAsync(TriviaRequest request, CancellationToken ct)
    {
        IReadOnlyList<TriviaQuestion> usable = [];
        try
        {
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            IReadOnlyList<TriviaQuestion> generated = await inner.GenerateAsync(request, limit.Token).WaitAsync(timeout, ct);
            usable = TriviaValidator.Usable(generated, request.Policy);
            if (usable.Count < generated.Count)
                logger.LogWarning("{Rejected} of {Total} generated trivia questions were rejected", generated.Count - usable.Count, generated.Count);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Trivia questions could not be generated");
        }

        if (usable.Count >= request.Count) return usable.Take(request.Count).ToArray();
        return [.. usable, .. BuiltInTrivia.Pick(request, request.Count - usable.Count, usable.Select(q => q.Text))];
    }
}
