using System.Security.Cryptography;
using TuringTown.Models;

namespace TuringTown.Services.Games;

/// <summary>
/// The built-in prompts of the Quick Fire round, with the answers the scripted bots give. All of them are family safe,
/// so they suit either <see cref="ContentPolicy"/>.
/// </summary>
internal static class QuickFirePrompts
{
    internal sealed record Prompt(string Text, string[] Answers);

    internal static readonly Prompt[] All =
    [
        new("Describe your morning in five words or less", ["snooze snooze snooze panic coffee", "slow start, cold toast", "forgot my keys again", "woke up, regretted it", "cereal and doom scrolling"]),
        new("What would you do with one extra hour today?", ["nap, obviously", "finally fold the laundry", "play something dumb", "take a long walk", "eat a second lunch"]),
        new("Name a food you will defend to the end", ["pineapple pizza sorry", "cold leftover fries", "plain buttered noodles", "olives, all of them", "gas station nachos"]),
        new("What is the most pointless thing you own?", ["a drawer of dead batteries", "a bread maker lol", "three phone cases, no phone for them", "a single roller skate", "so many tote bags"]),
        new("What sound instantly annoys you?", ["people chewing loud", "that low battery chirp", "my own alarm", "forks scraping plates", "a dripping tap at 2am"])
    ];

    public static Prompt Pick(ContentPolicy policy) => All[RandomNumberGenerator.GetInt32(All.Length)];
}
