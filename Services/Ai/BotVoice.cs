using System.Text;

namespace TuringTown.Services.Ai;

/// <summary>
/// How one scripted bot types: casing, end punctuation, pet fillers, the odd typo or emoji, and how chatty it is. It is
/// fixed for the whole game (seeded from the bot's id) and leans towards how the humans in the room wrote, so two bots
/// never sound like the same script and neither sounds unlike the room.
/// </summary>
public sealed class BotVoice
{
    private static readonly string[] FillerPool = ["ngl", "honestly", "ok but", "wait", "lol", "hmm", "tbh", "idk", "yeah", "bro", "nah but", "like"];
    private static readonly string[] EmojiPool = ["😂", "💀", "🤔", "😅", "👀", "🙃"];

    public bool Lowercase { get; }
    public bool DropEndPunctuation { get; }
    public bool UsesEmoji { get; }
    public int TypoPercent { get; }
    public int FillerPercent { get; }

    /// <summary>How often the bot lets a turn pass without writing (a quiet person reads more than they type).</summary>
    public int QuietPercent { get; }

    /// <summary>How often a free turn is spent @mentioning someone.</summary>
    public int MentionPercent { get; }

    private readonly string[] _fillers;

    private BotVoice(Random rng, double lowercaseShare, double punctuationRate, double emojiShare, bool hasSamples)
    {
        // Follow the room where it has written something, with a little personal variation.
        Lowercase = rng.NextDouble() < (hasSamples ? Math.Clamp(lowercaseShare, 0.15, 0.9) : 0.55);
        DropEndPunctuation = rng.NextDouble() < (hasSamples && punctuationRate > 0.03 ? 0.35 : 0.7);
        UsesEmoji = rng.NextDouble() < (hasSamples ? Math.Clamp(emojiShare * 1.5, 0.05, 0.8) : 0.15);
        TypoPercent = rng.Next(0, 9);
        FillerPercent = rng.Next(10, 35);
        QuietPercent = rng.Next(10, 40);
        MentionPercent = rng.Next(12, 30);
        _fillers = FillerPool.OrderBy(_ => rng.Next()).Take(2).ToArray();
    }

    /// <summary>The voice of one bot in one game. The same bot always gets the same voice.</summary>
    public static BotVoice For(Guid botId, StyleProfile style) =>
        new(new Random(Seed(botId)), style.LowercaseShare, style.PunctuationRate, style.EmojiShare, style.Samples.Count > 0);

    /// <summary>A voice for answering the form, from the humans' raw text answers.</summary>
    public static BotVoice For(Guid botId, IReadOnlyList<string> humanTexts)
    {
        string[] texts = humanTexts.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
        int chars = texts.Sum(t => t.Length);
        double lowercase = texts.Length == 0 ? 0 : texts.Count(t => !t.Any(char.IsUpper)) / (double)texts.Length;
        double punctuation = chars == 0 ? 0 : texts.Sum(t => t.Count(char.IsPunctuation)) / (double)chars;
        double emoji = texts.Length == 0 ? 0 : texts.Count(t => t.Any(char.IsSurrogate)) / (double)texts.Length;
        return new BotVoice(new Random(Seed(botId)), lowercase, punctuation, emoji, texts.Length > 0);
    }

    private static int Seed(Guid id) => BitConverter.ToInt32(id.ToByteArray(), 0) ^ BitConverter.ToInt32(id.ToByteArray(), 12);

    /// <summary>
    /// Writes <paramref name="text"/> the way this bot types. Put any "@Alias" mention in front of the result, never
    /// through here, so the alias stays exact. <paramref name="chat"/> allows fillers and emoji (not used on the form).
    /// </summary>
    public string Apply(string text, Random rng, bool chat = true)
    {
        string body = text.Trim();

        if (chat && body.Length > 0 && rng.Next(100) < FillerPercent && !body.Contains('?'))
            body = $"{_fillers[rng.Next(_fillers.Length)]} {char.ToLowerInvariant(body[0])}{body[1..]}";

        if (Lowercase) body = body.ToLowerInvariant();
        else if (body.Length > 0) body = char.ToUpperInvariant(body[0]) + body[1..];

        if (DropEndPunctuation) body = body.TrimEnd('.', '!');
        if (TypoPercent > 0 && rng.Next(100) < TypoPercent) body = Typo(body, rng);
        if (chat && UsesEmoji && rng.Next(100) < 20) body = $"{body} {EmojiPool[rng.Next(EmojiPool.Length)]}";

        return body;
    }

    /// <summary>Swaps two neighbouring letters in one longer word, like a thumb slipping.</summary>
    private static string Typo(string text, Random rng)
    {
        string[] words = text.Split(' ');
        int[] candidates = Enumerable.Range(0, words.Length).Where(i => words[i].Length > 4 && words[i].All(char.IsLetter)).ToArray();
        if (candidates.Length == 0) return text;

        int w = candidates[rng.Next(candidates.Length)];
        StringBuilder word = new(words[w]);
        int at = rng.Next(1, word.Length - 2);
        (word[at], word[at + 1]) = (word[at + 1], word[at]);
        words[w] = word.ToString();
        return string.Join(' ', words);
    }
}
