using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Mode.Api.Auditing;
using Mode.Api.Auth;
using Mode.Api.Authorization;
using Mode.Api.Messaging;
using Mode.Api.Realtime;
using Mode.Infrastructure.Data;

namespace Mode.Api.Hubs;

public sealed record JoinRoomRequest(RoomRef Room, long LastSequence);

/// <summary>
/// <see cref="Missed"/> holds the current state of every message changed after the client's last sequence, oldest change first.
/// When more than <see cref="ModeHub.MaxReplay"/> changes were missed, <see cref="ResyncRequired"/> is set instead.
/// </summary>
public sealed record JoinRoomResult(RoomRef Room, bool Granted, long CurrentSequence, IReadOnlyList<MessageDto> Missed, bool ResyncRequired);

/// <summary>Realtime endpoint. Connections are authenticated with the host token and closed when it expires.</summary>
[Authorize]
public sealed class ModeHub(
    IAuditLog audit,
    ModeDbContext db,
    AccessPolicy policy,
    ConnectionRegistry registry,
    MessageStore store,
    TypingThrottle typing) : Hub
{
    public const string Path = "/hubs/mode";
    public const int MaxReplay = 500;
    public const int MaxRoomsPerJoin = 200;

    public override async Task OnConnectedAsync()
    {
        var user = CurrentUser.FromPrincipal(Context.User!);
        registry.Add(Context.ConnectionId, user);
        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeEvents.UserGroup(user.Id));

        audit.Record(AuditActions.RealtimeConnected, "connection", Context.ConnectionId, actorUserId: user.Id);
        await db.SaveChangesAsync(Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public async Task<IReadOnlyList<JoinRoomResult>> JoinRooms(IReadOnlyList<JoinRoomRequest> rooms)
    {
        if (rooms.Count > MaxRoomsPerJoin) throw new HubException($"Join at most {MaxRoomsPerJoin} rooms at once.");
        var user = CurrentUser.FromPrincipal(Context.User!);
        var ct = Context.ConnectionAborted;
        var results = new List<JoinRoomResult>(rooms.Count);

        foreach (var request in rooms)
        {
            if (!(await policy.ForRoomAsync(user, request.Room, ct)).Has(Permission.Read))
            {
                results.Add(new JoinRoomResult(request.Room, false, 0, [], false));
                continue;
            }

            // Join first, then read: anything committed in between arrives twice rather than not at all.
            await Groups.AddToGroupAsync(Context.ConnectionId, request.Room.GroupName, ct);
            registry.Join(Context.ConnectionId, request.Room);

            var missed = await store.ChangesSinceAsync(request.Room, Math.Max(0, request.LastSequence), MaxReplay + 1, ct);
            var current = await store.CurrentSequenceAsync(request.Room, ct);
            results.Add(missed.Count > MaxReplay
                ? new JoinRoomResult(request.Room, true, current, [], true)
                : new JoinRoomResult(request.Room, true, current, missed, false));
        }

        return results;
    }

    public async Task LeaveRoom(RoomRef room)
    {
        registry.Leave(Context.ConnectionId, room);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, room.GroupName, Context.ConnectionAborted);
    }

    public async Task Typing(RoomRef room, Guid? parentMessageId)
    {
        if (!registry.HasJoined(Context.ConnectionId, room)) return;
        var user = CurrentUser.FromPrincipal(Context.User!);
        if (!typing.TryAcquire(user.Id, room.GroupName)) return;
        if (!(await policy.ForRoomAsync(user, room, Context.ConnectionAborted)).Has(Permission.Post)) return;

        await Clients.OthersInGroup(room.GroupName).SendAsync(RealtimeEvents.Typing, new TypingEvent(room, user.Id, parentMessageId));
    }
}
