namespace TuringTown.Models;

public enum RoomPhase
{
    Lobby,
    InGame
}

public enum RoomJoinError
{
    None,
    RoomFull,
    GameStarted
}

public enum RoomSettingsError
{
    None,
    NotInLobby,
    BelowMinimum,
    AboveServerMaximum,
    BelowPlayerCount
}

/// <summary>
/// Session-level settings chosen by the host.
/// </summary>
public readonly record struct RoomSettings(int MaxPlayers, bool FamilyFriendly);

/// <summary>
/// A consistent view of a room, safe to serialize.
/// </summary>
public readonly record struct RoomSnapshot(
    RoomPhase Phase,
    int PlayerCount,
    RoomSettings Settings,
    int ServerMaxPlayers,
    int MinPlayers);

/// <summary>
/// A running game.
/// </summary>
public class Room(RoomCode rc, GameOptions options)
{
    /// <summary>
    /// The code used to join the room.
    /// </summary>
    public RoomCode Code { get; } = rc;

    /// <summary>
    /// Secret identifying the host. Required to change settings.
    /// </summary>
    public string HostToken { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// The players currently in the game.
    /// </summary>
    private readonly List<Player> _players = new();

    private RoomPhase _phase = RoomPhase.Lobby;

    // New sessions default to the configured maximum.
    private RoomSettings _settings = new(options.MaxPlayers, FamilyFriendly: true);

    /// <summary>
    /// The content policy other systems should apply to anything generated for this room.
    /// </summary>
    public ContentPolicy ContentPolicy
    {
        get
        {
            lock (this)
                return new ContentPolicy(_settings.FamilyFriendly);
        }
    }

    public bool IsHost(string? token) =>
        token is not null && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(token), System.Text.Encoding.UTF8.GetBytes(HostToken));

    public RoomSnapshot GetSnapshot()
    {
        lock (this)
            return new RoomSnapshot(_phase, _players.Count, _settings, options.MaxPlayers, options.MinPlayers);
    }

    /// <summary>
    /// Adds a new player if the room is in the lobby and below its player limit.
    /// </summary>
    public bool TryAddPlayer(out Player? player, out RoomJoinError error)
    {
        // The check and the add share one lock so the limit can never be exceeded by concurrent joins.
        lock (this)
        {
            if (_phase != RoomPhase.Lobby)
            {
                player = null;
                error = RoomJoinError.GameStarted;
                return false;
            }

            if (_players.Count >= _settings.MaxPlayers)
            {
                player = null;
                error = RoomJoinError.RoomFull;
                return false;
            }

            player = new Player { Id = Guid.NewGuid() };
            _players.Add(player);
            error = RoomJoinError.None;
            return true;
        }
    }

    /// <summary>
    /// Updates host settings. Existing players are never removed: the limit cannot drop below the current count.
    /// </summary>
    public RoomSettingsError TryUpdateSettings(int? maxPlayers, bool? familyFriendly)
    {
        lock (this)
        {
            if (_phase != RoomPhase.Lobby)
                return RoomSettingsError.NotInLobby;

            if (maxPlayers is { } max)
            {
                if (max > options.MaxPlayers) return RoomSettingsError.AboveServerMaximum;
                if (max < options.MinPlayers) return RoomSettingsError.BelowMinimum;
                if (max < _players.Count) return RoomSettingsError.BelowPlayerCount;
            }

            _settings = _settings with
            {
                MaxPlayers = maxPlayers ?? _settings.MaxPlayers,
                FamilyFriendly = familyFriendly ?? _settings.FamilyFriendly
            };
            return RoomSettingsError.None;
        }
    }
}
