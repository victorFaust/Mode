using Microsoft.AspNetCore.SignalR;
using Mode.Api.Files;
using Mode.Api.Hubs;
using Mode.Api.Messaging;

namespace Mode.Api.Realtime;

public static class RealtimeEvents
{
    public const string MessageCreated = "messageCreated";
    public const string MessageEdited = "messageEdited";
    public const string MessageDeleted = "messageDeleted";
    public const string ReactionChanged = "reactionChanged";
    public const string Typing = "typing";
    public const string ActivityCreated = "activityCreated";
    public const string RoomAccessRevoked = "roomAccessRevoked";
    public const string FileUpdated = "fileUpdated";

    public static string UserGroup(string userId) => "user:" + userId;
}

public sealed record MessageEvent(RoomRef Room, long Sequence, MessageDto Message);

public sealed record MessageDeletedEvent(RoomRef Room, long Sequence, Guid MessageId, DateTimeOffset DeletedAt);

public sealed record ReactionChangedEvent(RoomRef Room, long Sequence, Guid MessageId, IReadOnlyList<ReactionSummaryDto> Reactions);

public sealed record TypingEvent(RoomRef Room, string UserId, Guid? ParentMessageId);

public sealed record ActivityEvent(ActivityDto Item);

public sealed record RoomAccessRevokedEvent(RoomRef Room);

public sealed record FileUpdatedEvent(RoomRef Room, FileDto File);

/// <summary>Pushes events to SignalR groups after the change has been committed.</summary>
public sealed class RealtimeNotifier(IHubContext<ModeHub> hub)
{
    public Task MessageCreatedAsync(MessageDto message) =>
        hub.Clients.Group(message.Room.GroupName).SendAsync(RealtimeEvents.MessageCreated, new MessageEvent(message.Room, message.UpdatedSequence, message));

    public Task MessageEditedAsync(MessageDto message) =>
        hub.Clients.Group(message.Room.GroupName).SendAsync(RealtimeEvents.MessageEdited, new MessageEvent(message.Room, message.UpdatedSequence, message));

    public Task MessageDeletedAsync(RoomRef room, long sequence, Guid messageId, DateTimeOffset deletedAt) =>
        hub.Clients.Group(room.GroupName).SendAsync(RealtimeEvents.MessageDeleted, new MessageDeletedEvent(room, sequence, messageId, deletedAt));

    public Task ReactionChangedAsync(RoomRef room, long sequence, Guid messageId, IReadOnlyList<ReactionSummaryDto> reactions) =>
        hub.Clients.Group(room.GroupName).SendAsync(RealtimeEvents.ReactionChanged, new ReactionChangedEvent(room, sequence, messageId, reactions));

    public Task FileUpdatedAsync(FileDto file) =>
        hub.Clients.Group(file.Room.GroupName).SendAsync(RealtimeEvents.FileUpdated, new FileUpdatedEvent(file.Room, file));

    public async Task ActivityCreatedAsync(IEnumerable<(string UserId, ActivityDto Item)> items)
    {
        foreach (var (userId, item) in items)
            await hub.Clients.Group(RealtimeEvents.UserGroup(userId)).SendAsync(RealtimeEvents.ActivityCreated, new ActivityEvent(item));
    }
}
