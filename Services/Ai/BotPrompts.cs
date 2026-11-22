using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using TuringTown.Models;

namespace TuringTown.Services.Ai;

/// <summary>
/// The prompt templates, which are embedded files in <c>Services/Ai/Prompts</c> so they are versioned with the code and
/// reviewed like it. Each starts with a <c>version: N</c> line (logged with every call) and uses <c>{{placeholders}}</c>.
/// The Family Friendly setting picks the tone text; the mandatory safety rules are in every prompt.
/// </summary>
public sealed partial class BotPrompts
{
    public sealed record Template(string Name, int Version, string Text);

    [GeneratedRegex(@"\{\{([a-z_]+)\}\}")]
    private static partial Regex Placeholder { get; }

    private static readonly string[] Moods =
    [
        "Mood for this line: curious.", "Mood for this line: a bit teasing.", "Mood for this line: relaxed.",
        "Mood for this line: suspicious.", "Mood for this line: joking.", "Mood for this line: matter-of-fact.",
        "Mood for this line: a little impatient.", "Mood for this line: friendly.", "Mood for this line: chatty.",
        "Mood for this line: distracted.", "Mood for this line: playful.", "Mood for this line: dry.", "Mood for this line: upbeat."
    ];

    private readonly Dictionary<string, Template> _templates;
    private readonly string[] _smallTalk;

    public BotPrompts()
    {
        Assembly assembly = typeof(BotPrompts).Assembly;
        _templates = [];
        foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.Contains(".Prompts.") && n.EndsWith(".md")))
        {
            using StreamReader reader = new(assembly.GetManifestResourceStream(resource)!);
            string name = resource[(resource.IndexOf(".Prompts.", StringComparison.Ordinal) + ".Prompts.".Length)..^".md".Length];
            _templates[name] = Parse(name, reader.ReadToEnd());
        }

        // small-talk.md is a list ("- idea" per line), not a template with placeholders.
        _smallTalk = Get("small-talk").Text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("- ", StringComparison.Ordinal))
            .Select(l => l[2..].Trim())
            .Where(l => l.Length > 0)
            .ToArray();
        if (_smallTalk.Length == 0) throw new InvalidOperationException("Prompt small-talk must list at least one idea.");
    }

    /// <summary>The small-talk ideas from small-talk.md.</summary>
    public IReadOnlyList<string> SmallTalkIdeas => _smallTalk;

    /// <summary>
    /// One small-talk idea at random. A reasoning model has no temperature, so without a random seed from here every bot
    /// in every game would open the chat the same way.
    /// </summary>
    public string RandomSmallTalk() => _smallTalk[RandomNumberGenerator.GetInt32(_smallTalk.Length)];

    public IReadOnlyCollection<Template> All => _templates.Values;

    public Template Get(string name) =>
        _templates.TryGetValue(name, out Template? t) ? t : throw new InvalidOperationException($"There is no prompt template called {name}.");

    /// <summary>
    /// Fills a template. Every placeholder in it must be given, and every value given must be used, so a typo cannot
    /// silently send a half-empty prompt.
    /// </summary>
    public string Render(string name, IReadOnlyDictionary<string, string> values)
    {
        Template template = Get(name);
        HashSet<string> used = [];

        string text = Placeholder.Replace(template.Text, m =>
        {
            string key = m.Groups[1].Value;
            if (!values.TryGetValue(key, out string? value)) throw new InvalidOperationException($"Prompt {name} needs a value for {{{{{key}}}}}.");
            used.Add(key);
            return value;
        });

        string? unused = values.Keys.FirstOrDefault(k => !used.Contains(k));
        if (unused is not null) throw new InvalidOperationException($"Prompt {name} has no placeholder {{{{{unused}}}}}.");
        return text;
    }

    /// <summary>The tone text for the room's Family Friendly setting.</summary>
    public string Tone(ContentPolicy policy) => Get(policy.FamilyFriendly ? "tone-family" : "tone-mature").Text;

    public static string RandomMood() => Moods[RandomNumberGenerator.GetInt32(Moods.Length)];

    private static Template Parse(string name, string raw)
    {
        string text = raw.Replace("\r\n", "\n");
        int newline = text.IndexOf('\n');
        string first = newline < 0 ? text : text[..newline];
        if (!first.StartsWith("version:", StringComparison.Ordinal) || !int.TryParse(first["version:".Length..].Trim(), out int version))
            throw new InvalidOperationException($"Prompt {name} must start with a 'version: N' line.");

        return new Template(name, version, text[(newline + 1)..].Trim());
    }
}
