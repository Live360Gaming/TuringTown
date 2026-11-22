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
/// Calls every game shares (pause, tutorial, continue) go through the room's <see cref="GameNight"/>; a game's own
/// calls go to its engine.
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
        games.TryGetNight(room, out GameNight? night);
        IReadOnlyList<ChatMessage> transcript = night?.GetTranscript() ?? [];
        IReadOnlyList<Guid> unmasked = night?.GetUnmasked() ?? [];

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
        else if (Context.Items.TryGetValue(RoleKey, out object? role) && role as string == "host"
                 && Context.Items.TryGetValue(RoomKey, out object? hr) && hr is Room hostRoom
                 && games.TryGetEngine(hostRoom, out IGameEngine? engine) && engine is { IsPaused: true })
        {
            // Only the host can resume, so a game it paused must not stay frozen after the host is gone.
            await engine.SetPausedAsync(false);
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
        if (games.TryGetNight(room, out GameNight? night) && night is not null)
            await night.NotifyPresenceChangedAsync();
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

    /// <summary>The host pauses (true) or resumes (false) the game: the stage clock stops and nobody can chat or vote.</summary>
    public async Task SetPaused(bool paused) => Throw(await RequireEngine(RequireHost()).SetPausedAsync(paused));

    /// <summary>Host reports which tutorial step is showing, so phones can follow along.</summary>
    public async Task TutorialStep(int step) => Throw(await RequireEngine(RequireHost()).TutorialStepAsync(step));

    /// <summary>The tutorial is over: the host says it finished (or skipped it), or the leader (the first player) skipped it.</summary>
    public Task TutorialDone()
    {
        Throw(RequireEngine(RequireHostOrLeader()).TutorialDone());
        return Task.CompletedTask;
    }

    /// <summary>
    /// The host, or the leader (the first player), moves the night on: past a game's results, past the scoreboard to
    /// the next game, or from the finale back to the lobby.
    /// </summary>
    public Task ContinueNight()
    {
        Throw(RequireNight(RequireHostOrLeader()).Continue());
        return Task.CompletedTask;
    }

    /// <summary>The older name of <see cref="ContinueNight"/>, kept for clients that still send it.</summary>
    public Task ReturnToLobby() => ContinueNight();

    // ---------------------------------------------------------------- players

    /// <summary>One answer per question: the written text, or the ids of the chosen options.</summary>
    public async Task SubmitOnboarding(OnboardingAnswer[] answers)
    {
        (Room room, Guid playerId) = RequirePlayer();
        Throw(await RequireEngine<TuringTownEngine>(room).SubmitOnboardingAsync(playerId, answers));
    }

    public async Task SendChat(string text)
    {
        (Room room, Guid playerId) = RequirePlayer();
        Throw(await RequireEngine<TuringTownEngine>(room).SendChatAsync(playerId, text));
    }

    /// <summary>The player started (true) or stopped (false) typing in the chat box. Shown to the room as a typing indicator.</summary>
    public async Task SetTyping(bool typing)
    {
        (Room room, Guid playerId) = RequirePlayer();
        await RequireEngine<TuringTownEngine>(room).SetTypingAsync(playerId, typing);
    }

    public async Task CastVote(Guid targetPlayerId)
    {
        (Room room, Guid playerId) = RequirePlayer();
        Throw(await RequireEngine<TuringTownEngine>(room).CastVoteAsync(playerId, targetPlayerId));
    }

    /// <summary>Witty Bits: write (or rewrite) the answer to one of your prompts.</summary>
    public async Task SubmitWittyAnswer(string promptId, string text)
    {
        (Room room, Guid playerId) = RequirePlayer();
        Throw(await RequireEngine<WittyBitsEngine>(room).SubmitAnswerAsync(playerId, promptId, text));
    }

    /// <summary>Witty Bits: vote for one answer in the current matchup (by its id, <c>a</c>, <c>b</c>, ...).</summary>
    public async Task CastWittyVote(string answerId)
    {
        (Room room, Guid playerId) = RequirePlayer();
        Throw(await RequireEngine<WittyBitsEngine>(room).CastVoteAsync(playerId, answerId));
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
            return room.IsLeader(playerId) ? room : throw new HubException("Only the host or the first player can do that.");
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

    private GameNight RequireNight(Room room) =>
        games.TryGetNight(room, out GameNight? night) && night is not null
            ? night
            : throw new HubException("The game has not started.");

    private IGameEngine RequireEngine(Room room) =>
        games.TryGetEngine(room, out IGameEngine? engine) && engine is not null
            ? engine
            : throw new HubException("The game has not started.");

    /// <summary>The running game, when it is the game a call belongs to.</summary>
    private T RequireEngine<T>(Room room) where T : class, IGameEngine =>
        RequireEngine(room) as T ?? throw new HubException("That is not part of this game.");

    private static void Throw(string? error)
    {
        if (error is not null) throw new HubException(error);
    }
}
