using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace TuringTown.Services.Games;

/// <summary>One recorded line of narration, as <c>game/src/narration/manifest.json</c> describes it.</summary>
public sealed record NarrationEntry(string Key, string Category, string File, string Title, string Transcript, int DurationMs);

/// <summary>
/// The recorded narration. The TV plays it; the server only needs the same list (embedded from the client's file, so
/// they cannot drift) to pick the goodbye line every screen shows.
/// </summary>
public static class NarrationManifest
{
    public const string Thanks = "goodbye_thanks";

    public static IReadOnlyList<NarrationEntry> Entries { get; } = Load();

    /// <summary>The lines that can follow "Thanks for playing!".</summary>
    public static IReadOnlyList<string> Goodbyes { get; } =
        Entries.Where(e => e.Category == "goodbye" && e.Key != Thanks).Select(e => e.Key).ToArray();

    /// <summary>One goodbye line at random, never the one the room heard last time.</summary>
    public static string PickGoodbye(string? last)
    {
        string[] pool = Goodbyes.Where(k => k != last).ToArray();
        return pool[RandomNumberGenerator.GetInt32(pool.Length)];
    }

    private static NarrationEntry[] Load()
    {
        Assembly assembly = typeof(NarrationManifest).Assembly;
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".narration.manifest.json", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<NarrationEntry[]>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
}
