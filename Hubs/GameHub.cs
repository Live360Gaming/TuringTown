using Microsoft.AspNetCore.SignalR;
using TuringTown.Models;
using TuringTown.Services;
using TuringTown.Services.Games;

namespace TuringTown.Hubs;

/// <summary>
/// Real-time channel for a room. Hosts and players authenticate with the tokens they were given over HTTP.
/// Each room is a SignalR group named after its room code.
///
/// Server to client: StateChanged(GameState), RosterChanged(PlayerInfo[]), ChatMessage(ChatMessage), Progress({done, total}).
/// Client to server: the methods below. See docs/game-flow.md.
/// </summary>
public sealed class GameHub(RoomManager roomManager, GameCoordinator games, IRoomBroadcaster broadcaster, TimeProvider time) : Hub
{
    private const string RoleKey = "role";
    private const string RoomKey = "room";
    private const string PlayerKey = "player";

    private Room FindRoom(string roomCode)
    {
        if (!RoomCode.TryFromString(roomCode, out RoomCode code) || !roomManager.TryGetRoom(code, out Room? room) || room is null)
            throw new HubException("Room not found.");
        return room;
    }

    private JoinResult BuildJoinResult(Room room, PlayerInfo? you)
    {
        games.TryGetEngine(room, out TuringTownEngine? engine);
        IReadOnlyList<ChatMessage> transcript = engine?.GetTranscript() ?? [];
        IReadOnlyList<Guid> unmasked = engine?.GetUnmasked() ?? [];

        return new JoinResult(room.State, room.GetRoster(), you, transcript, time.GetUtcNow().ToUnixTimeMilliseconds(), unmasked);
    }

    public async Task<JoinResult> JoinAsHost(string roomCode, string hostToken)
    {
        Room room = FindRoom(roomCode);
        if (!room.IsHost(hostToken))
            throw new HubException("Invalid host token.");

        Context.Items[RoleKey] = "host";
        Context.Items[RoomKey] = room;
        await Groups.AddToGroupAsync(Context.ConnectionId, room.Code.ToString());
        return BuildJoinResult(room, null);
    }

    public async Task<JoinResult> JoinAsPlayer(string roomCode, Guid playerId, string playerToken)
    {
        Room room = FindRoom(roomCode);
        if (!room.TryGetPlayer(playerId, playerToken, out Player? player) || player is null)
            throw new HubException("Invalid player.");

        Context.Items[RoleKey] = "player";
        Context.Items[RoomKey] = room;
        Context.Items[PlayerKey] = player.Id;
        await Groups.AddToGroupAsync(Context.ConnectionId, room.Code.ToString());

        room.SetConnected(player.Id, Context.ConnectionId);
        await broadcaster.RosterChanged(room);
        await NotifyPresence(room);

        PlayerInfo you = room.GetRoster().First(p => p.Id == player.Id);
        return BuildJoinResult(room, you);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(RoomKey, out object? r) && r is Room room
            && Context.Items.TryGetValue(PlayerKey, out object? p) && p is Guid playerId
            && room.SetDisconnected(playerId, Context.ConnectionId))
        {
            await broadcaster.RosterChanged(room);
            await NotifyPresence(room);

            // In the lobby the seat is only held for a short while (a refresh comes straight back).
            if (room.State.Stage == Stage.Lobby)
                _ = ClearFromLobbyAfterGraceAsync(room, playerId, room.GetDisconnectCount(playerId));
        }

