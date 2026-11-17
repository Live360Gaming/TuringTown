namespace TuringTown.Models;

/// <summary>
/// A running game.
/// </summary>
public class Room(RoomCode rc)
{
    /// <summary>
    /// The code used to join the room.
    /// </summary>
    public RoomCode Code { get; } = rc;
}