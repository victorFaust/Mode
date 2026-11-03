using Mode.Api.Endpoints;
using Mode.Api.Files;
using Mode.Domain.Entities;

namespace Mode.Api.Messaging;

public enum RoomType { Channel, Conversation }

public sealed record RoomRef(RoomType Type, Guid Id)
{
    [System.Text.Json.Serialization.JsonIgnore]
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
    /// <summary>True when the author deleted it themselves, so they can undo it.</summary>
    bool DeletedByAuthor,
    int ReplyCount,
    DateTimeOffset? LastReplyAt,
    IReadOnlyList<ReactionSummaryDto> Reactions,
    IReadOnlyList<MentionDto> Mentions,
    IReadOnlyList<AttachmentDto> Files,
    bool IsPinned,
    /// <summary>The forwarded copy shown as a card under the note in <see cref="BodyMarkdown"/>.</summary>
    ForwardDto? Forwarded,
    /// <summary>Posted by an incoming webhook; <see cref="Author"/> then carries the bot's name.</summary>
    bool IsBot);

/// <summary><see cref="MessageId"/> is null when the original no longer exists. Opening it still requires access to its room.</summary>
public sealed record ForwardDto(Guid? MessageId, UserDto Author, DateTimeOffset CreatedAt, string BodyMarkdown);

public sealed record PinDto(MessageDto Message, UserDto PinnedBy, DateTimeOffset PinnedAt);

/// <summary>One version of a message's text, oldest first; the last one is the current text.</summary>
public sealed record MessageVersionDto(string BodyMarkdown, DateTimeOffset WrittenAt);

public sealed record ForwardMessageRequest(Guid? Id, RoomRef? To, string? BodyMarkdown, bool? AcknowledgeDlpWarnings = null);

/// <summary>
/// Newest first; pass <see cref="NextBefore"/> as <c>before</c> to get older messages. Pages requested with <c>after</c>
/// are oldest first; pass <see cref="NextAfter"/> as <c>after</c> to get newer ones.
/// </summary>
public sealed record MessagePage(IReadOnlyList<MessageDto> Items, long? NextBefore, long? NextAfter);

public sealed record SendMessageRequest(Guid? Id, string? BodyMarkdown, Guid? ParentMessageId, IReadOnlyList<Guid>? FileIds, bool? AcknowledgeDlpWarnings = null);

public sealed record EditMessageRequest(string? BodyMarkdown, bool? AcknowledgeDlpWarnings = null);

public sealed record ActivityDto(
    Guid Id, ActivityType Type, string ActorUserId, Guid? WorkspaceId, Guid? ChannelId, Guid? ConversationId, Guid? MessageId, DateTimeOffset CreatedAt);

public sealed record ConversationDto(
    Guid Id, ConversationType Type, string? Name, IReadOnlyList<UserDto> Members, long LastSequence, DateTimeOffset CreatedAt,
    bool IsPinned, bool IsHidden);

public sealed record ConversationSettingsRequest(bool? IsPinned, bool? IsHidden);

public sealed record CreateConversationRequest(ConversationType? Type, string? UserId, IReadOnlyList<string>? UserIds, string? Name);

public sealed record RenameConversationRequest(string? Name);

public sealed record AddConversationMemberRequest(string? UserId);
