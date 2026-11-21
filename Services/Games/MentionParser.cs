using System.Text.RegularExpressions;
using TuringTown.Models;

namespace TuringTown.Services.Games;

/// <summary>Finds the players a chat line @mentions.</summary>
internal static class MentionParser
{
    /// <summary>
    /// The candidates a message @mentions: "@" followed by an alias exactly (any letter case). Unknown names are
    /// just text. At most <paramref name="max"/> count. Longer aliases are matched first, so "@Red Apple 2" is
    /// not read as "@Red Apple".
    /// </summary>
    public static IReadOnlyList<PlayerSeat> Find(string text, IEnumerable<PlayerSeat> candidates, int max)
    {
        string remaining = text;
        List<PlayerSeat> found = [];

        foreach (PlayerSeat seat in candidates.OrderByDescending(s => s.Alias.Length))
        {
            if (found.Count >= max) break;

            Match match = Regex.Match(remaining, $@"(?<![\w@])@{Regex.Escape(seat.Alias)}(?!\w)", RegexOptions.IgnoreCase);
            if (!match.Success) continue;

            found.Add(seat);
            remaining = remaining.Remove(match.Index, match.Length).Insert(match.Index, new string(' ', match.Length));
        }

        return found;
    }
}
