using System.Security.Cryptography;
using System.Text.RegularExpressions;
using TuringTown.Models;

namespace TuringTown.Services.Ai;

/// <summary>
/// An offline bot with no model behind it. It is the default agent for development and CI, and the fallback when the
/// real AI times out or fails, so a game can never stall waiting for it.
///
/// It tries hard not to be predictable: every bot has its own <see cref="BotVoice"/>, picks what to say by intent
/// (react, ask, accuse with a reason, defend, joke) from large pools, brings up the public answer board, never repeats
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
    ];

    private static readonly TextTemplate[] MatureText =
    [
        new("What is the pettiest hill you will die on?", true,
            ["people who dont put the cart back", "Gif is a hard g", "toilet paper goes over not under", "reply all should be illegal", "jogging is just fast walking with drama"]),
        new("What is the most unhinged thing you think is totally normal?", true,
            ["cereal at 2am", "Talking to my plants", "rewatching the same show six times", "dipping fries in ice cream", "showering at night only"]),
        new("What opinion would get you kicked out of a group chat?", true,
            ["voice notes should be banned", "Pineapple belongs on pizza", "group chats should go quiet after 10", "brunch is a scam", "the original is never better"]),
        new("Which food do you defend that everyone else hates?", true,
            ["olives", "Black licorice honestly", "pineapple pizza forever", "brussels sprouts but roasted", "anchovies dont @ me"]),
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
    ];

    private static readonly ChoiceTemplate[] MatureSingle =
    [
        new("Texting back right away is...", true, ["Desperate", "Polite", "Rare", "Whatever"], 1),
        new("The worst habit in a housemate?", true, ["Loud chewing", "Dirty dishes", "Late night calls", "Stealing snacks"], 1),
    ];

    private static readonly ChoiceTemplate[] FamilyMulti =
    [
        new("Which of these are overrated? Pick up to 3.", false, ["Brunch", "Camping", "Theme parks", "Avocado toast", "Road trips", "Fancy coffee"], 3),
        new("Which of these are secretly great? Pick up to 3.", false, ["Cold pizza", "Rainy days", "Board games", "Naps", "Grocery shopping", "Early mornings"], 3),
        new("Which snacks belong at movie night? Pick up to 3.", false, ["Popcorn", "Nachos", "Candy", "Pizza", "Ice cream"], 3),
    ];

    private static readonly ChoiceTemplate[] MatureMulti =
    [
        new("Which habits are red flags? Pick up to 3.", true, ["Never texting back", "Chewing loudly", "Always late", "Reply-all emails", "Spoilers"], 3),
        new("Which questionable choices do you defend? Pick up to 3.", true, ["Cereal for dinner", "Staying up too late", "Rewatching one show", "Ignoring texts", "Midnight snacks"], 3),
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

        string[] pool = FamilyText.Concat(MatureText).FirstOrDefault(t => t.Text == question.Text)?.Answers ?? GenericAnswers;
        // Never the same words as a human: the answers are all on the board.
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
        "someone here is way too calm", "this round feels different", "i keep changing my mind", "the answers board is lowkey useful",
        "can we all agree to not vote me", "I need more chat before I decide", "nobody is acting weird yet and I hate it",
        "this is the most suspicious group ever", "whoever it is is good", "i was so sure last round", "we should look at the answers again",
        "alright who is being too perfect", "my brain is not braining today", "I trust like two people here", "the quiet ones worry me a bit",
        "this is fun but stressful", "someone said something earlier that stuck with me", "I feel like we are missing something obvious",
        "ok im paying attention now", "too many normal answers", "hmm",
    ];

    private static readonly string[] MatureChat =
    [
        "ok that was kinda unhinged", "bro what", "nah thats shady", "I dont trust any of you", "this is chaos and I love it",
        "someone is lying through their teeth", "who raised you people", "absolutely not",
    ];

    private static readonly string[] Reactions =
    [
        "lol", "true", "fair", "nah", "hmm idk about that", "lmao what", "same honestly", "wait really", "ok that is fair", "big if true",
        "agreed", "not buying it", "huh", "haha", "real", "I mean yeah", "thats what I was thinking", "eh",
    ];

    private static readonly string[] AnswersToQuestions =
    [
        "no idea tbh", "maybe?", "I think so", "not really", "hard to say", "depends", "yes 100%", "ask someone else lol",
        "I was about to ask the same thing", "probably not",
    ];

    private static readonly string[] Defences =
    [
        "me? I literally just got here", "nope wrong person", "why me lol", "I have been typing like a normal person",
        "thats a reach", "if I was the bot I would be quieter", "ok but what about you", "I said that because I meant it",
        "you are deflecting", "bold accusation with zero proof", "check my answers, they are so me",
    ];

    private static readonly string[] MentionReplies =
    [
        "not sure what you mean", "why are you asking me", "haha fair question", "I was wondering the same about you",
        "hmm good point", "I dunno, you tell me", "I stand by it", "yeah I guess", "wait what did I miss",
    ];

    private static readonly string[] AccuseReasons =
    [
        "you have been really quiet", "you keep dodging questions", "your answers feel kinda generic", "not trusting you rn",
        "you agreed with everyone so far", "that last message was way too polished", "you changed your story", "explain yourself",
        "something about your answers is off", "you answered way too fast",
    ];

    private static readonly string[] BoardAboutOthers =
    [
        "{0}? really?", "explain {0} please", "{0} is a wild answer", "ok {0} is actually valid", "{0} is such a human answer tbh",
        "I respect {0}", "{0} feels made up", "nobody actually thinks {0}",
    ];

    private static readonly string[] BoardAnonymous =
    [
        "whoever said {0} needs to explain", "who put {0} lol", "{0} is the most suspicious answer on there",
        "the {0} answer has me thinking", "{0}?? who did that",
    ];

    private static readonly string[] BoardOwn =
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
            string defence = Pick(Defences, rng).TrimEnd('?');
            // Now and then, point at someone else instead of just defending.
            if (others.Length > 0 && rng.Next(100) < 35)
                return (said.Alias, $"{defence}. what about @{others[rng.Next(others.Length)]} though");
            return (said.Alias, defence);
        }

        if (said.Text.Contains('?') && ctx.Persona.Count > 0 && rng.Next(100) < 30)
        {
            string? mine = OwnAnswer(ctx, rng);
            if (mine is not null) return (said.Alias, string.Format(Pick(BoardOwn, rng), mine));
        }

        return (said.Alias, said.Text.Contains('?') ? Pick(AnswersToQuestions, rng) : Pick(MentionReplies, rng));
    }

    private static (string? Mention, string? Body) FreeLine(ChatRequest request, BotVoice voice, Random rng)
    {
        BotContext ctx = request.Context;
        // ChatLine is a struct: cast first, or "none" would come back as an empty line instead of null.
        ChatLine? last = request.Recent
            .Where(l => !l.Alias.Equals(request.BotAlias, StringComparison.OrdinalIgnoreCase))
            .Select(l => (ChatLine?)l)
            .LastOrDefault();
        string[] pool = request.Policy.FamilyFriendly ? FamilyChat : [.. FamilyChat, .. MatureChat];

        int roll = rng.Next(100);

        // Talk about the answer board.
        if (roll < 30 && ctx.PublicBoard.Count > 0)
        {
            (string? mention, string? body) = BoardLine(request, voice, rng);
            if (body is not null) return (mention, body);
        }

        // React to the last thing someone said.
        if (roll < 55 && last is { } line)
            return (null, line.Text.Contains('?') ? Pick(AnswersToQuestions, rng) : Pick(Reactions, rng));

        // Accuse someone, with a reason. Leans towards whoever the room is already looking at.
        if (roll < 55 + voice.MentionPercent && request.OtherPlayers.Count > 0)
            return (MostTalkedAbout(request, rng), Pick(AccuseReasons, rng));

        return (null, Pick(pool, rng));
    }

    private static (string? Mention, string? Body) BoardLine(ChatRequest request, BotVoice voice, Random rng)
    {
        BotContext ctx = request.Context;
        BoardQuestion question = ctx.PublicBoard[rng.Next(ctx.PublicBoard.Count)];

        if (rng.Next(100) < 25)
        {
            string? mine = OwnAnswer(ctx, rng, question);
            if (mine is not null) return (null, string.Format(Pick(BoardOwn, rng), mine));
        }

        BoardAnswer[] others = question.Answers
            .Where(a => a.Answer.Length > 0 && request.OtherPlayers.Contains(a.Alias))
            .ToArray();
        if (others.Length == 0) return (null, null);

        BoardAnswer target = others[rng.Next(others.Length)];
        string quoted = Quote(target.Answer, question.Kind, rng);

        return rng.Next(100) < voice.MentionPercent * 2
            ? (target.Alias, string.Format(Pick(BoardAboutOthers, rng), quoted))
            : (null, string.Format(Pick(BoardAnonymous, rng), quoted));
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

    private static string? OwnAnswer(BotContext ctx, Random rng, BoardQuestion? question = null)
    {
        PersonaAnswer[] mine = ctx.Persona
            .Where(p => question is null || p.Question == question.Question)
            .Where(p => p.Kind == QuestionKind.Text ? !string.IsNullOrWhiteSpace(p.Text) : p.ChosenOptions.Count > 0)
            .ToArray();
        if (mine.Length == 0) return null;

        PersonaAnswer p = mine[rng.Next(mine.Length)];
        return p.Kind == QuestionKind.Text
            ? Quote(p.Text!, QuestionKind.Text, rng)
            : p.ChosenOptions[rng.Next(p.ChosenOptions.Count)].ToLowerInvariant();
    }

    /// <summary>
    /// Who to @mention: weighted by how often the others have mentioned or accused each player recently, so a bot
    /// tends to pile on like a person would, with some chance for anyone.
    /// </summary>
    private static string MostTalkedAbout(ChatRequest request, Random rng)
    {
        string[] players = request.OtherPlayers.ToArray();
        double[] weights = players.Select(p => 1.0 + Heat(p, request.Recent, request.BotAlias)).ToArray();
        return players[WeightedIndex(weights, rng)];
    }

    // ---------------------------------------------------------------- votes

    public Task EndRoundAsync(RoundEndRequest request, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Not a coin toss: each candidate is weighted by how much the room accused or mentioned them, and more if they
    /// accused this bot. Everyone keeps a base chance, so the vote is never fully predictable.
    /// </summary>
    public Task<Guid?> CastVoteAsync(VoteRequest request, CancellationToken ct)
    {
        if (request.Candidates.Count == 0) return Task.FromResult<Guid?>(null);

        double[] weights = request.Candidates.Select(c =>
        {
            double w = 1.0 + Heat(c.Alias, request.Recent, request.BotAlias);
            // Someone who accused me is a natural target.
            w += 2.0 * request.Recent.Count(l => l.Alias == c.Alias && Mentions(l.Text, request.BotAlias) && Accusing.IsMatch(l.Text));
            return w;
        }).ToArray();

        return Task.FromResult<Guid?>(request.Candidates[WeightedIndex(weights, Random.Shared)].Id);
    }

    /// <summary>How much the room has been talking about <paramref name="alias"/>: 2 per accusing mention, 1 per other mention.</summary>
    internal static double Heat(string alias, IReadOnlyList<ChatLine> recent, string botAlias) =>
        recent
            .Where(l => !l.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase) && !l.Alias.Equals(botAlias, StringComparison.OrdinalIgnoreCase))
            .Where(l => Mentions(l.Text, alias))
            .Sum(l => Accusing.IsMatch(l.Text) ? 2.0 : 1.0);

    private static bool Mentions(string text, string alias) => text.Contains("@" + alias, StringComparison.OrdinalIgnoreCase);

    private static int WeightedIndex(double[] weights, Random rng)
    {
        double roll = rng.NextDouble() * weights.Sum();
        for (int i = 0; i < weights.Length; i++)
        {
            roll -= weights[i];
            if (roll < 0) return i;
        }
        return weights.Length - 1;
    }

    // ---------------------------------------------------------------- helpers

    private static string Normal(string text) => new(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string Pick(string[] pool) => pool[RandomNumberGenerator.GetInt32(pool.Length)];

    private static string Pick(string[] pool, Random rng) => pool[rng.Next(pool.Length)];
}
