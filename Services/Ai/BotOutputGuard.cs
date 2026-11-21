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

    private const double MaxMentionShare = 0.4;
    private const int MaxMentionsOfOnePlayer = 2;

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

        text = LimitMentions(text, g);
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

    // ---------------------------------------------------------------- mentions

    /// <summary>
    /// Keeps a mention only when it names a real player, is the only one in the line, is not becoming a habit, and the
    /// bot has not already addressed that player twice. Any other "@" is dropped and the name stays as plain text.
    /// </summary>
    private static string LimitMentions(string text, GuardInput g)
    {
        if (!text.Contains('@')) return text;

        string[] aliases = g.OtherPlayers.OrderByDescending(a => a.Length).ToArray();
        bool habit = g.OwnLines.Count >= 3 && g.OwnLines.Count(l => l.Text.Contains('@')) / (double)g.OwnLines.Count > MaxMentionShare;

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
            bool keep = alias is not null && !habit && kept == 0 && PriorMentions(g, alias) < MaxMentionsOfOnePlayer;
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

    private static int PriorMentions(GuardInput g, string alias) =>
        g.OwnLines.Count(l => l.Text.Contains("@" + alias, StringComparison.OrdinalIgnoreCase));
}
