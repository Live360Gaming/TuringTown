using TuringTown.Services.Games;

namespace TuringTown.Tests;

public class NarrationTests
{
    private static string AudioRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TuringTown.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "game", "public", "audio");
    }

    [Fact]
    public void The_manifest_has_26_unique_lines_in_four_categories()
    {
        IReadOnlyList<NarrationEntry> entries = NarrationManifest.Entries;

        Assert.Equal(26, entries.Select(e => e.Key).Distinct().Count());
        Assert.Equal(26, entries.Count);
        Assert.Equal([2, 10, 5, 9], new[] { "introduction", "transition", "ending", "goodbye" }.Select(c => entries.Count(e => e.Category == c)));
        Assert.All(entries, e => Assert.Equal($"turing-town/narration/{e.Key}.mp3", e.File));
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Transcript)));
    }

    [Fact]
    public void Every_line_has_a_real_mp3_file()
    {
        Assert.All(NarrationManifest.Entries, e =>
        {
            byte[] data = File.ReadAllBytes(Path.Combine(AudioRoot(), e.File));
            bool frameOrTag = data[0] == 0xFF && (data[1] & 0xE0) == 0xE0 || data[0] == 'I' && data[1] == 'D' && data[2] == '3';
            Assert.True(data.Length > 4000 && frameOrTag, e.Key);
            Assert.InRange(e.DurationMs, 500, 30000);
        });
    }

    [Fact]
    public void A_transition_line_finishes_well_before_the_server_gives_up_on_it()
    {
        int limit = new TuringTownOptions().TransitionMaxSeconds * 1000;
        Assert.All(NarrationManifest.Entries.Where(e => e.Category == "transition"), e => Assert.True(e.DurationMs + 3000 <= limit, e.Key));
    }

    [Fact]
    public void Every_other_goodbye_can_be_picked_and_the_last_one_is_not_repeated()
    {
        Assert.Equal(8, NarrationManifest.Goodbyes.Count);
        Assert.DoesNotContain(NarrationManifest.Thanks, NarrationManifest.Goodbyes);

        string last = NarrationManifest.Goodbyes[0];
        HashSet<string> picked = Enumerable.Range(0, 2000).Select(_ => NarrationManifest.PickGoodbye(last)).ToHashSet();

        Assert.Equal(NarrationManifest.Goodbyes.Skip(1).Order(), picked.Order());
    }
}
