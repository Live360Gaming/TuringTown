using System.Text.RegularExpressions;
using TuringTown.Models;

namespace TuringTown.Services.Games;

public enum ChatCheck
{
    Ok,
    Empty,
    TooLong,
    ContactInfo,
    IntroducesName
}

/// <summary>
/// Keeps the chat anonymous: players are only ever their alias. This blocks the obvious ways of leaking who
/// you are (contact details, links, introducing yourself). It cannot catch an arbitrary real name; that is
/// covered by the bots' instructions and the content filter. See docs/game-flow.md.
/// </summary>
public static partial class ChatFilter
{
    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.IgnoreCase)]
    private static partial Regex Email { get; }

    [GeneratedRegex(@"(https?://|www\.)\S+|\b[a-z0-9-]+\.(com|net|org|io|gg|co|me|ly|tv)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Link { get; }

    // Seven or more digits, allowing spaces, dots, dashes and brackets between them.
    [GeneratedRegex(@"(\+?\d[\s().-]*){7,}")]
    private static partial Regex Phone { get; }

    [GeneratedRegex(@"\b(my (real |full |first |last )?name is|name['’]s|i am called|i['’]?m called|call me)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NameIntro { get; }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace { get; }

    /// <summary>
    /// Trims and normalises <paramref name="text"/>, then checks it. <paramref name="cleaned"/> is what to post.
    /// </summary>
    public static ChatCheck Check(string? text, int maxLength, out string cleaned)
    {
        string value = new string((text ?? string.Empty).Where(c => !char.IsControl(c) || c == ' ').ToArray());
        cleaned = Whitespace.Replace(value, " ").Trim();

        if (cleaned.Length == 0) return ChatCheck.Empty;
        if (cleaned.Length > maxLength) return ChatCheck.TooLong;
        if (Email.IsMatch(cleaned) || Link.IsMatch(cleaned) || Phone.IsMatch(cleaned)) return ChatCheck.ContactInfo;
        if (NameIntro.IsMatch(cleaned)) return ChatCheck.IntroducesName;
        return ChatCheck.Ok;
    }

    /// <summary>
    /// A message the player can be shown for a failed check.
    /// </summary>
    public static string Describe(ChatCheck check) => check switch
    {
        ChatCheck.Empty => "Type something first.",
        ChatCheck.TooLong => "That message is too long.",
        ChatCheck.ContactInfo => "No contact details or links, stay anonymous.",
        ChatCheck.IntroducesName => "No names, you are only your alias here.",
        _ => "That message is not allowed."
    };
}

/// <summary>
/// Hook for content moderation (Azure AI Content Safety, see docs/content-policy.md). Applied to every chat
/// message, human or bot, with the room's policy.
/// </summary>
public interface IContentFilter
{
    Task<bool> IsAllowedAsync(string text, ContentPolicy policy, CancellationToken ct);
}

/// <summary>
/// Placeholder until a real content-safety service is wired in.
/// </summary>
public sealed class AllowAllContentFilter : IContentFilter
{
    public Task<bool> IsAllowedAsync(string text, ContentPolicy policy, CancellationToken ct) => Task.FromResult(true);
}
