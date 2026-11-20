namespace TuringTown.Models;

/// <summary>
/// Character keys handed out to players. The client uses the same keys as texture names (see CHARACTER_KEYS in characters.js).
/// </summary>
public static class Characters
{
    // Only human-looking characters are handed out, to humans and bots alike. The robot art (robot_01..03 in the
    // client) is never assigned: it is only shown once a bot has been found, which is the whole point of the game.
    // With more seats than characters (up to 6 humans + 2 bots) some faces repeat; the aliases still differ.
    public static readonly string[] All =
        ["character_01", "character_02", "character_03", "character_04", "character_05", "character_06"];

    /// <summary>
    /// Picks a random character, preferring ones not in <paramref name="taken"/>.
    /// </summary>
    public static string PickRandom(IEnumerable<string> taken)
    {
        string[] free = All.Except(taken).ToArray();
        string[] pool = free.Length > 0 ? free : All;
        return pool[System.Security.Cryptography.RandomNumberGenerator.GetInt32(pool.Length)];
    }
}

/// <summary>
/// Aliases are a player's only visible identity in the game, a random "Color Fruit" such as "Purple Mango".
/// The color's hex value is sent too, so the client can tint the character.
/// </summary>
public static class Aliases
{
    private static readonly (string Name, string Hex)[] Colors =
    [
        ("Red", "#e53935"), ("Orange", "#fb8c00"), ("Yellow", "#fdd835"), ("Green", "#43a047"),
        ("Teal", "#00897b"), ("Blue", "#1e88e5"), ("Purple", "#8e24aa"), ("Pink", "#ec407a"),
        ("Brown", "#8d6e63"), ("Silver", "#b0bec5")
    ];

    private static readonly string[] Fruits =
    [
        "Apple", "Banana", "Mango", "Cherry", "Grape", "Kiwi", "Lemon", "Lime", "Melon", "Peach",
        "Pear", "Plum", "Papaya", "Fig", "Guava", "Berry"
    ];

    /// <summary>
    /// Picks a random alias that is not in <paramref name="taken"/>. Falls back to a numeric suffix if the
    /// random picks keep colliding (only possible with a very large room).
    /// </summary>
    public static (string Name, string Color) PickRandom(IEnumerable<string> taken)
    {
        HashSet<string> used = new(taken, StringComparer.OrdinalIgnoreCase);

        (string Name, string Color) pick = Roll();
        for (int attempt = 0; attempt < 50 && used.Contains(pick.Name); attempt++)
            pick = Roll();

        if (!used.Contains(pick.Name)) return pick;

        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{pick.Name} {suffix}";
            if (!used.Contains(candidate)) return (candidate, pick.Color);
        }
    }

    private static (string Name, string Color) Roll()
    {
        var color = Colors[System.Security.Cryptography.RandomNumberGenerator.GetInt32(Colors.Length)];
        string fruit = Fruits[System.Security.Cryptography.RandomNumberGenerator.GetInt32(Fruits.Length)];
        return ($"{color.Name} {fruit}", color.Hex);
    }
}
