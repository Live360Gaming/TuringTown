using System.Runtime.CompilerServices;

namespace TuringTown.Tests;

/// <summary>
/// Makes the tests behave the same on every machine. A developer's user secrets and environment may switch the real AI on
/// (<c>Ai:Provider=Foundry</c>), the archive or Azure content safety; a test run must never call Azure or depend on that. These
/// environment variables outrank user secrets, and a test that wants a setting overrides it in code (in-memory settings are
/// added last), so the evaluation tests, which build their own clients, are not affected either.
/// </summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("Ai__Provider", "Scripted");
        Environment.SetEnvironmentVariable("Storage__ArchiveTranscripts", "false");
        Environment.SetEnvironmentVariable("ContentSafety__Provider", "None");
    }
}
