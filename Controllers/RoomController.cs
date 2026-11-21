using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TuringTown.Models;
using TuringTown.Services;

namespace TuringTown.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class RoomController(RoomManager roomManager) : ControllerBase
{
    public const string HostTokenHeader = "X-Host-Token";

    private readonly record struct CreateRoomResp(string RoomCode, string HostToken, RoomSnapshot State);

    /// <summary>
    /// Creates a new Room with a random room code, the user calling this is the TV hosting the game.
    /// </summary>
    /// <returns></returns>
    [HttpPost("create")]
    [EnableRateLimiting(Program.RoomRateLimit)]
    public IActionResult CreateRoom()
    {
        // Create a new random room code.
        RoomCode roomCode = RoomCode.CreateRandomRoomCode();

        // Create the room.
        Room room = roomManager.GetOrCreateRoom(roomCode);

        // Wrap the new code in the response
        return Ok(new CreateRoomResp(room.Code.ToString(), room.HostToken, room.GetSnapshot()));
    }

    public readonly record struct JoinRoomReq(string RoomCode);

    private readonly record struct JoinRoomResp
    {
        public string? Error { get; init; }
        public string? Code { get; init; }
        public Guid? PlayerId { get; init; }
        public string? RoomCode { get; init; }
        public string? PlayerToken { get; init; }
        public string? CharacterId { get; init; }
        public string? Alias { get; init; }
        public string? Color { get; init; }
    }

    /// <summary>
    /// Adds a player to a room, it's the client responsibility to follow up with a connection.
    /// </summary>
    /// <param name="req"></param>
    /// <returns></returns>
    [HttpPost("join")]
    [EnableRateLimiting(Program.RoomRateLimit)]
    public IActionResult JoinRoom([FromBody] JoinRoomReq req)
    {
        // Parse the room code
        if (!RoomCode.TryFromString(req.RoomCode, out RoomCode roomCode))
        {
            return BadRequest(new JoinRoomResp
            {
                Error = "Failed to parse room code.",
                Code = "invalid_code"
            });
        }

        // Get the room, joining never creates one.
        if (!roomManager.TryGetRoom(roomCode, out Room? room) || room is null)
        {
            return NotFound(new JoinRoomResp
            {
                Error = "Room not found.",
                Code = "room_not_found"
            });
        }

        // Add the new player, the room enforces the player limit and phase.
        if (!room.TryAddPlayer(out Player? player, out RoomJoinError joinError) || player is null)
        {
            return Conflict(joinError == RoomJoinError.RoomFull
                ? new JoinRoomResp {Error = "This room is full.", Code = "room_full"}
                : new JoinRoomResp {Error = "The game has already started.", Code = "game_started"});
        }

        // Return the player id to the client.
        return Ok(new JoinRoomResp
        {
            PlayerId = player.Id,
            RoomCode = room.Code.ToString(),
            PlayerToken = player.Token,
            CharacterId = player.CharacterId,
            Alias = player.Alias,
            Color = player.Color
        });
    }

    /// <summary>
    /// Current room state (phase, player count, settings). Polled by the host and phones.
    /// </summary>
    [HttpGet("{code}")]
    public IActionResult GetRoom(string code)
    {
        if (!RoomCode.TryFromString(code, out RoomCode roomCode))
            return BadRequest(new {Error = "Failed to parse room code."});

        if (!roomManager.TryGetRoom(roomCode, out Room? room) || room is null)
            return NotFound(new {Error = "Room not found."});

        return Ok(room.GetSnapshot());
    }

    public readonly record struct UpdateSettingsReq(int? MaxPlayers, bool? FamilyFriendly);

    /// <summary>
    /// Changes session settings. Host only (X-Host-Token header), lobby only.
    /// </summary>
    [HttpPut("{code}/settings")]
    public IActionResult UpdateSettings(string code, [FromBody] UpdateSettingsReq req)
    {
        if (!RoomCode.TryFromString(code, out RoomCode roomCode))
            return BadRequest(new {Error = "Failed to parse room code."});

        if (!roomManager.TryGetRoom(roomCode, out Room? room) || room is null)
            return NotFound(new {Error = "Room not found."});

        if (!room.IsHost(Request.Headers[HostTokenHeader].FirstOrDefault()))
            return StatusCode(StatusCodes.Status403Forbidden, new {Error = "Only the host can change settings."});

        string? error = room.TryUpdateSettings(req.MaxPlayers, req.FamilyFriendly) switch
        {
            RoomSettingsError.None => null,
            RoomSettingsError.NotInLobby => "Settings are locked once the game has started.",
            RoomSettingsError.BelowMinimum => "That is below the minimum number of players.",
            RoomSettingsError.AboveServerMaximum => "That is above the maximum allowed for this server.",
            RoomSettingsError.BelowPlayerCount => "The limit cannot be lower than the number of players already in the room.",
            _ => "Invalid settings."
        };

        return error is null ? Ok(room.GetSnapshot()) : Conflict(new {Error = error});
    }
}
