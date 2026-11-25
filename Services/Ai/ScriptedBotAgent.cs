using System.Security.Cryptography;
using System.Text.RegularExpressions;
using TuringTown.Models;

namespace TuringTown.Services.Ai;

/// <summary>
/// An offline bot with no model behind it. It is the default agent for development and CI, and the fallback when the
/// real AI times out or fails, so a game can never stall waiting for it.
///
/// It tries hard not to be predictable: every bot has its own <see cref="BotVoice"/>, picks what to say by intent
/// (react, ask, accuse with a reason, defend, joke) from large pools, brings up its own form answers, never repeats
/// one of its recent lines, and votes the way a person tends to (with the room, against whoever accused it).
/// </summary>
public sealed partial class ScriptedBotAgent : IBotAgent
{
    // ---------------------------------------------------------------- the built-in form: opinions, never trivia

    /// <summary>A free-text question with answers a person might give, in mixed lengths and moods.</summary>
    private sealed record TextTemplate(string Text, bool Mature, string[] Answers);

    private static readonly TextTemplate[] FamilyText =
    [
        new("What is the most overrated food, and why?", false,
            ["avocado toast, its just mushy bread", "Sushi. there I said it", "pumpkin spice everything", "kale", "Brunch. its breakfast but expensive", "cupcakes, way too much frosting"]),
        new("What is a hot take you will defend forever?", false,
            ["cold pizza beats hot pizza", "Cats are better than dogs", "socks with sandals are comfy", "winter is the best season", "breakfast for dinner is elite", "movies are way too long now"]),
        new("What do you think is the best way to spend a rainy day?", false,
            ["blanket, snacks and a movie", "Sleeping. obviously", "baking something and eating all of it", "puzzles and music", "video games all day", "staring out the window like im in a music video"]),
        new("What is something everyone loves that you just do not get?", false,
            ["camping. why sleep on the ground", "Coffee, tastes like burnt water", "theme parks, too many lines", "the beach, sand gets everywhere", "superhero movies", "escape rooms"]),
        new("What do you think makes a perfect sandwich?", false,
            ["crunchy bread and too much cheese", "Toasted. always toasted", "pickles, no debate", "pb and j, keep it simple", "the sauce honestly", "someone else making it"]),
        new("Which chore is secretly the worst, and why?", false,
            ["folding laundry, it never ends", "Dishes because of the soggy food bits", "vacuuming the stairs", "putting clothes away. I just wont", "emptying the dishwasher for some reason"]),
        new("What is a small thing that instantly ruins your mood?", false,
            ["stubbing my toe", "When the wifi drops", "wet socks", "forgetting my headphones", "slow walkers", "a song getting cut off"]),
        new("What do you think is the most useless invention?", false,
            ["self checkout lol", "Those tiny drink umbrellas", "smart fridges", "auto play videos", "selfie sticks", "white sneakers"]),
        new("What is a snack combo that sounds weird but is actually great?", false,
            ["fries dipped in a milkshake", "Apples and peanut butter", "popcorn with chocolate chips", "pickles and chips", "cheese on apple pie", "pretzels in ice cream"]),
        new("What would you do with a completely free Saturday?", false,
            ["sleep in and then nap", "Long walk and a big breakfast", "binge a whole season", "clean everything then do nothing", "go to the library", "road trip somewhere random"]),
        new("What is a skill you wish you could learn instantly?", false,
            ["playing piano", "Speaking every language", "cooking without a recipe", "drawing", "juggling honestly", "fixing my own car"]),
        new("What is the best smell in the world?", false,
            ["fresh bread", "Rain on hot pavement", "coffee in the morning", "a new book", "cookies baking", "laundry out of the dryer"]),
        new("Which fictional place would you want to live in?", false,
            ["somewhere with dragons but safe", "A cozy village from a cartoon", "anywhere with talking animals", "a giant candy land", "a space station", "a treehouse city"]),
        new("What is something you were weirdly proud of as a kid?", false,
            ["my rock collection", "Tying my shoes first in class", "a perfect spelling test", "my bike skills", "staying up til midnight once", "my sticker book"]),
    ];

