using Microsoft.EntityFrameworkCore;
using Mode.Api.Authorization;
using Mode.Domain.Directory;
using Mode.Domain.Entities;
using Mode.Infrastructure.Data;

namespace Mode.Api.Messaging;

/// <summary>Stores a message's mentions and creates activity items for mentioned people who can read the room.</summary>
public sealed class MentionProcessor(ModeDbContext db, AccessPolicy policy, IUserDirectory directory, TimeProvider clock)
{
    public static readonly TimeSpan ActivityLifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// Replaces <paramref name="message"/>'s mentions (its Mentions collection must be loaded) and adds activity items
    /// to the context. Users who were already notified about this message are not notified again.
    /// </summary>
    public async Task<IReadOnlyList<ActivityItem>> ApplyAsync(Message message, RoomRef room, Guid? workspaceId, ParsedMentions parsed, CancellationToken ct)
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

        if (userIds.Count == 0 && !mentionsEveryone) return [];

        // In a channel, @workspace reaches only workspace members who can read this channel, same as @channel.
        var readers = (await policy.ListReadersAsync(room, ct)).ToHashSet();
        var alreadyNotified = (await db.ActivityItems.Where(a => a.MessageId == message.Id).Select(a => a.UserId).ToListAsync(ct)).ToHashSet();

        var recipients = new Dictionary<string, ActivityType>();
        foreach (var id in userIds.Where(readers.Contains))
            recipients[id] = ActivityType.Mention;
        if (mentionsEveryone)
            foreach (var id in readers)
                recipients.TryAdd(id, ActivityType.ChannelMention);

        var now = clock.GetUtcNow();
        var items = recipients
            .Where(r => r.Key != message.AuthorUserId && !alreadyNotified.Contains(r.Key))
            .Select(r => new ActivityItem
            {
                Id = Guid.NewGuid(),
                UserId = r.Key,
                Type = r.Value,
                ActorUserId = message.AuthorUserId,
                WorkspaceId = workspaceId,
                ChannelId = message.ChannelId,
                ConversationId = message.ConversationId,
                MessageId = message.Id,
                CreatedAt = now,
                ExpiresAt = now + ActivityLifetime,
            })
            .ToList();
        db.ActivityItems.AddRange(items);
        return items;
    }

    public static ActivityDto ToDto(ActivityItem item) =>
        new(item.Id, item.Type, item.ActorUserId, item.WorkspaceId, item.ChannelId, item.ConversationId, item.MessageId, item.CreatedAt);
}
