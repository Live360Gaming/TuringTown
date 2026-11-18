namespace TuringTown.Models;

/// <summary>
/// The content restrictions for a session. Content-producing systems (AI generation, prompt pools, player text)
/// must accept this explicitly so family-friendly mode cannot be forgotten. See docs/content-policy.md.
/// </summary>
/// <param name="FamilyFriendly">
/// True restricts mature content. False only relaxes game-content restrictions; mandatory safety protections always apply.
/// </param>
public readonly record struct ContentPolicy(bool FamilyFriendly);
