using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Mode.Api.Auth;
using Mode.Api.Authorization;
using Mode.Api.Hubs;
using Mode.Infrastructure.Data;

namespace Mode.Api.Realtime;

/// <summary>
/// Re-checks every room a user's live connections have joined and drops the ones they can no longer read, and marks
/// unread feed items from those rooms as read so the badge stays truthful.
/// Called after any membership or role change, before the API responds, so events stop immediately.
/// </summary>
public sealed class AccessRevoker(ConnectionRegistry registry, AccessPolicy policy, IHubContext<ModeHub> hub, ModeDbContext db, TimeProvider clock)
{
    public async Task ReevaluateAsync(string userId, CancellationToken ct)
    {
        var readable = await policy.ReadableRoomsAsync(new CurrentUser(userId, userId, null, false, false), ct);
        var (workspaceIds, channelIds, conversationIds) = (readable.WorkspaceIds, readable.ChannelIds, readable.ConversationIds);
        await db.ActivityItems
            .Where(a => a.UserId == userId && a.ReadAt == null &&
                (a.ChannelId != null ? !channelIds.Contains(a.ChannelId.Value)
                : a.ConversationId != null ? !conversationIds.Contains(a.ConversationId.Value)
                : a.WorkspaceId != null && !workspaceIds.Contains(a.WorkspaceId.Value)))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ReadAt, clock.GetUtcNow()), ct);
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