    private static readonly TextTemplate[] MatureText =
    [
        new("What harmless thing makes you irrationally pissed off?", true,
            ["people who dont put the cart back", "slow walkers", "reply all emails", "the printer refusing to work", "wet socks"]),
        new("What is your most embarrassing lazy-ass habit?", true,
            ["eating cereal straight from the box", "leaving laundry in the dryer", "rewatching the same show", "calling toast dinner", "ignoring my alarm"]),
        new("What petty thing would you put in a swear jar?", true,
            ["meetings that could be emails", "people spoiling the ending", "loud chewing", "slow internet", "the snooze button"]),
        new("What is a ridiculous thing you would ban if you had the power?", true,
            ["reply all emails", "voice notes longer than a minute", "tiny restaurant portions", "slow walkers", "monday mornings"]),
        new("What is the most pointless thing you have rage-quit?", true,
            ["a board game against my family", "assembling flat pack furniture", "a crossword", "a phone game level", "untangling headphones"]),
        new("What overhyped trend is honestly a load of crap?", true,
            ["fancy water bottles", "gender reveal parties", "influencer restaurants", "ten step skincare", "waking up at 5am"]),
    ];

    /// <summary>For a question the bot has no list for (a generated one): opinion-shaped, so it fits most questions.</summary>
    private static readonly string[] GenericAnswers =
    [
        "honestly depends on my mood", "I go back and forth on this", "probably the obvious one", "cant pick just one",
        "the simple option every time", "something cozy", "Whatever is the least effort lol", "no strong feelings tbh",
        "I would overthink this way too much", "the weird option, always", "anything but the popular answer",
    ];

    private sealed record ChoiceTemplate(string Text, bool Mature, string[] Options, int MaxSelect);

    private static readonly ChoiceTemplate[] FamilySingle =
    [
        new("Is a hot dog a sandwich?", false, ["Yes", "No", "Only on weekends", "It is a taco"], 1),
        new("Pineapple on pizza is...", false, ["A crime", "Genius", "Fine I guess", "Never tried it"], 1),
        new("When is breakfast food best?", false, ["Morning only", "Any time", "Midnight", "Dinner"], 1),
        new("Cereal first or milk first?", false, ["Cereal first", "Milk first", "No milk at all", "Who even cares"], 1),
        new("The best season is...", false, ["Summer", "Autumn", "Winter", "Spring"], 1),
        new("Socks with sandals?", false, ["Never", "Comfy so yes", "Only at home", "Fashion icon"], 1),
        new("The best pet is...", false, ["Dog", "Cat", "Fish", "Something weird like a lizard"], 1),
        new("Beach or mountains?", false, ["Beach", "Mountains", "Neither, stay home", "Both in one trip"], 1),
        new("Early bird or night owl?", false, ["Early bird", "Night owl", "Tired all day", "Depends on snacks"], 1),
        new("How do you eat a cookie?", false, ["In one bite", "Nibble the edges", "Dunk it", "Crumble it on ice cream"], 1),
        new("The best way to travel is...", false, ["Plane", "Train", "Road trip", "Teleport please"], 1),
        new("Pancakes or waffles?", false, ["Pancakes", "Waffles", "French toast", "Cereal is fine"], 1),
        new("If you could have one superpower?", false, ["Flying", "Invisibility", "Super speed", "Talking to animals", "Never needing sleep"], 1),
    ];

    private static readonly ChoiceTemplate[] MatureSingle =
    [
        new("Which petty annoyance deserves a swear jar?", true, ["Reply-all emails", "Loud chewing", "Spoilers", "Slow walkers"], 1),
        new("Your lazy-ass dinner of choice?", true, ["Cereal", "Cold pizza", "Toast", "Leftovers"], 1),
        new("Which damn noise annoys you most?", true, ["Car alarms", "Loud chewing", "Pen clicking", "Leaf blowers"], 1),
        new("Worst crap to step on barefoot?", true, ["A toy brick", "Something wet", "A cereal flake", "A cat toy"], 1),
    ];

