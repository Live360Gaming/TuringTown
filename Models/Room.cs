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

public enum RoomStartError
{
    None,
    NotInLobby,
    NotEnoughPlayers,
    NotAllReady
}

public enum ReadyError
{
    None,
    NotInLobby,
    UnknownPlayer
}

public enum CharacterChangeError
{
    None,
    NotInLobby,
    UnknownCharacter,
    Taken
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
    /// The only game that exists so far. Clients use it to pick the right scenes.
    /// </summary>
    public const string GameId = "turing-town";

    /// <summary>
    /// The code used to join the room.
    /// </summary>
    public RoomCode Code { get; } = rc;

    /// <summary>
    /// Secret identifying the host. Required to change settings.
    /// </summary>
    public string HostToken { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// The players currently in the game. Humans join in the lobby, bots are added when the game starts.
    /// </summary>
    private readonly List<Player> _players = new();

    private GameState _state = GameState.InLobby(GameId);

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

    /// <summary>
    /// The current stage of the game.
    /// </summary>
    public GameState State
    {
        get
        {
            lock (this)
                return _state;
        }
    }

    public bool IsHost(string? token) =>
        token is not null && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(token), System.Text.Encoding.UTF8.GetBytes(HostToken));

    public RoomSnapshot GetSnapshot()
    {
        lock (this)
            return new RoomSnapshot(PhaseOf(_state), _players.Count, _settings, options.MaxPlayers, options.MinPlayers);
    }

    private static RoomPhase PhaseOf(GameState state) => state.Stage == Stage.Lobby ? RoomPhase.Lobby : RoomPhase.InGame;

    /// <summary>
    /// Adds a new player if the room is in the lobby and below its player limit.
    /// </summary>
    public bool TryAddPlayer(out Player? player, out RoomJoinError error)
    {
        // The check and the add share one lock so the limit can never be exceeded by concurrent joins.
        lock (this)
        {
            if (_state.Stage != Stage.Lobby)
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

            player = NewPlayer(isBot: false);
            _players.Add(player);
            error = RoomJoinError.None;
            return true;
        }
    }

    // Must be called with the lock held.
    private Player NewPlayer(bool isBot)
    {
        var (alias, color) = Aliases.PickRandom(_players.Select(p => p.Alias));
        return new Player
        {
            Id = Guid.NewGuid(),
            Token = isBot ? string.Empty : Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
            CharacterId = Characters.PickRandom(_players.Select(p => p.CharacterId)),
            Alias = alias,
            Color = color,
            IsBot = isBot,
            // Bots are always "online"; there is no connection to lose.
            Connected = isBot
        };
    }

