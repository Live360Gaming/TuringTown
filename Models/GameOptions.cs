using System.ComponentModel.DataAnnotations;

namespace TuringTown.Models;

/// <summary>
/// Authoritative game limits, bound from the "Game" section of appsettings.json.
/// </summary>
public sealed class GameOptions : IValidatableObject
{
    public const string SectionName = "Game";

    /// <summary>
    /// The absolute maximum number of players in a session. The host can lower a session's limit but never exceed this.
    /// A full room (these humans plus the night's bots) must fit the character art; checked on start in Program.cs.
    /// </summary>
    [Range(1, 1000)]
    public int MaxPlayers { get; set; } = 4;

    /// <summary>
    /// The minimum number of players needed to play. Also the lowest limit the host may select.
    /// </summary>
    [Range(1, 1000)]
    public int MinPlayers { get; set; } = 2;

    /// <summary>
    /// How long a player who drops out of the lobby keeps their seat. A page refresh or a brief signal loss
    /// comes back within this time; after it, the seat is freed. Only applies in the lobby.
    /// </summary>
    [Range(1, 3600)]
    public int LobbyDisconnectGraceSeconds { get; set; } = 15;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MaxPlayers < MinPlayers)
            yield return new ValidationResult("MaxPlayers must be greater than or equal to MinPlayers.",
                [nameof(MaxPlayers), nameof(MinPlayers)]);
    }
}
