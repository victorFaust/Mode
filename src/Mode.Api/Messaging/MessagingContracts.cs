using Mode.Api.Endpoints;
using Mode.Api.Files;
using Mode.Domain.Entities;

namespace Mode.Api.Messaging;

public enum RoomType { Channel, Conversation }

public sealed record RoomRef(RoomType Type, Guid Id)
{
    public string GroupName => $"{(Type == RoomType.Channel ? "channel" : "conversation")}:{Id}";

    public static RoomRef Of(Message message) => message.ChannelId is { } channelId
        ? new RoomRef(RoomType.Channel, channelId)
        : new RoomRef(RoomType.Conversation, message.ConversationId!.Value);
}

public sealed record ReactionSummaryDto(string Emoji, int Count, IReadOnlyList<string> UserIds);

public sealed record MentionDto(MentionKind Kind, string? UserId);

public sealed record MessageDto(
    Guid Id,
    RoomRef Room,
    long Sequence,
    long UpdatedSequence,
    Guid? ParentMessageId,
    UserDto Author,
    string BodyMarkdown,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EditedAt,
    bool IsDeleted,
    int ReplyCount,
    DateTimeOffset? LastReplyAt,
    IReadOnlyList<ReactionSummaryDto> Reactions,
    IReadOnlyList<MentionDto> Mentions,
    IReadOnlyList<AttachmentDto> Files);

/// <summary>
/// Newest first; pass <see cref="NextBefore"/> as <c>before</c> to get older messages. Pages requested with <c>after</c>
/// are oldest first; pass <see cref="NextAfter"/> as <c>after</c> to get newer ones.
/// </summary>
public sealed record MessagePage(IReadOnlyList<MessageDto> Items, long? NextBefore, long? NextAfter);

public sealed record SendMessageRequest(Guid? Id, string? BodyMarkdown, Guid? ParentMessageId, IReadOnlyList<Guid>? FileIds);

public sealed record EditMessageRequest(string? BodyMarkdown);

public sealed record ActivityDto(
    Guid Id, ActivityType Type, string ActorUserId, Guid? WorkspaceId, Guid? ChannelId, Guid? ConversationId, Guid? MessageId, DateTimeOffset CreatedAt);

public sealed record ConversationDto(
    Guid Id, ConversationType Type, string? Name, IReadOnlyList<UserDto> Members, long LastSequence, DateTimeOffset CreatedAt);

public sealed record CreateConversationRequest(ConversationType? Type, string? UserId, IReadOnlyList<string>? UserIds, string? Name);

public sealed record RenameConversationRequest(string? Name);

public sealed record AddConversationMemberRequest(string? UserId);
