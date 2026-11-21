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
        c is >= '​' and <= '‏'    // zero-width space, joiners, direction marks
            or >= '‪' and <= '‮'  // bidi embedding and override
            or >= '⁠' and <= '⁤'  // word joiner and invisible operators
            or >= '⁦' and <= '⁩'  // bidi isolates
            or '﻿'                      // byte order mark
        || (char.IsControl(c) && !char.IsWhiteSpace(c))
        || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format;
}
