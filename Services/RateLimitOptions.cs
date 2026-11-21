namespace TuringTown.Services;

/// <summary>
/// The "RateLimits" section: how often one client address may create or join rooms, so a script cannot spin up rooms (and
/// games on the real AI) without end.
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    public int RoomRequestsPerMinute { get; set; } = 60;
}
