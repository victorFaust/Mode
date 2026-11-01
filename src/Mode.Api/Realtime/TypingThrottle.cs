using System.Collections.Concurrent;

namespace Mode.Api.Realtime;

/// <summary>At most one typing event per user per room in each window.</summary>
public sealed class TypingThrottle(TimeProvider clock)
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new();

    public bool TryAcquire(string userId, string roomGroup)
    {
        var now = clock.GetUtcNow();
        var key = userId + "|" + roomGroup;
        var last = _lastSent.GetValueOrDefault(key);
        if (now - last < Window) return false;
        _lastSent[key] = now;

        if (_lastSent.Count > 10_000)
            foreach (var stale in _lastSent.Where(p => now - p.Value > Window).Select(p => p.Key).ToList())
                _lastSent.TryRemove(stale, out _);
        return true;
    }
}
