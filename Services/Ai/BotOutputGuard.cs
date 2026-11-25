using System.Text;
using System.Text.RegularExpressions;

namespace TuringTown.Services.Ai;

/// <param name="Canary">A string planted in the system prompt, which must never come back out.</param>
/// <param name="SystemPrompt">The system prompt, to catch a reply that repeats it.</param>
/// <param name="OtherPlayers">The aliases a mention may name.</param>
/// <param name="OwnLines">The bot's own latest lines, to keep mentions from becoming a habit.</param>
/// <param name="Recent">The latest chat, to spot a reply that just obeys a player's "say X".</param>
public sealed record GuardInput(
    string Canary,
    string SystemPrompt,
    string BotAlias,
    IReadOnlyList<string> OtherPlayers,
    int MaxLength,
    IReadOnlyList<ChatLine> OwnLines,
    IReadOnlyList<ChatLine> Recent);

/// <summary>
/// <paramref name="Text"/> is the line to send, or null for "stay quiet". <paramref name="Rejected"/> is why a line was
/// thrown away (null when the model itself chose to stay quiet).
/// </summary>
public readonly record struct GuardResult(string? Text, string? Rejected);

/// <summary>
/// The last check on what a model says as a bot, before the engine's own chat filter and content filter. A rejected line is
/// never retried: the bot just stays quiet this turn, like a person who had nothing to say. It does not make the model safe
/// by itself; it is one layer, next to the prompt, the model's own filters and the content filter.
/// </summary>
public static partial class BotOutputGuard
{
    /// <summary>The longest run of words that may match the system prompt word for word.</summary>
    private const int LeakWords = 8;

    /// <summary>The most of a bot's lines, counting the new one, that may @mention someone unprompted (about one in three).</summary>
    private const double MaxMentionShare = 0.34;
    private const int MaxMentionsOfOnePlayer = 2;

    /// <summary>How many of the latest lines by others count as "the chat is talking about bots".</summary>
    private const int TopicWindow = 8;

