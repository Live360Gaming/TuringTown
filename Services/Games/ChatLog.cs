using TuringTown.Models;

namespace TuringTown.Services.Games;

/// <summary>
/// The chat history of one game, capped at a limit, plus the per-player "slow down" rule. Thread-safe, and it
/// never calls out while holding its lock, so callers may use it from inside their own locks.
/// </summary>
internal sealed class ChatLog(int transcriptLimit, int minIntervalMs)
{
    private readonly object _lock = new();
    private readonly List<ChatMessage> _messages = [];
    private readonly Dictionary<Guid, DateTimeOffset> _lastPost = [];

    /// <summary>
    /// Records that <paramref name="playerId"/> is posting now. False (and nothing recorded) if they posted
    /// less than the minimum interval ago.
    /// </summary>
    public bool TryTakeTurn(Guid playerId, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_lastPost.TryGetValue(playerId, out DateTimeOffset last) && (now - last).TotalMilliseconds < minIntervalMs)
                return false;
            _lastPost[playerId] = now;
            return true;
        }
    }

    public void Add(ChatMessage message)
    {
        lock (_lock)
        {
            _messages.Add(message);
            if (_messages.Count > transcriptLimit) _messages.RemoveAt(0);
        }
    }

    public IReadOnlyList<ChatMessage> Snapshot()
    {
        lock (_lock)
            return _messages.ToArray();
    }

    public IReadOnlyList<ChatMessage> Recent(int count)
    {
        lock (_lock)
            return _messages.TakeLast(count).ToArray();
    }
}
