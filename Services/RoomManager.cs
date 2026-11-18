using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using TuringTown.Models;

namespace TuringTown.Services;

/// <summary>
/// Manages the currently running rooms.
/// </summary>
public class RoomManager(IOptions<GameOptions> options)
{
    private readonly ConcurrentDictionary<RoomCode, Room> _rooms = new();

    /// <summary>
    /// Creates a new room with the given code, or returns the existing one.
    /// </summary>
    /// <param name="roomCode"></param>
    /// <returns></returns>
    public Room GetOrCreateRoom(RoomCode roomCode) => _rooms.GetOrAdd(roomCode, rc => new Room(rc, options.Value));

    /// <summary>
    /// Looks up an existing room without creating it.
    /// </summary>
    public bool TryGetRoom(RoomCode roomCode, out Room? room) => _rooms.TryGetValue(roomCode, out room);
}