    private static readonly ChoiceTemplate[] FamilyMulti =
    [
        new("Which of these are overrated? Pick up to 3.", false, ["Brunch", "Camping", "Theme parks", "Avocado toast", "Road trips", "Fancy coffee"], 3),
        new("Which of these are secretly great? Pick up to 3.", false, ["Cold pizza", "Rainy days", "Board games", "Naps", "Grocery shopping", "Early mornings"], 3),
        new("Which snacks belong at movie night? Pick up to 3.", false, ["Popcorn", "Nachos", "Candy", "Pizza", "Ice cream"], 3),
        new("Which pizza toppings are the best? Pick up to 3.", false, ["Pepperoni", "Mushrooms", "Pineapple", "Extra cheese", "Olives", "Peppers"], 3),
        new("Which would you bring to a desert island? Pick up to 2.", false, ["A good book", "Sunscreen", "A hammock", "Snacks", "A guitar", "A best friend"], 2),
        new("Which chores do you not mind doing? Pick up to 3.", false, ["Dishes", "Laundry", "Vacuuming", "Cooking", "Yard work", "Grocery runs"], 3),
        new("Which of these are perfect on a lazy Sunday? Pick up to 3.", false, ["Pancakes", "Naps", "Long walks", "Puzzles", "Movies", "Video games"], 3),
        new("Which animals would make the best roommates? Pick up to 2.", false, ["Penguin", "Golden retriever", "Sloth", "Owl", "Raccoon", "Cat"], 2),
        new("Which dessert would you never turn down? Pick up to 3.", false, ["Ice cream", "Brownies", "Cheesecake", "Cookies", "Apple pie", "Donuts"], 3),
        new("Which classes did you actually enjoy as a kid? Pick up to 3.", false, ["Math", "Art", "Science", "History", "Music", "Gym"], 3),
        new("Which holidays are the most fun? Pick up to 3.", false, ["Halloween", "New Year", "Birthdays", "Summer break", "Thanksgiving", "Winter holidays"], 3),
        new("Which sounds are the most relaxing? Pick up to 3.", false, ["Rain", "Ocean waves", "A crackling fire", "Birds", "Wind chimes", "A cat purring"], 3),
        new("Which superpowers would be most useful? Pick up to 2.", false, ["Flying", "Teleporting", "Mind reading", "Super strength", "Freezing time", "Healing"], 2),
    ];

    private static readonly ChoiceTemplate[] MatureMulti =
    [
        new("Which petty things piss you off? Pick up to 3.", true, ["Loud chewing", "Spoilers", "Slow walkers", "Reply-all emails", "Wet socks"], 3),
        new("Which lazy-ass choices do you defend? Pick up to 3.", true, ["Cereal for dinner", "Cold pizza", "Ignoring chores", "Midnight snacks", "Snoozing alarms"], 3),
        new("Which damn things ruin a road trip? Pick up to 3.", true, ["Traffic", "No snacks", "Bad music", "Wrong turns", "Car sickness", "Backseat drivers"], 3),
        new("Which crappy jobs would you hate most? Pick up to 2.", true, ["Cleaning gutters", "Telemarketing", "Mall Santa", "Dog walking in rain", "Toll booth", "Theme park mascot"], 2),
        new("Which petty crimes against food piss you off? Pick up to 3.", true, ["Soggy fries", "Cold coffee", "Burnt toast", "Too much ice", "Stale chips", "Tiny portions"], 3),
    ];

