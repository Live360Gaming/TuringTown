using System.Security.Cryptography;
using TuringTown.Services.Ai;

namespace TuringTown.Services.Games;

/// <summary>
/// How a bot plays Shout Out. The server decides this, not a model: a model knows nearly every answer and would buzz
/// in at once, which would give it away. So each bot knows only so much (drawn once per game), sometimes guesses,
/// sometimes stays quiet, and buzzes at a human pace: when the room is slow, the bots slow down too.
/// </summary>
internal sealed class ShoutOutBotPolicy(ShoutOutOptions options, Func<double>? random = null)
{
    /// <summary>The usual time from "buzzing open" to a buzz when nothing is known about the humans yet.</summary>
    internal const double DefaultHumanBuzzSeconds = 3.0;

    private readonly Func<double> _random = random ?? (() => RandomNumberGenerator.GetInt32(1_000_000) / 1_000_000.0);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, double> _knowledge = [];
    private readonly List<double> _humanBuzzSeconds = [];

    /// <summary>A plan for one question: whether the bot knows the answer, and when it buzzes (null: it does not).</summary>
    public readonly record struct BuzzPlan(bool Knows, TimeSpan? BuzzAfter);

    public double KnowledgeOf(Guid bot)
    {
        lock (_gate)
        {
            if (!_knowledge.TryGetValue(bot, out double k))
            {
                double min = Math.Min(options.BotKnowledgeMin, options.BotKnowledgeMax);
                double max = Math.Max(options.BotKnowledgeMin, options.BotKnowledgeMax);
                _knowledge[bot] = k = min + _random() * (max - min);
            }
            return k;
        }
    }

    /// <summary>A human buzzed this long after buzzing opened.</summary>
    public void RecordHumanBuzz(TimeSpan afterOpen)
    {
        lock (_gate)
            _humanBuzzSeconds.Add(Math.Max(0, afterOpen.TotalSeconds));
    }

    /// <summary>How much slower than the default pace the humans are (0.8 to 2).</summary>
    public double Pace()
    {
        lock (_gate)
        {
            if (_humanBuzzSeconds.Count < 3) return 1;
            double median = _humanBuzzSeconds.Order().ElementAt(_humanBuzzSeconds.Count / 2);
            return Math.Clamp(median / DefaultHumanBuzzSeconds, 0.8, 2.0);
        }
    }

    public BuzzPlan PlanBuzz(Guid bot, TriviaDifficulty difficulty)
    {
        double shift = difficulty switch { TriviaDifficulty.Easy => 0.1, TriviaDifficulty.Hard => -0.15, _ => 0 };
        bool knows = _random() < Math.Clamp(KnowledgeOf(bot) + shift, 0.05, 0.95);
        // Knowing makes a buzz likely and quick; not knowing makes it a rare, slow guess.
        bool buzzes = _random() < (knows ? 0.8 : 0.2);
        if (!buzzes) return new BuzzPlan(knows, null);

        double seconds = knows ? 1.5 + _random() * 3 : 3 + _random() * 4;
        return new BuzzPlan(knows, TimeSpan.FromSeconds(seconds * Pace()));
    }

    /// <summary>How long the bot takes to pick its answer once it has buzzed, always inside <paramref name="limit"/>.</summary>
    public TimeSpan AnswerDelay(TimeSpan limit)
    {
        double max = Math.Max(0.3, Math.Min(3, limit.TotalSeconds - 1));
        return TimeSpan.FromSeconds(Math.Min(max, 0.8 + _random() * (max - 0.8)));
    }

    /// <summary>The answer index the bot picks: the right one when it knows, otherwise a guess (lucky a quarter of the time).</summary>
    public int PickAnswer(bool knows, int correct, int count)
    {
        if (knows || _random() < 0.25) return correct;
        int[] wrong = Enumerable.Range(0, count).Where(i => i != correct).ToArray();
        return wrong[Math.Min(wrong.Length - 1, (int)(_random() * wrong.Length))];
    }
}