    /// <summary>
    /// Looks up a player by id, verifying their secret token.
    /// </summary>
    public bool TryGetPlayer(Guid id, string? token, out Player? player)
    {
        lock (this)
        {
            player = _players.FirstOrDefault(p => p.Id == id && !p.IsBot);
            if (player is null || token is null || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(token), System.Text.Encoding.UTF8.GetBytes(player.Token)))
            {
                player = null;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Lets a player in the lobby switch to a character nobody else has chosen. Choosing your own is a no-op.
    /// </summary>
    public CharacterChangeError TryChangeCharacter(Guid playerId, string characterId)
    {
        lock (this)
        {
            if (_state.Stage != Stage.Lobby) return CharacterChangeError.NotInLobby;
            if (!Characters.All.Contains(characterId)) return CharacterChangeError.UnknownCharacter;

            Player? player = _players.FirstOrDefault(p => p.Id == playerId && !p.IsBot);
            if (player is null) return CharacterChangeError.UnknownCharacter;
            if (player.CharacterId == characterId) return CharacterChangeError.None;
            if (_players.Any(p => p.CharacterId == characterId)) return CharacterChangeError.Taken;

            player.CharacterId = characterId;
            return CharacterChangeError.None;
        }
    }

    /// <summary>
    /// Sets a player's lobby ready flag.
    /// </summary>
    public ReadyError TrySetReady(Guid playerId, bool ready)
    {
        lock (this)
        {
            if (_state.Stage != Stage.Lobby) return ReadyError.NotInLobby;

            Player? player = _players.FirstOrDefault(p => p.Id == playerId && !p.IsBot);
            if (player is null) return ReadyError.UnknownPlayer;

            player.Ready = ready;
            return ReadyError.None;
        }
    }

    /// <summary>
    /// True if this player is the leader: the first human to have joined, who may start the game.
    /// </summary>
    public bool IsLeader(Guid playerId)
    {
        lock (this)
            return _players.FirstOrDefault(p => !p.IsBot)?.Id == playerId;
    }

    /// <summary>
    /// Moves the room out of the lobby once enough humans have joined and all are ready, and seats the bots.
    /// </summary>
    /// <param name="botCountFor">How many bots to add for the given number of humans.</param>
    public RoomStartError TryStart(Func<int, int> botCountFor)
    {
        lock (this)
        {
            if (_state.Stage != Stage.Lobby) return RoomStartError.NotInLobby;

            int humans = _players.Count;
            if (humans < options.MinPlayers) return RoomStartError.NotEnoughPlayers;
            if (_players.Any(p => !p.Ready)) return RoomStartError.NotAllReady;

            int bots = botCountFor(humans);
            for (int i = 0; i < bots; i++)
                _players.Add(NewPlayer(isBot: true));

            _state = new GameState(GameId, Stage.Tutorial, 0, 0, null, null);
            return RoomStartError.None;
        }
    }

    /// <summary>
    /// Replaces the current state and returns it.
    /// </summary>
    internal GameState SetState(GameState state)
    {
        lock (this)
        {
            _state = state;
            return state;
        }
    }

    /// <summary>
    /// Changes the state only if the room is still in <paramref name="expected"/>, so a late call can never
    /// overwrite a stage the game has already moved on from.
    /// </summary>
    internal bool TryUpdateState(Stage expected, Func<GameState, GameState> change, out GameState state)
    {
        lock (this)
        {
            if (_state.Stage != expected)
            {
                state = _state;
                return false;
            }

            _state = change(_state);
            state = _state;
            return true;
        }
    }

    /// <summary>
    /// Everyone in the room, sorted by alias so that seat order never gives a bot away.
    /// </summary>
    public IReadOnlyList<PlayerInfo> GetRoster()
    {
        lock (this)
            return _players.OrderBy(p => p.Alias, StringComparer.Ordinal).Select(ToSeat).Select(s => s.ToInfo()).ToArray();
    }

    /// <summary>
    /// A server-side copy of every seat, including the secret role.
    /// </summary>
    internal IReadOnlyList<PlayerSeat> GetSeats()
    {
        lock (this)
            return _players.OrderBy(p => p.Alias, StringComparer.Ordinal).Select(ToSeat).ToArray();
    }

    internal bool TryGetSeat(Guid id, out PlayerSeat seat)
    {
        lock (this)
        {
            Player? player = _players.FirstOrDefault(p => p.Id == id);
            seat = player is null ? default : ToSeat(player);
            return player is not null;
        }
    }

    // Must be called with the lock held (the leader is the first human in join order).
    private PlayerSeat ToSeat(Player p) => new(p.Id, p.Alias, p.Color, p.CharacterId, p.IsBot, p.Alive, p.Connected,
        p.Ready, !p.IsBot && _players.FirstOrDefault(x => !x.IsBot) == p);

    /// <summary>
    /// Marks the player as connected through the given hub connection (a newer connection replaces an older one).
    /// </summary>
    public void SetConnected(Guid playerId, string connectionId)
    {
        lock (this)
        {
            Player? player = _players.FirstOrDefault(p => p.Id == playerId);
            if (player is null) return;
            player.ConnectionId = connectionId;
            player.Connected = true;
        }
    }

    /// <summary>
    /// Marks the player as disconnected, but only if <paramref name="connectionId"/> is still their current
    /// connection (a refresh opens the new connection before the old one reports closing).
    /// </summary>
    /// <returns>True if the roster changed.</returns>
    public bool SetDisconnected(Guid playerId, string connectionId)
    {
        lock (this)
        {
            Player? player = _players.FirstOrDefault(p => p.Id == playerId);
            if (player is null || player.IsBot || player.ConnectionId != connectionId) return false;
            player.ConnectionId = null;
            player.Connected = false;
            player.DisconnectCount++;
            return true;
        }
    }

    /// <summary>
    /// How long a player who drops out of the lobby keeps their seat.
    /// </summary>
    public TimeSpan LobbyDisconnectGrace => TimeSpan.FromSeconds(options.LobbyDisconnectGraceSeconds);

    /// <summary>
    /// The player's current drop count (see <see cref="Player.DisconnectCount"/>).
    /// </summary>
    public int GetDisconnectCount(Guid playerId)
    {
        lock (this)
            return _players.FirstOrDefault(p => p.Id == playerId)?.DisconnectCount ?? 0;
    }

    /// <summary>
    /// Frees the seat of a human who dropped out in the lobby and has not come back since
    /// (<paramref name="disconnectCount"/> is the drop the timer was started for).
    /// </summary>
    /// <returns>True if the player was removed.</returns>
    public bool TryRemoveDisconnected(Guid playerId, int disconnectCount)
    {
        lock (this)
        {
            if (_state.Stage != Stage.Lobby) return false;

            Player? player = _players.FirstOrDefault(p => p.Id == playerId && !p.IsBot);
            if (player is null || player.Connected || player.DisconnectCount != disconnectCount) return false;

            _players.Remove(player);
            return true;
        }
    }

    /// <summary>
    /// Votes a player out. They stay in the room as a spectator.
    /// </summary>
    internal void Eliminate(Guid playerId)
    {
        lock (this)
        {
            Player? player = _players.FirstOrDefault(p => p.Id == playerId);
            if (player is not null) player.Alive = false;
        }
    }

    /// <summary>
    /// Ends the game: bots leave, everyone is alive again and the room is back in the lobby.
    /// </summary>
    internal void ResetToLobby()
    {
        lock (this)
        {
            _players.RemoveAll(p => p.IsBot);
            foreach (Player player in _players)
            {
                player.Alive = true;
                player.Ready = false;
            }
            _state = GameState.InLobby(GameId);
        }
    }

    /// <summary>
    /// Updates host settings. Existing players are never removed: the limit cannot drop below the current count.
    /// </summary>
    public RoomSettingsError TryUpdateSettings(int? maxPlayers, bool? familyFriendly)
    {
        lock (this)
        {
            if (_state.Stage != Stage.Lobby)
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