        await base.OnDisconnectedAsync(exception);
    }

    private async Task ClearFromLobbyAfterGraceAsync(Room room, Guid playerId, int disconnectCount)
    {
        try
        {
            await Task.Delay(room.LobbyDisconnectGrace, time);
            bool wasLeader = room.IsLeader(playerId);
            if (!room.TryRemoveDisconnected(playerId, disconnectCount)) return;

            // The leader dropped out of the lobby for good: the game session ends with them.
            if (wasLeader)
                await CloseRoomAsync(room, "leader", exceptConnectionId: null);
            else
                await broadcaster.RosterChanged(room);
        }
        catch (Exception)
        {
            // Nothing to do: the seat stays and is cleared the next time the player drops.
        }
    }

    // A form or vote must not wait for a player who left.
    private async Task NotifyPresence(Room room)
    {
        if (games.TryGetEngine(room, out TuringTownEngine? engine) && engine is not null)
            await engine.NotifyPresenceChangedAsync();
    }

    // ---------------------------------------------------------------- host

    /// <summary>Starts the game. The host can, and so can the leader (the first player to join).</summary>
    public Task StartGame()
    {
        Room room = RequireHostOrLeader();

        string? error = games.StartGame(room) switch
        {
            RoomStartError.None => null,
            RoomStartError.NotInLobby => "The game has already started.",
            RoomStartError.NotEnoughPlayers => "Not enough players to start.",
            RoomStartError.NotAllReady => "Waiting for everyone to be ready.",
            _ => "Could not start the game."
        };
        return error is null ? Task.CompletedTask : throw new HubException(error);
    }

    /// <summary>
    /// The host or the leader quits: everyone else is told the room is closed, the game stops and the code stops working.
    /// </summary>
    public Task CloseRoom()
    {
        Room room = RequireHostOrLeader();
        string reason = Context.Items.TryGetValue(RoleKey, out object? role) && role as string == "host" ? "host" : "leader";
        return CloseRoomAsync(room, reason, Context.ConnectionId);
    }

    private async Task CloseRoomAsync(Room room, string reason, string? exceptConnectionId)
    {
        string group = room.Code.ToString();
        IClientProxy clients = exceptConnectionId is null ? Clients.Group(group) : Clients.GroupExcept(group, exceptConnectionId);
        await clients.SendAsync("RoomClosed", reason);
        roomManager.Remove(room.Code);
        games.StopGame(room);
    }

    /// <summary>Host reports which tutorial step is showing, so phones can follow along.</summary>
    public async Task TutorialStep(int step) => Throw(await RequireEngine(RequireHost()).TutorialStepAsync(step));

    /// <summary>Host says the tutorial is over (or skipped).</summary>
    public Task TutorialDone()
    {
        Throw(RequireEngine(RequireHost()).TutorialDone());
        return Task.CompletedTask;
    }

    /// <summary>The host, or the leader (the first player), leaves the results screen: everyone goes back to the lobby to play again.</summary>
    public Task ReturnToLobby()
    {
        Throw(RequireEngine(RequireHostOrLeader()).ReturnToLobby());
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- players

    /// <summary>One answer per question: the written text, or the ids of the chosen options.</summary>
    public async Task SubmitOnboarding(OnboardingAnswer[] answers)
    {
        (Room room, Guid playerId) = RequirePlayer();
        Throw(await RequireEngine(room).SubmitOnboardingAsync(playerId, answers));
    }

    public async Task SendChat(string text)
    {
        (Room room, Guid playerId) = RequirePlayer();
        Throw(await RequireEngine(room).SendChatAsync(playerId, text));
    }

    public async Task CastVote(Guid targetPlayerId)
    {
        (Room room, Guid playerId) = RequirePlayer();
        Throw(await RequireEngine(room).CastVoteAsync(playerId, targetPlayerId));
    }

    /// <summary>While waiting in the lobby, switch to a character nobody else has chosen.</summary>
    public async Task ChangeCharacter(string characterId)
    {
        (Room room, Guid playerId) = RequirePlayer();
        string? error = room.TryChangeCharacter(playerId, characterId) switch
        {
            CharacterChangeError.None => null,
            CharacterChangeError.NotInLobby => "Characters can only be changed in the lobby.",
            CharacterChangeError.Taken => "Someone else already picked that character.",
            _ => "Unknown character."
        };
        Throw(error);
        await broadcaster.RosterChanged(room);
    }

    /// <summary>While waiting in the lobby, mark yourself ready (or not).</summary>
    public async Task SetReady(bool ready)
    {
        (Room room, Guid playerId) = RequirePlayer();
        string? error = room.TrySetReady(playerId, ready) switch
        {
            ReadyError.None => null,
            ReadyError.NotInLobby => "You can only get ready in the lobby.",
            _ => "Join the room first."
        };
        Throw(error);
        await broadcaster.RosterChanged(room);
    }

    // ---------------------------------------------------------------- helpers

    private Room RequireHostOrLeader()
    {
        if (Context.Items.TryGetValue(RoleKey, out object? role) && role as string == "player"
            && Context.Items.TryGetValue(RoomKey, out object? r) && r is Room room
            && Context.Items.TryGetValue(PlayerKey, out object? p) && p is Guid playerId)
        {
            return room.IsLeader(playerId) ? room : throw new HubException("Only the host or the first player can start the game.");
        }
        return RequireHost();
    }

    private Room RequireHost()
    {
        if (!Context.Items.TryGetValue(RoleKey, out object? role) || role as string != "host"
            || !Context.Items.TryGetValue(RoomKey, out object? r) || r is not Room room)
            throw new HubException("Only the host can do that.");
        return room;
    }

    private (Room, Guid) RequirePlayer()
    {
        if (!Context.Items.TryGetValue(RoleKey, out object? role) || role as string != "player"
            || !Context.Items.TryGetValue(RoomKey, out object? r) || r is not Room room
            || !Context.Items.TryGetValue(PlayerKey, out object? p) || p is not Guid playerId)
            throw new HubException("Join the room first.");
        return (room, playerId);
    }

    private TuringTownEngine RequireEngine(Room room) =>
        games.TryGetEngine(room, out TuringTownEngine? engine) && engine is not null
            ? engine
            : throw new HubException("The game has not started.");

    private static void Throw(string? error)
    {
        if (error is not null) throw new HubException(error);
    }
}
