using System.Globalization;
using System.Text;

namespace TuringTown.Services.Ai;

/// <summary>
/// Cleans text typed by a player before it is shown to a model. Players can type anything, so this removes what is
/// used to hide or fake instructions: control, zero-width and direction-changing characters, and line breaks. It does
/// not make the text safe by itself; it must still only be passed as quoted data, never merged into instructions.
/// </summary>
public static class PromptText
{
    public static string Sanitize(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";

        StringBuilder sb = new(Math.Min(text.Length, maxLength));
        bool lastWasSpace = false;
        foreach (char c in text)
        {
            if (IsInvisible(c)) continue;

            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (lastWasSpace || sb.Length == 0) continue;
                sb.Append(' ');
                lastWasSpace = true;
                continue;
            }

            sb.Append(c);
            lastWasSpace = false;
            if (sb.Length >= maxLength) break;
        }

        return sb.ToString().Trim();
    }

    private static bool IsInvisible(char c) =>
        c is >= '\u200B' and <= '\u200F'    // zero-width space, joiners, direction marks
            or >= '\u202A' and <= '\u202E'  // bidi embedding and override
            or >= '\u2060' and <= '\u2064'  // word joiner and invisible operators
            or >= '\u2066' and <= '\u2069'  // bidi isolates
            or '\uFEFF'                      // byte order mark
        || (char.IsControl(c) && !char.IsWhiteSpace(c))
        || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format;
}
