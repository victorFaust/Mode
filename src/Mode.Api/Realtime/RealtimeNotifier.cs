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
    public const string Notification = "notification";
    public const string PresenceChanged = "presenceChanged";
    public const string ReadMarkerChanged = "readMarkerChanged";
    public const string ActivityRead = "activityRead";
    public const string ConversationSettingsChanged = "conversationSettingsChanged";

    public static string UserGroup(string userId) => "user:" + userId;
}

public sealed record MessageEvent(RoomRef Room, long Sequence, MessageDto Message);

public sealed record MessageDeletedEvent(RoomRef Room, long Sequence, Guid MessageId, DateTimeOffset DeletedAt);

public sealed record ReactionChangedEvent(RoomRef Room, long Sequence, Guid MessageId, IReadOnlyList<ReactionSummaryDto> Reactions);

public sealed record TypingEvent(RoomRef Room, string UserId, Guid? ParentMessageId);

public sealed record ActivityEvent(ActivityDto Item, int UnreadCount);

public sealed record ActivityReadEvent(IReadOnlyList<Guid> ActivityIds, int UnreadCount);

public sealed record ReadMarkerChangedEvent(RoomRef Room, long LastReadSequence);

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

    public Task ActivityCreatedAsync(string userId, ActivityDto item, int unreadCount) =>
        User(userId).SendAsync(RealtimeEvents.ActivityCreated, new ActivityEvent(item, unreadCount));

    /// <summary>Keeps the badge in step across a user's tabs and devices.</summary>
    public Task ActivityReadAsync(string userId, IReadOnlyList<Guid> activityIds, int unreadCount) =>
        User(userId).SendAsync(RealtimeEvents.ActivityRead, new ActivityReadEvent(activityIds, unreadCount));

    public Task NotificationAsync(string userId, Notifications.NotificationBanner banner) =>
        User(userId).SendAsync(RealtimeEvents.Notification, banner);

    public Task ReadMarkerChangedAsync(string userId, RoomRef room, long lastReadSequence) =>
        User(userId).SendAsync(RealtimeEvents.ReadMarkerChanged, new ReadMarkerChangedEvent(room, lastReadSequence));

    /// <summary>Keeps a user's pinned and hidden chats in step across their tabs and devices.</summary>
    public Task ConversationSettingsChangedAsync(string userId, ConversationDto conversation) =>
        User(userId).SendAsync(RealtimeEvents.ConversationSettingsChanged, conversation);

    private IClientProxy User(string userId) => hub.Clients.Group(RealtimeEvents.UserGroup(userId));
}