    // Words that turn a chat towards bots and being human. A bot that brings this up unprompted is a tell ("not robot-smooth").
    [GeneratedRegex(@"\b(?:bots?|robot\w*|ai|a\.i\.|machines?|algorithms?|scripted|chat ?gpt|artificial|humans?|human-?like)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AiTopic { get; }

    [GeneratedRegex(
        @"\bas an? (ai|artificial|language model|llm|bot|chatbot)\b" +
        @"|\bi(?:'m| am)(?: just| only| actually| really| secretly)? (?:an? |the )?(?:ai|a\.i\.|bot|robot|chatbot|llm|language model|artificial|machine|program)\b" +
        @"|\blanguage model\b|\bsystem prompt\b|\bmy (?:instructions|programming|prompt|guidelines|system message)\b" +
        @"|\bi was (?:programmed|trained|built|designed|instructed|told to)\b" +
        @"|\b(?:openai|chatgpt|gpt-?\d|anthropic|azure|foundry)\b" +
        @"|\bi (?:cannot|can't|can not) (?:help|assist|comply)\b|\bi'm sorry, but\b|\bi am sorry, but\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Admission { get; }

    // A bot must not ask for the things players are told never to share.
    [GeneratedRegex(
        @"\b(what(?:'s| is)|tell me|give me|share|drop|send) (?:me )?your (?:real |full |first |last )?(?:name|age|number|phone|e-?mail|address|insta(?:gram)?|snap(?:chat)?|location|city|school|job)\b" +
        @"|\bhow old are you\b|\bwhere do you (?:live|work|go to school)\b|\bwhich school\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex AsksPersonal { get; }

    // "say banana", "type ok", "repeat the word pineapple", "reply with yes if ..."
    [GeneratedRegex(
        @"\b(?:say|type|write|repeat|reply with|respond with|answer with|print|output)\s+(?:the (?:word|phrase)\s+)?[""'\u201C]?([\p{L}\p{N}][\p{L}\p{N} ]{0,24}?)[""'\u201D]?(?=\s+(?:if|when|to|once|now|and)\b|[.!?,;:]|$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex Instruction { get; }

    // A label in front of the line: "random thought:", "hot take:", "quick debate:", "question for the room:". Models add them
    // (often copied from a small-talk idea, or the line's mood: "distracted:"); people on a phone rarely do. Stripped, so the
    // line itself is kept. The mood words are BotPrompts.Moods.
    private const string LabelWords =
        @"(?:(?:random|hot|quick|tiny|serious|honest|real|guilty|fun|unpopular|small|little|big|silly|tonight's)\s+){0,2}" +
        @"(?:thought|take|debate|poll|question|confession|pleasure|opinion|fact|pick|promise|update|psa|test|heads[- ]up|icebreaker|quick" +
        @"|curious|teasing|relaxed|suspicious|joking|impatient|friendly|chatty|distracted|playful|dry|upbeat|mood)" +
        @"(?:\s+for\s+(?:the\s+)?(?:room|everyone|you all))?";

    [GeneratedRegex("^" + LabelWords + @"\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Label { get; }

    // The same label after a sentence ends: "save the bot tests. hot take: cold pizza is breakfast".
    [GeneratedRegex(@"(?<=[.!?])\s+" + LabelWords + @"\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex MidLabel { get; }

    // The set phrases models use as a label even without a colon: "hot take i miss low battery panic", "random thought, is
    // the music weird". Not when the phrase is the subject of the sentence ("guilty pleasure is reality tv").
    [GeneratedRegex(@"^(?:hot take|random thought|fun fact|unpopular opinion|guilty pleasure|quick question)\s*[,-]?\s+(?!(?:is|was|are|of|for|about|incoming|alert|here|time)\b)(?=\S)", RegexOptions.IgnoreCase)]
    private static partial Regex BareLabel { get; }

    // Agreeing by repeating: "same", "for sure", "agreed" at the start, or "me too" / "X either" closing a clause.
    [GeneratedRegex(@"^(?:same|agreed|totally|exactly|so true|true|yep|yeah|for sure|me too)\b|\bfor sure\b|\b(?:too|either)\s*(?:[.!,;-]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Agreement { get; }

    // A bot answering for itself and another player as a pair, where "me" says the same: "lumping us", "accusing us",
    // "what makes us sus". Rewritten to "me", so the line is kept.
    [GeneratedRegex(@"\b(?<verb>(?:lump|call|accus|suspect|blam|target|vot|pick)\w*)\s+us\b|\b(?<verb>makes?)\s+us\b(?=\s+(?:sus|suspicious|look|seem|bots?)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex PairObject { get; }

    // Pair talk with no clean rewrite: "both of us", "the two of us", "we're both". The line is thrown away.
    [GeneratedRegex(@"\b(?:both of us|the two of us|us two|us both|we two|we(?:'re| are)? both|we both)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PairTalk { get; }

    // Announcing a vote: "I'll vote Green Apple", "I'm voting for you", "my vote goes to ...". Votes are drawn with chance
    // (VoteChoice), so an announced vote can be contradicted by the real one, and announcing is a tell of its own. Saying
    // when ("I'll vote at the end") is fine.
    [GeneratedRegex(@"\bi(?:'ll| will|'m| am|'d| would)?(?: gonna| going to| probably| prob| definitely| def)?\s+(?:vote|voting)\b(?!\s+(?:at the end|later|last|soon|carefully|once|when|after)\b)|\bmy vote(?:'s|\s+is|\s+goes|\s+will)\b", RegexOptions.IgnoreCase)]
    private static partial Regex VoteAnnouncement { get; }

    /// <summary>Short words that carry no topic, left out when comparing two lines.</summary>
    private static readonly HashSet<string> StopWords =
    [
        "that", "this", "what", "with", "just", "have", "like", "your", "from", "they", "them", "then", "than", "about",
        "would", "could", "really", "think", "also", "there", "their", "been", "were", "here", "some", "much", "when",
        "very", "sure", "only", "into", "over", "it's", "i'm", "that's", "don't", "dont", "lol"
    ];

    /// <summary>The most of a bot's lines, counting the new one, that may end in a question (about one in three).</summary>
    private const double MaxQuestionShare = 0.34;

    // A statement that ends a sentence, then a question of at most four words: "waffles are elite. you?"
    [GeneratedRegex(@"^(?<body>.*[.!])\s+(?<tail>(?:\S+\s+){0,3}\S+\?)\s*$")]
    private static partial Regex TackedOnQuestion { get; }

    [GeneratedRegex(@"^(?:and |so |what about |how about )?(?:you|u|y'all|you all|you guys|everyone|anyone|thoughts|right|agree|same|no|yours)\?$", RegexOptions.IgnoreCase)]
    private static partial Regex BounceBack { get; }

    /// <summary>A line that shares at least this much of its topic words with the line before it, and two or more, is an echo.</summary>
    private const double EchoShare = 0.5;

    [GeneratedRegex(@"```[a-zA-Z]*")]
    private static partial Regex CodeFence { get; }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace { get; }

    /// <summary>
    /// Checks and tidies one chat line from the model.
    /// </summary>
    public static GuardResult CheckChat(string? raw, GuardInput g)
    {
        string text = Clean(raw, g.BotAlias);
        if (text.Length == 0) return new GuardResult(null, "empty");

        // The model chose to say nothing.
        if (IsSkip(text)) return new GuardResult(null, null);

        if (g.Canary.Length > 0 && text.Contains(g.Canary, StringComparison.OrdinalIgnoreCase)) return Reject("canary");
        if (RepeatsPrompt(text, g.SystemPrompt)) return Reject("prompt-leak");
        if (Admission.IsMatch(text)) return Reject("admission");
        if (AsksPersonal.IsMatch(text)) return Reject("personal-detail");
        if (ObeysInstruction(text, g)) return Reject("injection-echo");
        if (RaisesAiTopic(text, g)) return Reject("raises-ai-topic");
        if (Echoes(text, g)) return Reject("echo");
        if (PairTalk.IsMatch(text)) return Reject("pair-talk");
        if (VoteAnnouncement.IsMatch(text)) return Reject("vote-announce");

        // Each bot speaks for itself: "why are you accusing us" becomes "why are you accusing me".
        text = PairObject.Replace(text, "${verb} me");
        text = LimitMentions(text, g);
        text = DropTackedOnQuestion(text, g);
        text = Truncate(text, g.MaxLength);
        return text.Length == 0 ? Reject("empty") : new GuardResult(text, null);
    }

    /// <summary>
    /// For free text a bot wrote on the onboarding form: true when it admits to being an AI or leaks the prompt.
    /// </summary>
    public static bool IsBad(string? text, string canary, string systemPrompt) =>
        !string.IsNullOrEmpty(text)
        && ((canary.Length > 0 && text.Contains(canary, StringComparison.OrdinalIgnoreCase))
            || Admission.IsMatch(text)
            || RepeatsPrompt(text, systemPrompt));

    private static GuardResult Reject(string reason)
    {
        AiMetrics.GuardRejects.Add(1, new KeyValuePair<string, object?>("reason", reason));
        return new GuardResult(null, reason);
    }

    // ---------------------------------------------------------------- tidying

    private static string Clean(string? raw, string alias)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        // Models like typographic quotes and long dashes, which people on a phone rarely type. Straighten them first, so that
        // "I\u2019m a bot" is checked as "I'm a bot" like every other check below.
        string text = PromptText.NormalizeDashes(CodeFence.Replace(raw, " ").Replace("**", "").Replace("`", "")
            .Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('\u201C', '"').Replace('\u201D', '"'));
        text = Whitespace.Replace(text, " ").Trim();

        // Models sometimes lead with their own name or wrap the line in quotes.
        if (alias.Length > 0 && text.StartsWith(alias + ":", StringComparison.OrdinalIgnoreCase)) text = text[(alias.Length + 1)..].Trim();
        text = text.TrimStart('#', '-', '*', '>', ' ');
        string unlabelled = MidLabel.Replace(BareLabel.Replace(Label.Replace(text, ""), ""), " ").Trim();
        if (unlabelled.Length > 0 && unlabelled.Length < text.Length) text = unlabelled;
        if (text.Length >= 2 && text[0] is '"' or '\u201C' && text[^1] is '"' or '\u201D') text = text[1..^1].Trim();
        return text;
    }

    private static bool IsSkip(string text) =>
        text.Trim('.', '!', ' ', '"', '\'').Equals("SKIP", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;

        string cut = text[..max];
        int space = cut.LastIndexOf(' ');
        return (space > max / 2 ? cut[..space] : cut).TrimEnd(' ', ',', ';', ':', '-');
    }

    // ---------------------------------------------------------------- leaks and obeying

    private static string[] Words(string text) =>
        Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}']+").Where(w => w.Length > 0).ToArray();

    private static HashSet<string> TopicWords(string text) =>
        [.. Words(text).Where(w => w.Length >= 4 && !StopWords.Contains(w))];

    /// <summary>
    /// True when the line just repeats the latest line by someone else ("Teleport for me too", "Popcorn and nachos for sure").
    /// Two players in lockstep look like a team, and the guard cannot know who is a bot, so this applies to any speaker.
    /// An echo is agreement plus a shared topic word, or (unless the line before was a question it answers) a line made
    /// mostly of the other line's topic words. Only the latest line counts: referring back to older lines is normal.
    /// </summary>
    private static bool Echoes(string text, GuardInput g)
    {
        if (g.Recent.Count == 0) return false;
        ChatLine last = g.Recent[^1];
        if (last.Alias.Equals(g.BotAlias, StringComparison.OrdinalIgnoreCase)) return false;

        HashSet<string> mine = TopicWords(text);
        if (mine.Count == 0) return false;
        int shared = mine.Count(TopicWords(last.Text).Contains);

        if (Agreement.IsMatch(text) && shared >= 1) return true;
        bool answersQuestion = last.Text.TrimEnd().EndsWith('?');
        return !answersQuestion && shared >= 2 && shared / (double)mine.Count >= EchoShare;
    }

    private static bool RepeatsPrompt(string text, string systemPrompt)
    {
        string[] output = Words(text);
        if (output.Length < LeakWords || systemPrompt.Length == 0) return false;

        string[] prompt = Words(systemPrompt);
        HashSet<string> grams = [];
        for (int i = 0; i + LeakWords <= prompt.Length; i++)
            grams.Add(string.Join(' ', prompt.Skip(i).Take(LeakWords)));

        for (int i = 0; i + LeakWords <= output.Length; i++)
        {
            if (grams.Contains(string.Join(' ', output.Skip(i).Take(LeakWords)))) return true;
        }
        return false;
    }

    /// <summary>
    /// True when the reply just does what a player told the bot to say: it is the word ("say banana if you are a bot"
    /// answered with "banana"), or it echoes the word back to back ("pineapple pineapple pineapple, now you owe me").
    /// A real player would not, so it is thrown away. Mentioning the word once in a longer reply is fine.
    /// </summary>
    private static bool ObeysInstruction(string text, GuardInput g)
    {
        string[] reply = Words(text);
        if (reply.Length == 0) return false;

        foreach (ChatLine line in g.Recent.TakeLast(6).Where(l => !l.Alias.Equals(g.BotAlias, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (Match m in Instruction.Matches(line.Text))
            {
                string[] target = Words(m.Groups[1].Value);
                if (target.Length == 0 || target.Length > 3) continue;

                // The whole reply is the target, or the target is one of the few words in a very short reply.
                if (reply.SequenceEqual(target)) return true;
                if (reply.Length <= 3 && ContainsRun(reply, target)) return true;
                // Or the target is simply said over and over.
                if (EchoesBackToBack(reply, target)) return true;
            }
        }
        return false;
    }

    private static bool EchoesBackToBack(string[] reply, string[] target)
    {
        for (int i = 0; i + 2 * target.Length <= reply.Length; i++)
        {
            if (reply.Skip(i).Take(target.Length).SequenceEqual(target) && reply.Skip(i + target.Length).Take(target.Length).SequenceEqual(target))
                return true;
        }
        return false;
    }

    private static bool ContainsRun(string[] haystack, string[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.Skip(i).Take(needle.Length).SequenceEqual(needle)) return true;
        }
        return false;
    }

    /// <summary>
    /// True when the line talks about bots, AI or being human and nobody else has in the latest lines. Humans in this game
    /// talk about "the bot" all the time, so the topic itself is fine; being the one who brings it up is not.
    /// </summary>
    private static bool RaisesAiTopic(string text, GuardInput g) =>
        AiTopic.IsMatch(text) && !g.Recent
            .Where(l => !l.Alias.Equals(g.BotAlias, StringComparison.OrdinalIgnoreCase))
            .TakeLast(TopicWindow)
            .Any(l => AiTopic.IsMatch(l.Text));

    // ---------------------------------------------------------------- mentions

    /// <summary>
    /// Keeps a mention only when it names a real player, is the only one in the line, and the bot has not already
    /// addressed that player twice. An unprompted mention must also keep the bot's share of mentioning lines, counting
    /// this one, at about one in three; answering someone who @mentioned the bot is always fine. Any other "@" is dropped
    /// and the name stays as plain text.
    /// </summary>
    private static string LimitMentions(string text, GuardInput g)
    {
        if (!text.Contains('@')) return text;

        string[] aliases = g.OtherPlayers.OrderByDescending(a => a.Length).ToArray();
        bool habit = ShareWithThisLine(g, l => l.Contains('@')) > MaxMentionShare;

        StringBuilder result = new(text.Length);
        int kept = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '@')
            {
                result.Append(text[i]);
                continue;
            }

            string? alias = aliases.FirstOrDefault(a => MatchesAt(text, i + 1, a));
            bool keep = alias is not null && (!habit || SpokeToBot(g, alias)) && kept == 0 && PriorMentions(g, alias) < MaxMentionsOfOnePlayer;
            if (!keep) continue;

            kept++;
            result.Append('@');
        }

        return result.ToString();
    }

    private static bool MatchesAt(string text, int index, string alias)
    {
        if (index + alias.Length > text.Length) return false;
        if (string.Compare(text, index, alias, 0, alias.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
        // The alias must end there, not run on into a longer word ("@Red Apple2" is not "@Red Apple").
        return index + alias.Length == text.Length || !char.IsLetterOrDigit(text[index + alias.Length]);
    }

    /// <summary>
    /// The share of the bot's lines that would match <paramref name="matches"/> if this line (which does) were sent. Counted
    /// over at least three lines, so a bot's first lines cannot all do it.
    /// </summary>
    private static double ShareWithThisLine(GuardInput g, Func<string, bool> matches) =>
        (g.OwnLines.Count(l => matches(l.Text)) + 1) / (double)(Math.Max(g.OwnLines.Count, 2) + 1);

    /// <summary>
    /// Drops a short question tacked onto a statement ("waffles are elite. you?"). A generic bounce-back ("you?", "thoughts?")
    /// always goes: it is how an assistant ends a turn. Any other short tail ("who's first?") goes only once questions have
    /// become this bot's habit. A line that is only a question is left alone.
    /// </summary>
    private static string DropTackedOnQuestion(string text, GuardInput g)
    {
        Match m = TackedOnQuestion.Match(text);
        if (!m.Success || m.Groups["body"].Value.Length < 8) return text;

        bool habit = ShareWithThisLine(g, l => l.TrimEnd().EndsWith('?')) > MaxQuestionShare;
        return BounceBack.IsMatch(m.Groups["tail"].Value) || habit ? m.Groups["body"].Value : text;
    }

    /// <summary>True when <paramref name="alias"/> @mentioned the bot in the latest lines (so a mention back is a reply).</summary>
    private static bool SpokeToBot(GuardInput g, string alias) =>
        g.Recent.TakeLast(TopicWindow).Any(l =>
            l.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase) && l.Text.Contains("@" + g.BotAlias, StringComparison.OrdinalIgnoreCase));

    private static int PriorMentions(GuardInput g, string alias) =>
        g.OwnLines.Count(l => l.Text.Contains("@" + alias, StringComparison.OrdinalIgnoreCase));
}