    /// <summary>
    /// Used by <see cref="BuiltInQuestionGenerator"/>, and directly by the engine when a generator returns questions
    /// that fail validation. Hand-written opinion questions of every kind, picked at random, in a random order.
    /// </summary>
    public static IReadOnlyList<OnboardingQuestion> BuiltInQuestions(ContentPolicy policy, QuestionMix mix)
    {
        List<OnboardingQuestion> picked = [];

        TextTemplate[] textPool = policy.FamilyFriendly ? FamilyText : [.. FamilyText, .. MatureText];
        foreach (TextTemplate t in Draw(textPool, mix.Text))
            picked.Add(new OnboardingQuestion("", QuestionKind.Text, t.Text, t.Mature, [], 0, 0));

        ChoiceTemplate[] singles = policy.FamilyFriendly ? FamilySingle : [.. FamilySingle, .. MatureSingle];
        foreach (ChoiceTemplate t in Draw(singles, mix.SingleChoice))
            picked.Add(ToQuestion(t, QuestionKind.SingleChoice));

        ChoiceTemplate[] multis = policy.FamilyFriendly ? FamilyMulti : [.. FamilyMulti, .. MatureMulti];
        foreach (ChoiceTemplate t in Draw(multis, mix.MultiChoice))
            picked.Add(ToQuestion(t, QuestionKind.MultiChoice));

        if (!policy.FamilyFriendly && picked.Count > 0 && !picked.Any(q => q.Mature))
        {
            int index = RandomNumberGenerator.GetInt32(picked.Count);
            picked[index] = MatureQuestion(picked[index].Kind);
        }

        return picked
            .OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue))
            .Select((q, i) => q with { Id = $"q{i + 1}" })
            .ToArray();
    }

    internal static OnboardingQuestion MatureQuestion(QuestionKind kind) => kind switch
    {
        QuestionKind.Text => TextQuestion(Pick(MatureText)),
        QuestionKind.SingleChoice => ToQuestion(Pick(MatureSingle), kind),
        QuestionKind.MultiChoice => ToQuestion(Pick(MatureMulti), kind),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static OnboardingQuestion TextQuestion(TextTemplate t) =>
        new("", QuestionKind.Text, t.Text, t.Mature, [], 0, 0);

    internal static IReadOnlyList<OnboardingQuestion> QuestionCandidates(ContentPolicy policy, QuestionKind kind, bool matureOnly = false)
    {
        IEnumerable<OnboardingQuestion> questions = kind switch
        {
            QuestionKind.Text => FamilyText.Concat(MatureText).Select(TextQuestion),
            QuestionKind.SingleChoice => FamilySingle.Concat(MatureSingle).Select(t => ToQuestion(t, kind)),
            QuestionKind.MultiChoice => FamilyMulti.Concat(MatureMulti).Select(t => ToQuestion(t, kind)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return questions.Where(q => (!policy.FamilyFriendly || !q.Mature) && (!matureOnly || q.Mature))
            .OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).ToArray();
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

    // ---------------------------------------------------------------- answering the form

    public Task<IReadOnlyList<OnboardingAnswer>> AnswerOnboardingAsync(OnboardingAnswerRequest request, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<OnboardingAnswer>>(request.Questions
            .Select(q => ScriptedAnswer(q, request.HumanAnswers, request.Context.Key.BotId))
            .ToArray());

    /// <summary>
    /// A valid answer to one question. A choice is drawn from what the humans picked (so it looks like theirs), or at
    /// random when nobody has answered yet. A text answer comes from a list for that question (or a generic one), about
    /// as long as the humans' and typed in the bot's voice, and never a copy of a human's. Also the engine's replacement
    /// for an agent answer that fails validation.
    /// </summary>
    public static OnboardingAnswer ScriptedAnswer(OnboardingQuestion question, IReadOnlyList<OnboardingAnswer> humanAnswers, Guid? botId = null)
    {
        if (question.Kind == QuestionKind.Text)
            return new OnboardingAnswer(question.Id, ScriptedText(question, humanAnswers, botId ?? Guid.NewGuid()), []);

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

    private static string ScriptedText(OnboardingQuestion question, IReadOnlyList<OnboardingAnswer> humanAnswers, Guid botId)
    {
        string[] allHuman = humanAnswers.Select(a => a.Text ?? "").Where(t => t.Trim().Length > 0).ToArray();
        string[] forThis = humanAnswers.Where(a => a.QuestionId == question.Id).Select(a => a.Text ?? "").Where(t => t.Trim().Length > 0).ToArray();

        string[] pool = FamilyText.Concat(MatureText).FirstOrDefault(t => t.Text == question.Text)?.Answers
            ?? Games.QuickFirePrompts.All.FirstOrDefault(p => p.Text == question.Text)?.Answers
            ?? GenericAnswers;
        // Never the same words as a human: if both came up in chat, the twin would stand out.
        string[] fresh = pool.Where(p => !Games.OnboardingAnswerValidator.IsNearCopy(p, forThis)).ToArray();
        if (fresh.Length > 0) pool = fresh;

        // About as long as the humans wrote: of two random picks, keep the one closer to their typical length.
        int typical = forThis.Length > 0 ? forThis.Select(t => t.Length).Order().ElementAt(forThis.Length / 2) : 0;
        BotVoice voice = BotVoice.For(botId, allHuman);
        string result = "";
        for (int attempt = 0; attempt < 6; attempt++)
        {
            string answer = Pick(pool);
            if (typical > 0)
            {
                string other = Pick(pool);
                if (Math.Abs(other.Length - typical) < Math.Abs(answer.Length - typical)) answer = other;
            }

            // Checked again once typed in the bot's voice, which may have changed a word or two.
            result = voice.Apply(answer, Random.Shared, chat: false);
            if (!Games.OnboardingAnswerValidator.IsNearCopy(result, forThis)) break;
        }
        return result;
    }

    // ---------------------------------------------------------------- chat

    [GeneratedRegex(@"\b(bot|bots|ai|robot|sus|suspicious|lying|liar|fake|sketchy|vote|voting|trust|weird|scripted)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Accusing { get; }

    private static readonly string[] FamilyChat =
    [
        "ok this is harder than I thought", "I genuinely have no idea who it is", "everyone sounds normal so far which is scary",
        "someone here is way too calm", "this round feels different", "i keep changing my mind", "someone is typing way too fast",
        "can we all agree to not vote me", "I need more chat before I decide", "nobody is acting weird yet and I hate it",
        "this is the most suspicious group ever", "whoever it is is good", "i was so sure last round",
        "alright who is being too perfect", "my brain is not braining today", "I trust like two people here", "the quiet ones worry me a bit",
        "this is fun but stressful", "someone said something earlier that stuck with me", "I feel like we are missing something obvious",
        "ok im paying attention now", "everyone is too normal", "hmm",
    ];

    /// <summary>What a relaxed person says while a chat is warming up: anything but detective work.</summary>
    private static readonly string[] SmallTalk =
    [
        "ok my phone is on 4 percent so if I vanish thats why", "im so hungry rn", "I am bad at this kind of game, just warning everyone",
        "that timer is way too short", "who picked these names lol", "cereal before milk, discuss", "honestly just here for the snacks",
        "is anyone else lying on the couch for this", "pineapple on pizza is fine and I wont take questions", "ok I just got comfy",
        "this music slaps tbh", "my brain is half asleep", "hi everyone", "already chaotic and nobody has said anything",
        "whoever named me deserves a raise", "I had way too much coffee today", "best game night snack, go",
    ];

    private static readonly string[] MatureChat =
    [
        "ok that was kinda unhinged", "bro what", "nah thats shady", "I dont trust any of you", "this is chaos and I love it",
        "someone is lying through their teeth", "who raised you people", "absolutely not",
    ];

    private static readonly string[] Reactions =
    [
        "lol", "hmm", "wait really", "huh", "haha", "ok", "interesting", "hold on", "wait what",
    ];

    private static readonly string[] Defences =
    [
        "me? I literally just got here", "nope wrong person", "why me lol", "I have been typing like a normal person",
        "thats a reach", "if I was the bot I would be quieter", "ok but what about you", "I said that because I meant it",
        "you are deflecting", "bold accusation with zero proof", "ask me anything, I will prove it",
    ];

    private static readonly string[] MentionReplies =
    [
        "not sure what you mean", "why are you asking me", "haha wait what", "I dunno, you tell me",
        "wait what did I miss", "can you explain", "not sure honestly",
    ];

    private static readonly string[] AccuseReasons =
    [
        "people are pointing at you, whats your side", "others seem suspicious of you, what do you think",
        "youre getting accused a lot, anything to say",
    ];

    private static readonly string[] OwnAnswerLines =
    [
        "I put {0} and I stand by it", "yes I said {0}, fight me", "{0} was my answer and its correct",
        "people are sleeping on {0}", "for the record I said {0}",
    ];

    public Task<string?> NextChatMessageAsync(ChatRequest request, CancellationToken ct)
    {
        BotContext ctx = request.Context;
        BotVoice voice = BotVoice.For(ctx.Key.BotId, ctx.Style);
        Random rng = Random.Shared;

        HashSet<string> recentlySaid = new(ctx.OwnLines.Select(l => Normal(l.Text)).Concat(request.Recent.Select(l => Normal(l.Text))));

        // Spoken to: answer the most recent person. Several tries, so a fresh line is found.
        for (int attempt = 0; attempt < 8; attempt++)
        {
            (string? mention, string? body) = request.Mentions.Count > 0
                ? ReplyToMention(request, rng)
                : attempt == 0 && rng.Next(100) < voice.QuietPercent
                    ? (null, null)
                    : FreeLine(request, voice, rng);

            if (body is null)
            {
                if (request.Mentions.Count == 0 && attempt == 0) return Task.FromResult<string?>(null);
                continue;
            }

            string text = voice.Apply(body, rng);
            string line = mention is null ? text : $"@{mention} {text}";
            if (line.Length > ctx.ChatMaxLength) line = line[..ctx.ChatMaxLength].TrimEnd();
            if (recentlySaid.Contains(Normal(line)) || recentlySaid.Contains(Normal(body))) continue;
            return Task.FromResult<string?>(line);
        }

        return Task.FromResult<string?>(null);
    }

    private static (string? Mention, string? Body) ReplyToMention(ChatRequest request, Random rng)
    {
        ChatLine said = request.Mentions[^1];
        BotContext ctx = request.Context;
        (string? Mention, string? Body) result = ReplyBody(request, said, ctx, rng);
        // Not every reply starts with an @mention: people often just answer.
        return rng.Next(100) < 70 ? result : (null, result.Body);
    }

    private static (string? Mention, string? Body) ReplyBody(ChatRequest request, ChatLine said, BotContext ctx, Random rng)
    {
        string[] others = request.OtherPlayers.Where(p => p != said.Alias).ToArray();

        if (Accusing.IsMatch(said.Text))
        {
            string[] defences = rng.Next(100) < 70 ? BotPersonality.For(ctx.Key).Defences : Defences;
            string defence = Pick(defences, rng).TrimEnd('?');
            // Now and then, point at someone else instead of just defending.
            if (others.Length > 0 && rng.Next(100) < 35)
                return (said.Alias, $"{defence}. what about @{others[rng.Next(others.Length)]} though");
            return (said.Alias, defence);
        }

        if (said.Text.Contains('?') && ctx.Persona.Count > 0)
        {
            PersonaAnswer[] matching = ctx.Persona.Where(p => QuestionMatches(said.Text, p.Question)).ToArray();
            if (matching.Length > 0)
            {
                PersonaAnswer p = matching[rng.Next(matching.Length)];
                string? mine = p.Kind == QuestionKind.Text ? p.Text : p.ChosenOptions.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(mine)) return (said.Alias, string.Format(Pick(OwnAnswerLines, rng), Quote(mine, p.Kind, rng)));
            }
            return (said.Alias, Pick(MentionReplies, rng));
        }

        return (said.Alias, Pick(MentionReplies, rng));
    }

    private static bool QuestionMatches(string message, string question)
    {
        string[] words = Regex.Matches(question.ToLowerInvariant(), @"\b[a-z]{5,}\b")
            .Select(m => m.Value).Where(w => w is not ("which" or "what" or "think" or "about" or "would" or "these" or "there")).ToArray();
        return words.Any(w => Regex.IsMatch(message, $@"\b{Regex.Escape(w)}\b", RegexOptions.IgnoreCase));
    }

    internal static (string? Mention, string? Body) FreeLine(ChatRequest request, BotVoice voice, Random rng)
    {
        BotContext ctx = request.Context;
        // ChatLine is a struct: cast first, or "none" would come back as an empty line instead of null.
        ChatLine? last = request.Recent
            .Where(l => !l.Alias.Equals(request.BotAlias, StringComparison.OrdinalIgnoreCase))
            .Select(l => (ChatLine?)l)
            .LastOrDefault();
        string[] pool = request.Policy.FamilyFriendly ? FamilyChat : [.. FamilyChat, .. MatureChat];

        int roll = rng.Next(100);

        // Bring up its own form answer. Answers are private, so a bot can only ever talk about its own.
        if (roll < 15)
        {
            string? mine = OwnAnswer(ctx, rng);
            if (mine is not null) return (null, string.Format(Pick(OwnAnswerLines, rng), mine));
        }

        // The chat is just warming up: small talk, like a person, not "who is the bot". Later, now and then too.
        int fromOthers = request.Recent.Count(l => !l.Alias.Equals(request.BotAlias, StringComparison.OrdinalIgnoreCase));
        if (fromOthers < 3 ? roll < 80 : roll < 23)
        {
            string[] openers = rng.Next(100) < 70 ? BotPersonality.For(ctx.Key).Openers : SmallTalk;
            return (null, Pick(openers, rng));
        }

        // React to the last thing someone said.
        if (roll < 55 && last is { } line)
            return (null, line.Text.Contains('?') ? "not sure what you mean" : Pick(Reactions, rng));

        // Accuse someone, with a reason. Leans towards whoever the room is already looking at.
        if (roll < 55 + voice.MentionPercent)
        {
            string[] accused = request.OtherPlayers.Where(p => request.Recent.Any(l =>
                !l.Alias.Equals(p, StringComparison.OrdinalIgnoreCase) && Mentions(l.Text, p) && Accusing.IsMatch(l.Text))).ToArray();
            if (accused.Length > 0) return (accused[rng.Next(accused.Length)], Pick(AccuseReasons, rng));
        }

        return (null, Pick(pool, rng));
    }

    /// <summary>A short piece of an answer to quote: one picked option, or the first few words of a text answer.</summary>
    private static string Quote(string answer, QuestionKind kind, Random rng)
    {
        if (kind != QuestionKind.Text)
        {
            string[] options = answer.Split(", ");
            return options[rng.Next(options.Length)].ToLowerInvariant();
        }

        string[] words = answer.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Take(5)).TrimEnd('.', ',', '!').ToLowerInvariant();
    }

    private static string? OwnAnswer(BotContext ctx, Random rng)
    {
        PersonaAnswer[] mine = ctx.Persona
            .Where(p => p.Kind == QuestionKind.Text ? !string.IsNullOrWhiteSpace(p.Text) : p.ChosenOptions.Count > 0)
            .ToArray();
        if (mine.Length == 0) return null;

        PersonaAnswer p = mine[rng.Next(mine.Length)];
        return p.Kind == QuestionKind.Text
            ? Quote(p.Text!, QuestionKind.Text, rng)
            : p.ChosenOptions[rng.Next(p.ChosenOptions.Count)].ToLowerInvariant();
    }

    // ---------------------------------------------------------------- votes

    public Task EndRoundAsync(RoundEndRequest request, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Goes with the room, with chance: each candidate's suspicion is how much the others have accused or mentioned them,
    /// and <see cref="VoteChoice"/> draws the vote. Whether someone accused this bot does not count: humans do the accusing,
    /// so voting back at an accuser would pick out the humans.
    /// </summary>
    public Task<Guid?> CastVoteAsync(VoteRequest request, CancellationToken ct)
    {
        if (request.Candidates.Count == 0) return Task.FromResult<Guid?>(null);

        double[] suspicion = request.Candidates.Select(c =>
        {
            double heat = Heat(c.Alias, request.Recent, request.BotAlias);
            return heat / (heat + 2);
        }).ToArray();

        return Task.FromResult<Guid?>(request.Candidates[VoteChoice.Pick(suspicion, Random.Shared.NextDouble())].Id);
    }

    /// <summary>How much the room has been talking about <paramref name="alias"/>: 2 per accusing mention, 1 per other mention.</summary>
    internal static double Heat(string alias, IReadOnlyList<ChatLine> recent, string botAlias) =>
        recent
            .Where(l => !l.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase) && !l.Alias.Equals(botAlias, StringComparison.OrdinalIgnoreCase))
            .Where(l => Mentions(l.Text, alias))
            .Sum(l => Accusing.IsMatch(l.Text) ? 2.0 : 1.0);

    private static bool Mentions(string text, string alias) => text.Contains("@" + alias, StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- helpers

    private static string Normal(string text) => new(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static T Pick<T>(T[] pool) => pool[RandomNumberGenerator.GetInt32(pool.Length)];

    private static string Pick(string[] pool, Random rng) => pool[rng.Next(pool.Length)];
}
