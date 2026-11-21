namespace TuringTown.Models;

/// <summary>
/// A seat in a room: a human or an AI bot. This is server-internal and must never be serialized to clients,
/// because <see cref="IsBot"/> is the secret the game is about. Clients only ever see <see cref="PlayerInfo"/>.
/// </summary>
public class Player
{
    /// <summary>
    /// The unique ID of this player in the game.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Secret identifying this player. Required to connect to the game hub. Empty for bots.
    /// </summary>
    public required string Token { get; init; }

    /// <summary>
    /// The character (see <see cref="Characters"/>) randomly assigned on join. Humans may swap it in the lobby (see <see cref="Room.TryChangeCharacter"/>).
    /// </summary>
    public required string CharacterId { get; set; }

    /// <summary>
    /// The random "Color Fruit" name this player is known by.
    /// </summary>
    public required string Alias { get; init; }

    /// <summary>
    /// Hex color matching the alias, used to tint the character.
    /// </summary>
    public required string Color { get; init; }

    /// <summary>
    /// True for AI players. Never exposed until the reveal.
    /// </summary>
    public bool IsBot { get; init; }

    /// <summary>
    /// False once voted out. Eliminated humans become spectators.
    /// </summary>
    public bool Alive { get; set; } = true;

    /// <summary>
    /// The hub connection currently representing this player, if any. Only changed through <see cref="Room"/>.
    /// </summary>
    public string? ConnectionId { get; set; }

    public bool Connected { get; set; }

    /// <summary>
    /// How many times this player has dropped. A pending "clear them from the lobby" timer only acts if this
    /// has not changed since it started, so a quick reconnect-and-drop is not cleared early.
    /// </summary>
    public int DisconnectCount { get; set; }

    /// <summary>
    /// The lobby "I'm ready" flag. Humans only; cleared when the room returns to the lobby.
    /// </summary>
    public bool Ready { get; set; }
}
