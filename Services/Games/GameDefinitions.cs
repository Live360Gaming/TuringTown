using System.Reflection;
using System.Text.Json;

namespace TuringTown.Services.Games;

/// <summary>
/// Which mini games a game night may play. Each game's definition, <c>game/src/minigames/&lt;id&gt;/data/game.json</c>,
/// is embedded in the server, and its <c>"enabled"</c> flag (true when missing) turns the game on or off. A game
/// with no definition is enabled.
/// </summary>
public sealed class GameDefinitions
{
    private readonly IReadOnlyDictionary<string, bool> _enabled;

    public GameDefinitions(IReadOnlyDictionary<string, bool> enabled) => _enabled = enabled;

    /// <summary>The flags from the embedded game.json files.</summary>
    public static GameDefinitions FromEmbeddedData()
    {
        Assembly assembly = typeof(GameDefinitions).Assembly;
        Dictionary<string, bool> enabled = new(StringComparer.Ordinal);
        foreach (string resource in assembly.GetManifestResourceNames().Where(n => n.Contains(".minigames.") && n.EndsWith(".game.json")))
        {
            using Stream stream = assembly.GetManifestResourceStream(resource)!;
            using JsonDocument doc = JsonDocument.Parse(stream);
            JsonElement root = doc.RootElement;
            string id = root.GetProperty("id").GetString() ?? throw new InvalidOperationException($"{resource} has no id.");
            enabled[id] = !root.TryGetProperty("enabled", out JsonElement flag) || flag.GetBoolean();
        }
        return new GameDefinitions(enabled);
    }

    /// <summary>Every game enabled, whatever the data says (for tests).</summary>
    public static GameDefinitions AllEnabled() => new(new Dictionary<string, bool>());

    public bool IsEnabled(string gameId) => _enabled.GetValueOrDefault(gameId, true);
}
