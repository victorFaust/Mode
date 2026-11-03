using System.Collections.Concurrent;
using Mode.Api.Auth;
using Mode.Api.Messaging;

namespace Mode.Api.Realtime;

/// <summary>
/// Which rooms each live connection has joined, so access can be re-checked when memberships change.
/// In-memory and per server; scaling out needs a shared store (see docs/DECISIONS.md).
/// </summary>
public sealed class ConnectionRegistry
{
    private sealed record Connection(CurrentUser User, ConcurrentDictionary<string, RoomRef> Rooms, Action Abort);

    private readonly ConcurrentDictionary<string, Connection> _connections = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _byUser = new();

    public void Add(string connectionId, CurrentUser user, Action abort)
    {
        _connections[connectionId] = new Connection(user, new ConcurrentDictionary<string, RoomRef>(), abort);
        _byUser.GetOrAdd(user.Id, _ => new ConcurrentDictionary<string, byte>())[connectionId] = 0;
    }

    public IReadOnlyList<string> ConnectedUserIds() => _byUser.Where(u => !u.Value.IsEmpty).Select(u => u.Key).ToList();

    /// <summary>Closes every live connection of a person; the hub's disconnect handling removes their groups.</summary>
    public int AbortAll(string userId)
    {
        if (!_byUser.TryGetValue(userId, out var ids)) return 0;
        var aborted = 0;
        foreach (var id in ids.Keys)
            if (_connections.TryGetValue(id, out var connection))
            {
                connection.Abort();
                aborted++;
            }
        return aborted;
    }

    public void Remove(string connectionId)
    {
        if (!_connections.TryRemove(connectionId, out var connection)) return;
        if (_byUser.TryGetValue(connection.User.Id, out var ids))
            ids.TryRemove(connectionId, out _);
    }

    public CurrentUser? UserOf(string connectionId) => _connections.TryGetValue(connectionId, out var c) ? c.User : null;

    public void Join(string connectionId, RoomRef room)
    {
        if (_connections.TryGetValue(connectionId, out var connection))
            connection.Rooms[room.GroupName] = room;
    }

    public void Leave(string connectionId, RoomRef room)
    {
        if (_connections.TryGetValue(connectionId, out var connection))
            connection.Rooms.TryRemove(room.GroupName, out _);
    }

    public bool HasJoined(string connectionId, RoomRef room) =>
        _connections.TryGetValue(connectionId, out var c) && c.Rooms.ContainsKey(room.GroupName);

    public IReadOnlyList<(string ConnectionId, CurrentUser User, IReadOnlyList<RoomRef> Rooms)> ConnectionsOf(string userId) =>
        _byUser.TryGetValue(userId, out var ids)
            ? ids.Keys
                .Select(id => _connections.TryGetValue(id, out var c) ? (id, c.User, (IReadOnlyList<RoomRef>)c.Rooms.Values.ToList()) : default)
                .Where(x => x.id is not null)
                .ToList()
            : [];
}
