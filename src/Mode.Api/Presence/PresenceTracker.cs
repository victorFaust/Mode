using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Mode.Api.Hubs;
using Mode.Api.Realtime;

namespace Mode.Api.Presence;

public enum PresenceStatus { Available, Busy, DoNotDisturb, Away, Offline }

public sealed record PresenceDto(string UserId, PresenceStatus Status, string? Message);

public sealed class PresenceOptions
{
    public const string SectionName = "Presence";
    public int AwayAfterSeconds { get; set; } = 300;
    public int SweepSeconds { get; set; } = 30;
}

/// <summary>
/// In-memory presence (ephemeral by design). A manually chosen status and message last until changed or until the
/// server restarts. Per server: scaling out needs a shared store.
/// </summary>
public sealed class PresenceTracker(IOptions<PresenceOptions> options, TimeProvider clock)
{
    private sealed class State
    {
        public HashSet<string> Connections { get; } = [];
        public DateTimeOffset LastActive { get; set; }
        public PresenceStatus? Manual { get; set; }
        public string? Message { get; set; }
        public PresenceDto? Published { get; set; }
    }

    private readonly ConcurrentDictionary<string, State> _users = new();

    public PresenceDto? Connected(string userId, string connectionId) => Update(userId, state =>
    {
        state.Connections.Add(connectionId);
        state.LastActive = clock.GetUtcNow();
    });

    public PresenceDto? Disconnected(string userId, string connectionId) => Update(userId, state => state.Connections.Remove(connectionId));

    public PresenceDto? Heartbeat(string userId, bool active) => Update(userId, state =>
    {
        if (active) state.LastActive = clock.GetUtcNow();
    });

    /// <summary>Available clears a manual status; a null or blank message clears the message.</summary>
    public PresenceDto? SetManual(string userId, PresenceStatus status, string? message) => Update(userId, state =>
    {
        state.Manual = status == PresenceStatus.Available ? null : status;
        state.Message = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
    });

    public PresenceDto Get(string userId)
    {
        if (!_users.TryGetValue(userId, out var state)) return new PresenceDto(userId, PresenceStatus.Offline, null);
        lock (state) return Effective(userId, state);
    }

    public bool IsConnected(string userId) => _users.TryGetValue(userId, out var state) && Locked(state, s => s.Connections.Count > 0);

    /// <summary>The user's chosen status, even while offline, so DND also holds back push to their devices.</summary>
    public bool IsDoNotDisturb(string userId) =>
        _users.TryGetValue(userId, out var state) && Locked(state, s => s.Manual == PresenceStatus.DoNotDisturb);

    /// <summary>Re-evaluates time-based transitions (available to away) and returns what changed.</summary>
    public IReadOnlyList<PresenceDto> Sweep()
    {
        var changes = new List<PresenceDto>();
        foreach (var userId in _users.Keys)
            if (Update(userId, _ => { }) is { } change) changes.Add(change);
        return changes;
    }

    private PresenceDto? Update(string userId, Action<State> change)
    {
        var state = _users.GetOrAdd(userId, _ => new State { LastActive = clock.GetUtcNow() });
        lock (state)
        {
            change(state);
            var current = Effective(userId, state);
            if (current == state.Published) return null;
            state.Published = current;
            return current;
        }
    }

    private PresenceDto Effective(string userId, State state)
    {
        var status = state.Connections.Count == 0 ? PresenceStatus.Offline
            : state.Manual is { } manual ? manual
            : clock.GetUtcNow() - state.LastActive > TimeSpan.FromSeconds(options.Value.AwayAfterSeconds) ? PresenceStatus.Away
            : PresenceStatus.Available;
        return new PresenceDto(userId, status, state.Message);
    }

    private static T Locked<T>(State state, Func<State, T> read)
    {
        lock (state) return read(state);
    }
}

public sealed class PresenceNotifier(IHubContext<ModeHub> hub)
{
    public static string Group(string userId) => "presence:" + userId;

    public async Task PublishAsync(PresenceDto? change)
    {
        if (change is not null)
            await hub.Clients.Group(Group(change.UserId)).SendAsync(RealtimeEvents.PresenceChanged, change);
    }
}

/// <summary>Publishes time-based presence changes, such as turning away after inactivity.</summary>
public sealed class PresenceSweeper(PresenceTracker tracker, PresenceNotifier notifier, IOptions<PresenceOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.SweepSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken).AsTask().ContinueWith(t => !t.IsCanceled && t.Result, CancellationToken.None))
            foreach (var change in tracker.Sweep())
                await notifier.PublishAsync(change);
    }
}
