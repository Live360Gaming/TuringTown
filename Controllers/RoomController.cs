using Microsoft.AspNetCore.Mvc;
using TuringTown.Models;
using TuringTown.Services;

namespace TuringTown.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class RoomController(RoomManager roomManager) : ControllerBase
{
    readonly record struct CreateRoomResp(string RoomCode);
    
    /// <summary>
    /// Creates a new Room with a random room code.
    /// </summary>
    /// <returns></returns>
    [HttpPost]
    public IActionResult CreateRoom()
    {
        // Create a new random room code.
        RoomCode roomCode = RoomCode.CreateRandomRoomCode();
        
        // Create the room.
        Room room = roomManager.GetOrCreateRoom(roomCode);
        
        // Wrap the new code in the response
        return Ok(new CreateRoomResp(room.Code.ToString()));
    }
}