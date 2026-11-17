using Microsoft.AspNetCore.Mvc;
using TuringTown.Models;
using TuringTown.Services;

namespace TuringTown.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class RoomController(RoomManager roomManager) : ControllerBase
{
    private readonly record struct CreateRoomResp(string RoomCode);
    
    /// <summary>
    /// Creates a new Room with a random room code, the user calling this is the TV hosting the game.
    /// </summary>
    /// <returns></returns>
    [HttpPost("create")]
    public IActionResult CreateRoom()
    {
        // Create a new random room code.
        RoomCode roomCode = RoomCode.CreateRandomRoomCode();
        
        // Create the room.
        Room room = roomManager.GetOrCreateRoom(roomCode);
        
        // Wrap the new code in the response
        return Ok(new CreateRoomResp(room.Code.ToString()));
    }

    public readonly record struct JoinRoomReq(string RoomCode);

    private readonly record struct JoinRoomResp
    {
        public string? Error { get; init; }
        public Guid? PlayerId { get; init; }
    }

    /// <summary>
    /// Adds a player to a room, it's the client responsibility to follow up with a connection.
    /// </summary>
    /// <param name="req"></param>
    /// <returns></returns>
    [HttpPost("join")]
    public IActionResult JoinRoom([FromBody] JoinRoomReq req)
    {
        // Parse the room code
        if (!RoomCode.TryFromString(req.RoomCode, out RoomCode roomCode))
        {
            return BadRequest(new JoinRoomResp
            {
                Error = "Failed to parse room code."
            });
        }

        // Get the room
        Room room = roomManager.GetOrCreateRoom(roomCode);
        
        // Add the new player
        Player player = room.AddNewPlayer();

        // Return the player id to the client.
        return Ok(new JoinRoomResp
        {
            PlayerId = player.Id
        });
    }
}