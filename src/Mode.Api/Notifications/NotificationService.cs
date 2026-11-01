using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mode.Api.Authorization;
using Mode.Api.Jobs;
using Mode.Api.Messaging;
using Mode.Api.Presence;
using Mode.Api.Realtime;
using Mode.Domain.Entities;
using Mode.Infrastructure.Data;

namespace Mode.Api.Notifications;

public sealed record NotificationRequest(
    string RecipientId, ActivityType Type, string ActorUserId, Guid? WorkspaceId, Guid? ChannelId, Guid? ConversationId, Guid? MessageId);

public sealed record PreparedNotification(ActivityItem Item, bool Banner);

public sealed record NotificationBanner(Guid ActivityId, ActivityType Type, string Title, string Body, Guid? ChannelId, Guid? ConversationId, Guid? MessageId);

/// <summary>
/// The single path from an event to a user's feed, banner, push and email. Decides per recipient using their category
/// settings, room mute and Do not disturb; only people who can read the room are ever notified.
/// </summary>
public sealed class NotificationService(
    ModeDbContext db, AccessPolicy policy, PreferencesStore preferences, PresenceTracker presence, JobQueue jobs,
    NotificationContent content, RealtimeNotifier notifier, IOptions<PushOptions> push, TimeProvider clock)
{
    public static readonly TimeSpan ActivityLifetime = TimeSpan.FromDays(30);

    public static NotificationCategory CategoryOf(ActivityType type, Guid? conversationId) =>
        type is ActivityType.Mention or ActivityType.ChannelMention ? NotificationCategory.Mentions
        : conversationId is not null ? NotificationCategory.Chats
        : NotificationCategory.Channels;

    private static int Priority(ActivityType type) => type switch
    {
        ActivityType.Mention => 0,
        ActivityType.ChannelMention => 1,
        ActivityType.Reply => 2,
        ActivityType.Reaction => 3,
        ActivityType.ChatMessage => 4,
        _ => 5,
    };

    /// <summary>Adds feed items and push jobs to the current unit of work. Call <see cref="DeliverAsync"/> after it commits.</summary>
    public async Task<IReadOnlyList<PreparedNotification>> PrepareAsync(IEnumerable<NotificationRequest> requests, CancellationToken ct)
    {
        // One item per person per message, keeping the most specific reason.
        var chosen = requests
            .Where(r => r.RecipientId != r.ActorUserId)
            .GroupBy(r => (r.RecipientId, r.MessageId, r.ChannelId, r.ConversationId, r.WorkspaceId))
            .Select(g => g.MinBy(r => Priority(r.Type))!)
            .ToList();
        if (chosen.Count == 0) return [];

        chosen = await DropAlreadyNotifiedAsync(chosen, ct);
        chosen = await KeepReadersAsync(chosen, ct);
        if (chosen.Count == 0) return [];

        var recipients = chosen.Select(r => r.RecipientId).Distinct().ToList();
        var settings = await preferences.GetManyAsync(recipients, ct);
        var muted = (await db.ReadMarkers
                .Where(m => recipients.Contains(m.UserId) && m.IsMuted)
                .Select(m => new { m.UserId, Room = m.ChannelId ?? m.ConversationId })
                .ToListAsync(ct))
            .Select(m => (m.UserId, m.Room!.Value))
            .ToHashSet();

        var now = clock.GetUtcNow();
        var prepared = new List<PreparedNotification>();
        foreach (var request in chosen)
        {
            var userSettings = settings[request.RecipientId];
            var mode = userSettings.For(CategoryOf(request.Type, request.ConversationId));
            if (mode == DeliveryMode.Off) continue;

            // A muted room stays silent except for a direct @mention of this person.
            var room = request.ChannelId ?? request.ConversationId;
            if (room is { } roomId && muted.Contains((request.RecipientId, roomId)) && request.Type != ActivityType.Mention) continue;

            var item = new ActivityItem
            {
                Id = Guid.NewGuid(),
                UserId = request.RecipientId,
                Type = request.Type,
                ActorUserId = request.ActorUserId,
                WorkspaceId = request.WorkspaceId,
                ChannelId = request.ChannelId,
                ConversationId = request.ConversationId,
                MessageId = request.MessageId,
                CreatedAt = now,
                ExpiresAt = now + ActivityLifetime,
            };
            db.ActivityItems.Add(item);

            var banner = mode == DeliveryMode.BannerAndFeed && !presence.IsDoNotDisturb(request.RecipientId);
            if (banner && userSettings.PushEnabled && push.Value.IsConfigured)
                jobs.Enqueue(JobTypes.PushSend, new ActivityJobPayload(item.Id), maxAttempts: 3);
            prepared.Add(new PreparedNotification(item, banner));
        }
        return prepared;
    }

    /// <summary>Pushes feed updates and banners over SignalR once the items are saved.</summary>
    public async Task DeliverAsync(IReadOnlyList<PreparedNotification> prepared, CancellationToken ct)
    {
        if (prepared.Count == 0) return;

        var recipients = prepared.Select(p => p.Item.UserId).Distinct().ToList();
        var unread = await db.ActivityItems
            .Where(a => recipients.Contains(a.UserId) && a.ReadAt == null && a.ExpiresAt > clock.GetUtcNow())
            .GroupBy(a => a.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count, ct);
        var texts = await content.BuildAsync(prepared.Where(p => p.Banner).Select(p => p.Item).ToList(), ct);

        foreach (var (item, banner) in prepared)
        {
            await notifier.ActivityCreatedAsync(item.UserId, ToDto(item), unread.GetValueOrDefault(item.UserId));
            if (banner && texts.TryGetValue(item.Id, out var text))
                await notifier.NotificationAsync(item.UserId,
                    new NotificationBanner(item.Id, item.Type, text.Title, text.Body, item.ChannelId, item.ConversationId, item.MessageId));
        }
    }

    public static ActivityDto ToDto(ActivityItem item) =>
        new(item.Id, item.Type, item.ActorUserId, item.WorkspaceId, item.ChannelId, item.ConversationId, item.MessageId, item.CreatedAt);

    private async Task<List<NotificationRequest>> DropAlreadyNotifiedAsync(List<NotificationRequest> requests, CancellationToken ct)
    {
        var messageIds = requests.Where(r => r.MessageId is not null).Select(r => r.MessageId!.Value).Distinct().ToList();
        if (messageIds.Count == 0) return requests;

        var notified = (await db.ActivityItems
                .Where(a => a.MessageId != null && messageIds.Contains(a.MessageId.Value))
                .Select(a => new { a.UserId, MessageId = a.MessageId!.Value })
                .ToListAsync(ct))
            .Select(a => (a.UserId, a.MessageId))
            .ToHashSet();
        return requests.Where(r => r.MessageId is null || !notified.Contains((r.RecipientId, r.MessageId.Value))).ToList();
    }

    private async Task<List<NotificationRequest>> KeepReadersAsync(List<NotificationRequest> requests, CancellationToken ct)
    {
        var kept = new List<NotificationRequest>();
        foreach (var group in requests.GroupBy(r => (r.ChannelId, r.ConversationId)))
        {
            RoomRef? room = group.Key.ChannelId is { } channelId ? new RoomRef(RoomType.Channel, channelId)
                : group.Key.ConversationId is { } conversationId ? new RoomRef(RoomType.Conversation, conversationId)
                : null;
            if (room is null)
            {
                kept.AddRange(group);
                continue;
            }
            var readers = (await policy.ListReadersAsync(room, ct)).ToHashSet();
            kept.AddRange(group.Where(r => readers.Contains(r.RecipientId)));
        }
        return kept;
    }
}

public sealed record ActivityJobPayload(Guid ActivityItemId);
