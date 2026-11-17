using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TuringTown.Models;

/// <summary>
/// The room code is just an i32, it's displayed to the user as hexadecimal "AABB-CCDD" (8 hex chars)
/// </summary>
/// <param name="Number"></param>
public partial record struct RoomCode(uint Number)
{
    /// <summary>
    /// Uses crypto random number to generate a room code.
    /// </summary>
    /// <returns></returns>
    public static RoomCode CreateRandomRoomCode()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(sizeof(uint));
        uint roomNumber = BitConverter.ToUInt32(bytes);
        return new RoomCode(roomNumber);
    }

    /// <summary>
    /// Everything non-hexadecimal
    /// </summary>
    [GeneratedRegex(@"[^A-Za-z0-9]")]
    private static partial Regex StripBadChars { get; }

    public static bool TryFromString(string stringCode, out RoomCode roomCode)
    {
        try
        {
            // Strip everything non-hexadecimal
            string cleanedCode = StripBadChars.Replace(stringCode, string.Empty);

            byte[] buffer = Convert.FromHexString(cleanedCode);

            if (buffer.Length != sizeof(uint))
            {
                roomCode = default;
                return false;
            }

            uint code = BitConverter.ToUInt32(buffer, 0);

            roomCode = new RoomCode(code);
            return true;
        }
        catch (Exception)
        {
            roomCode = default;
            return false;
        }
    }

    public override string ToString()
    {
        // I know Endian exists, but I don't care this will be the same each time.
        byte[] bytes = BitConverter.GetBytes(Number);

        // "AAAAAAAA"
        string code = Convert.ToHexString(bytes);

        // "AAAA-AAAA"
        return $"{code[..4]}-{code[4..8]}";
    }
}