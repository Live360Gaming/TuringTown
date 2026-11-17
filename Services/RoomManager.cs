using System.Collections.Concurrent;
using TuringTown.Models;

namespace TuringTown.Services;

/// <summary>
/// Manages the currently running rooms.
/// </summary>
public class RoomManager
{
    private readonly ConcurrentDictionary<RoomCode, Room> _rooms = new();

    /// <summary>
    /// Gets or creates a new room using a room code.
    /// </summary>
    /// <param name="roomCode"></param>
    /// <returns></returns>
    public Room GetOrCreateRoom(RoomCode roomCode) => _rooms.GetOrAdd(roomCode, rc => new Room(rc));
}