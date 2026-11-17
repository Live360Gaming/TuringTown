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

    /// <summary>
    /// The players currently in the game.
    /// </summary>
    private List<Player> _players = new();

    /// <summary>
    /// Adds a new
    /// </summary>
    /// <returns></returns>
    public Player AddNewPlayer()
    {
        // Generate a new player id
        Player p = new Player
        {
            Id = Guid.NewGuid()
        };
        
        // Lock the game and add the player.
        lock (this)
            _players.Add(p);

        // Return the new player.
        return p;
    }
}