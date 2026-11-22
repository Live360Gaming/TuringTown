using TuringTown.Models;
using TuringTown.Services.Ai;

namespace TuringTown.Services.Games;

/// <summary>
/// The game night's shared memory for the AI team (see <see cref="TeamContext"/>). The night writes the plan and the
/// score; each game adds public highlights; every bot request carries a <see cref="Snapshot"/>. Thread-safe.
///
/// Bots regenerate for each game (their per-game memory is cleared), but this lasts the whole night.
/// </summary>
public sealed class NightContext
{
    public const int MaxHighlights = 16;
    public const int MaxHighlightLength = 200;

    private readonly object _gate = new();
    private readonly List<string> _highlights = [];
    private IReadOnlyList<string> _games = [];
    private string? _current;
    private int _number;
    private TeamScore? _score;
    private IReadOnlyList<PlayerNote> _players = [];

    public void SetPlan(IReadOnlyList<string> games)
    {
        lock (_gate)
            _games = games.ToArray();
    }

    public void StartGame(string title)
    {
        lock (_gate)
        {
            _current = title;
            _number++;
        }
    }

    /// <summary>The standings, and the team totals only when they are public (null hides them).</summary>
    public void SetScore(IReadOnlyList<Standing> standings, TeamTotals? publicTotals)
    {
        lock (_gate)
        {
            _players = standings.Select(s => new PlayerNote(s.Alias, s.Points)).ToArray();
            _score = publicTotals is { } t ? new TeamScore(t.Humans, t.Ai) : null;
        }
    }

    /// <summary>Something the whole room just saw. Player text is sanitised and capped; the oldest lines drop off.</summary>
    public void AddHighlight(string text)
    {
        string clean = PromptText.Sanitize(text.ReplaceLineEndings(" "), MaxHighlightLength).Trim();
        if (clean.Length == 0) return;
        lock (_gate)
        {
            _highlights.Add(clean);
            if (_highlights.Count > MaxHighlights) _highlights.RemoveAt(0);
        }
    }

    public TeamContext Snapshot()
    {
        lock (_gate)
            return new TeamContext(TeamContext.DefaultGoal, _games, _current, _number, _score, _players, _highlights.ToArray());
    }
}
