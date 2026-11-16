using System.Security.Cryptography;
using TuringTown.Models;

namespace TuringTown.Services;

public class RoomCodeGenerator
{
    /// <summary>
    /// Uses crypto random number to generate a room code.
    /// </summary>
    /// <returns></returns>
    public RoomCode CreateRoomCode()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(sizeof(uint));
        uint roomNumber = BitConverter.ToUInt32(bytes);
        return new RoomCode(roomNumber);
    }
}