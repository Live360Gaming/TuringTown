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
    Task ChatMessage(Room room, ChatMessage message);

    /// <summary>How many of the required players have finished the current stage (answers or votes in).</summary>
    Task Progress(Room room, int done, int total);
}

/// <summary>
/// Sends to every connection in the room's SignalR group (the group is named after the room code).
/// </summary>
public sealed class SignalRRoomBroadcaster(IHubContext<GameHub> hub) : IRoomBroadcaster
{
    private IClientProxy Group(Room room) => hub.Clients.Group(room.Code.ToString());

    public Task StateChanged(Room room, GameState state) => Group(room).SendAsync("StateChanged", state);

    public Task RosterChanged(Room room) => Group(room).SendAsync("RosterChanged", room.GetRoster());

    public Task ChatMessage(Room room, ChatMessage message) => Group(room).SendAsync("ChatMessage", message);

    public Task Progress(Room room, int done, int total) => Group(room).SendAsync("Progress", new { done, total });
}
