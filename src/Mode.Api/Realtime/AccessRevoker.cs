using Microsoft.AspNetCore.SignalR;
using Mode.Api.Authorization;
using Mode.Api.Hubs;

namespace Mode.Api.Realtime;

/// <summary>
/// Re-checks every room a user's live connections have joined and drops the ones they can no longer read.
/// Called after any membership or role change, before the API responds, so events stop immediately.
/// </summary>
public sealed class AccessRevoker(ConnectionRegistry registry, AccessPolicy policy, IHubContext<ModeHub> hub)
{
    public async Task ReevaluateAsync(string userId, CancellationToken ct)
    {
        foreach (var (connectionId, user, rooms) in registry.ConnectionsOf(userId))
        foreach (var room in rooms)
        {
            if ((await policy.ForRoomAsync(user, room, ct)).Has(Permission.Read)) continue;

            registry.Leave(connectionId, room);
            await hub.Groups.RemoveFromGroupAsync(connectionId, room.GroupName, ct);
            await hub.Clients.Client(connectionId).SendAsync(RealtimeEvents.RoomAccessRevoked, new RoomAccessRevokedEvent(room), ct);
        }
    }
}
