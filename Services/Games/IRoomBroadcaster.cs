using Microsoft.AspNetCore.SignalR;
using TuringTown.Hubs;
using TuringTown.Models;

namespace TuringTown.Services.Games;

/// <summary>
/// How the game engine tells a room's clients what happened. An interface so tests can watch it.
/// </summary>
public interface IRoomBroadcaster
{
    Task StateChanged(Room room, GameState state);
    Task RosterChanged(Room room);

    /// <summary>The room's settings or limits changed (<see cref="RoomSnapshot"/>). Also sent with every roster change.</summary>
    Task RoomChanged(Room room);

    Task ChatMessage(Room room, ChatMessage message);

    /// <summary>A player (human or bot, indistinguishable) started or stopped typing. Only the id is sent.</summary>
    Task Typing(Room room, Guid playerId, bool typing);

    /// <summary>How many of the required players have finished the current stage (answers or votes in).</summary>
    Task Progress(Room room, int done, int total);

    Task GameError(Room room, string message);

    /// <summary>Tells one player something nobody else may know (see <see cref="SecretInfo"/>). Never sent to the group.</summary>
    Task Secret(Room room, Guid playerId, SecretInfo secret);
}

/// <summary>
/// Sends to every connection in the room's SignalR group (the group is named after the room code).
/// </summary>
public sealed class SignalRRoomBroadcaster(IHubContext<GameHub> hub) : IRoomBroadcaster
{
    private IClientProxy Group(Room room) => hub.Clients.Group(room.Code.ToString());

    public async Task StateChanged(Room room, GameState state)
    {
        await Group(room).SendAsync("StateChanged", state);
        // Back in the lobby the phase and player count (the bots have left) changed too.
        if (state.Stage == Stage.Lobby) await RoomChanged(room);
    }

    // The snapshot's player count follows the roster, so it goes along with every roster change.
    public async Task RosterChanged(Room room)
    {
        await Group(room).SendAsync("RosterChanged", room.GetRoster());
        await RoomChanged(room);
    }

    public Task RoomChanged(Room room) => Group(room).SendAsync("RoomChanged", room.GetSnapshot());

    public Task ChatMessage(Room room, ChatMessage message) => Group(room).SendAsync("ChatMessage", message);

    public Task Typing(Room room, Guid playerId, bool typing) => Group(room).SendAsync("Typing", new { playerId, typing });

    public Task Progress(Room room, int done, int total) => Group(room).SendAsync("Progress", new { done, total });

    public Task GameError(Room room, string message) => Group(room).SendAsync("GameError", message);

    public Task Secret(Room room, Guid playerId, SecretInfo secret) =>
        room.ConnectionOf(playerId) is { } connection ? hub.Clients.Client(connection).SendAsync("Secret", secret) : Task.CompletedTask;
}
