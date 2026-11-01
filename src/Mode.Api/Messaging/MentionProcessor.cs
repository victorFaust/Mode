using Mode.Api.Authorization;
using Mode.Api.Notifications;
using Mode.Domain.Directory;
using Mode.Domain.Entities;

namespace Mode.Api.Messaging;

/// <summary>Stores a message's mentions and says who should be notified about them.</summary>
public sealed class MentionProcessor(AccessPolicy policy, IUserDirectory directory)
{
    /// <summary>
    /// Replaces <paramref name="message"/>'s mentions (its Mentions collection must be loaded) and returns notification
    /// requests; <see cref="NotificationService"/> filters them to readers and drops people already notified.
    /// </summary>
    public async Task<List<NotificationRequest>> ApplyAsync(Message message, RoomRef room, Guid? workspaceId, ParsedMentions parsed, CancellationToken ct)
    {
        message.Mentions.Clear();

        var userIds = parsed.UserIds
            .Select(raw => directory.TryNormalizeId(raw, out var id) ? id : null)
            .OfType<string>()
            .Distinct()
            .ToList();
        var mentionsEveryone = parsed.Channel || (parsed.Workspace && room.Type == RoomType.Channel);

        // Keys left unset: EF only treats new children of an already-tracked message as inserts when their key is default.
        foreach (var id in userIds)
            message.Mentions.Add(new Mention { Kind = MentionKind.User, TargetUserId = id });
        if (parsed.Channel)
            message.Mentions.Add(new Mention { Kind = MentionKind.Channel });
        if (parsed.Workspace && room.Type == RoomType.Channel)
            message.Mentions.Add(new Mention { Kind = MentionKind.Workspace });

        var requests = userIds
            .Select(id => Request(id, ActivityType.Mention))
            .ToList();

        // In a channel, @workspace reaches only workspace members who can read this channel, same as @channel.
        if (mentionsEveryone)
            requests.AddRange((await policy.ListReadersAsync(room, ct)).Select(id => Request(id, ActivityType.ChannelMention)));
        return requests;

        NotificationRequest Request(string recipient, ActivityType type) =>
            new(recipient, type, message.AuthorUserId, workspaceId, message.ChannelId, message.ConversationId, message.Id);
    }
}
