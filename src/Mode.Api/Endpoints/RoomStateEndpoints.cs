using Microsoft.EntityFrameworkCore;
using Mode.Api.Auth;
using Mode.Api.Authorization;
using Mode.Api.Messaging;
using Mode.Api.Presence;
using Mode.Api.Realtime;
using Mode.Domain.Directory;
using Mode.Domain.Entities;
using Mode.Infrastructure.Data;

namespace Mode.Api.Endpoints;

/// <summary>
/// Per room: messages after the read marker (excluding the user's own), unread @mentions of the user or room,
/// and where the first unread message is so the client can open the room there.
/// </summary>
public sealed record RoomUnreadDto(RoomRef Room, int UnreadCount, int MentionCount, long LastReadSequence, long? FirstUnreadSequence, bool IsMuted);

public sealed record MarkReadRequest(long? Sequence);

public sealed record MuteRequest(bool? Muted);

public sealed record SetPresenceRequest(PresenceStatus? Status, string? Message);

public static class RoomStateEndpoints
{
    public const int MaxPresenceMessage = 280;
    public const int MaxPresenceLookup = 200;

    public static RouteGroupBuilder MapRoomStateEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/me/unread", Unread).WithTags("Unread");
        api.MapPut("/channels/{channelId:guid}/read",
            (Guid channelId, MarkReadRequest body, [AsParameters] RoomStateDependencies deps, CancellationToken ct) =>
                MarkRead(new RoomRef(RoomType.Channel, channelId), body, deps, ct)).WithTags("Unread");
        api.MapPut("/conversations/{conversationId:guid}/read",
            (Guid conversationId, MarkReadRequest body, [AsParameters] RoomStateDependencies deps, CancellationToken ct) =>
                MarkRead(new RoomRef(RoomType.Conversation, conversationId), body, deps, ct)).WithTags("Unread");
        api.MapPut("/channels/{channelId:guid}/mute",
            (Guid channelId, MuteRequest body, [AsParameters] RoomStateDependencies deps, CancellationToken ct) =>
                Mute(new RoomRef(RoomType.Channel, channelId), body, deps, ct)).WithTags("Unread");
        api.MapPut("/conversations/{conversationId:guid}/mute",
            (Guid conversationId, MuteRequest body, [AsParameters] RoomStateDependencies deps, CancellationToken ct) =>
                Mute(new RoomRef(RoomType.Conversation, conversationId), body, deps, ct)).WithTags("Unread");

        api.MapPut("/me/presence", SetPresence).WithTags("Presence");
        api.MapGet("/users/presence", GetPresence).WithTags("Presence");
        return api;
    }

    public sealed record RoomStateDependencies(
        CurrentUser User, ModeDbContext Db, AccessPolicy Policy, MessageStore Store, RealtimeNotifier Notifier, TimeProvider Clock);

    public sealed class UnreadRow
    {
        public Guid? ChannelId { get; set; }
        public Guid? ConversationId { get; set; }
        public int UnreadCount { get; set; }
        public long FirstUnreadSequence { get; set; }
    }

    private static async Task<IResult> Unread(CurrentUser user, ModeDbContext db, AccessPolicy policy, TimeProvider clock, CancellationToken ct)
    {
        var rooms = await policy.ReadableRoomsAsync(user, ct);
        var channelIds = rooms.ChannelIds.ToArray();
        var conversationIds = rooms.ConversationIds.ToArray();

        // Top-level and reply messages both count; deleted ones and the user's own do not.
        var counts = await db.Database.SqlQuery<UnreadRow>($"""
            SELECT m."ChannelId", m."ConversationId", count(*)::int AS "UnreadCount", min(m."Sequence") AS "FirstUnreadSequence"
            FROM "Messages" m
            LEFT JOIN "ReadMarkers" r ON r."UserId" = {user.Id}
                AND (r."ChannelId" = m."ChannelId" OR r."ConversationId" = m."ConversationId")
            WHERE (m."ChannelId" = ANY({channelIds}) OR m."ConversationId" = ANY({conversationIds}))
              AND m."DeletedAt" IS NULL
              AND m."AuthorUserId" <> {user.Id}
              AND m."Sequence" > coalesce(r."LastReadSequence", 0)
            GROUP BY m."ChannelId", m."ConversationId"
            """).ToListAsync(ct);

        var now = clock.GetUtcNow();
        var mentions = await db.ActivityItems
            .Where(a => a.UserId == user.Id && a.ReadAt == null && a.ExpiresAt > now &&
                (a.Type == ActivityType.Mention || a.Type == ActivityType.ChannelMention))
            .GroupBy(a => new { a.ChannelId, a.ConversationId })
            .Select(g => new { g.Key.ChannelId, g.Key.ConversationId, Count = g.Count() })
            .ToListAsync(ct);
        var markers = await db.ReadMarkers.Where(r => r.UserId == user.Id).AsNoTracking().ToListAsync(ct);

        static Guid RoomId(Guid? channelId, Guid? conversationId) => channelId ?? conversationId!.Value;
        var countByRoom = counts.ToDictionary(c => RoomId(c.ChannelId, c.ConversationId));
        var mentionsByRoom = mentions.Where(m => m.ChannelId is not null || m.ConversationId is not null)
            .ToDictionary(m => RoomId(m.ChannelId, m.ConversationId), m => m.Count);
        var markerByRoom = markers.ToDictionary(m => RoomId(m.ChannelId, m.ConversationId));

        var all = channelIds.Select(id => new RoomRef(RoomType.Channel, id))
            .Concat(conversationIds.Select(id => new RoomRef(RoomType.Conversation, id)));
        return TypedResults.Ok(all
            .Select(room =>
            {
                var count = countByRoom.GetValueOrDefault(room.Id);
                var marker = markerByRoom.GetValueOrDefault(room.Id);
                return new RoomUnreadDto(room, count?.UnreadCount ?? 0, mentionsByRoom.GetValueOrDefault(room.Id),
                    marker?.LastReadSequence ?? 0, count?.FirstUnreadSequence, marker?.IsMuted ?? false);
            })
            .Where(r => r.UnreadCount > 0 || r.MentionCount > 0 || r.IsMuted)
            .ToList());
    }

    /// <summary>Moves the read marker forward (never back) and clears feed items for messages up to it.</summary>
    private static async Task<IResult> MarkRead(RoomRef room, MarkReadRequest body, RoomStateDependencies deps, CancellationToken ct)
    {
        var (user, db, policy, store, notifier, clock) = deps;
        if (!(await policy.ForRoomAsync(user, room, ct)).Has(Permission.Read)) return Problems.NotFound();
        if (body.Sequence is not { } requested || requested < 0) return Problems.Invalid("sequence", "Required and not negative.");

        var sequence = Math.Min(requested, await store.CurrentSequenceAsync(room, ct));
        var now = clock.GetUtcNow();
        var id = Guid.NewGuid();
        var saved = room.Type == RoomType.Channel
            ? await db.Database.SqlQuery<long>($"""
                INSERT INTO "ReadMarkers" ("Id", "UserId", "ChannelId", "LastReadSequence", "IsMuted", "UpdatedAt")
                VALUES ({id}, {user.Id}, {room.Id}, {sequence}, false, {now})
                ON CONFLICT ("UserId", "ChannelId") WHERE "ChannelId" IS NOT NULL DO UPDATE
                SET "LastReadSequence" = GREATEST("ReadMarkers"."LastReadSequence", EXCLUDED."LastReadSequence"), "UpdatedAt" = EXCLUDED."UpdatedAt"
                RETURNING "LastReadSequence" AS "Value"
                """).ToListAsync(ct)
            : await db.Database.SqlQuery<long>($"""
                INSERT INTO "ReadMarkers" ("Id", "UserId", "ConversationId", "LastReadSequence", "IsMuted", "UpdatedAt")
                VALUES ({id}, {user.Id}, {room.Id}, {sequence}, false, {now})
                ON CONFLICT ("UserId", "ConversationId") WHERE "ConversationId" IS NOT NULL DO UPDATE
                SET "LastReadSequence" = GREATEST("ReadMarkers"."LastReadSequence", EXCLUDED."LastReadSequence"), "UpdatedAt" = EXCLUDED."UpdatedAt"
                RETURNING "LastReadSequence" AS "Value"
                """).ToListAsync(ct);
        var lastRead = saved[0];

        var inRoom = store.InRoom(room).Where(m => m.Sequence <= lastRead).Select(m => m.Id);
        var cleared = await db.ActivityItems
            .Where(a => a.UserId == user.Id && a.ReadAt == null && a.MessageId != null && inRoom.Contains(a.MessageId.Value))
            .Select(a => a.Id)
            .ToListAsync(ct);
        if (cleared.Count > 0)
        {
            await db.ActivityItems.Where(a => cleared.Contains(a.Id)).ExecuteUpdateAsync(s => s.SetProperty(a => a.ReadAt, now), ct);
            var unread = await db.ActivityItems.CountAsync(a => a.UserId == user.Id && a.ReadAt == null && a.ExpiresAt > now, ct);
            await notifier.ActivityReadAsync(user.Id, cleared, unread);
        }

        await notifier.ReadMarkerChangedAsync(user.Id, room, lastRead);
        return TypedResults.Ok(new { lastReadSequence = lastRead });
    }

    /// <summary>A muted room still counts unread messages but only notifies for direct @mentions.</summary>
    private static async Task<IResult> Mute(RoomRef room, MuteRequest body, RoomStateDependencies deps, CancellationToken ct)
    {
        var (user, db, policy, _, _, clock) = deps;
        if (!(await policy.ForRoomAsync(user, room, ct)).Has(Permission.Read)) return Problems.NotFound();
        if (body.Muted is not { } muted) return Problems.Invalid("muted", "Required.");

        var now = clock.GetUtcNow();
        var id = Guid.NewGuid();
        if (room.Type == RoomType.Channel)
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "ReadMarkers" ("Id", "UserId", "ChannelId", "LastReadSequence", "IsMuted", "UpdatedAt")
                VALUES ({id}, {user.Id}, {room.Id}, 0, {muted}, {now})
                ON CONFLICT ("UserId", "ChannelId") WHERE "ChannelId" IS NOT NULL DO UPDATE
                SET "IsMuted" = EXCLUDED."IsMuted", "UpdatedAt" = EXCLUDED."UpdatedAt"
                """, ct);
        else
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "ReadMarkers" ("Id", "UserId", "ConversationId", "LastReadSequence", "IsMuted", "UpdatedAt")
                VALUES ({id}, {user.Id}, {room.Id}, 0, {muted}, {now})
                ON CONFLICT ("UserId", "ConversationId") WHERE "ConversationId" IS NOT NULL DO UPDATE
                SET "IsMuted" = EXCLUDED."IsMuted", "UpdatedAt" = EXCLUDED."UpdatedAt"
                """, ct);
        return TypedResults.Ok(new { muted });
    }

    private static async Task<IResult> SetPresence(
        SetPresenceRequest body, CurrentUser user, PresenceTracker presence, PresenceNotifier notifier)
    {
        if (body.Status is not { } status || !Enum.IsDefined(status) || status == PresenceStatus.Offline)
            return Problems.Invalid("status", "Use Available, Busy, DoNotDisturb or Away.");
        if (body.Message is { Length: > MaxPresenceMessage })
            return Problems.Invalid("message", $"At most {MaxPresenceMessage} characters.");
        if (body.Message is not null && body.Message.Any(char.IsControl))
            return Problems.Invalid("message", "Control characters are not allowed.");

        await notifier.PublishAsync(presence.SetManual(user.Id, status, body.Message));
        return TypedResults.Ok(presence.Get(user.Id));
    }

    /// <summary>Status for people the caller shares a workspace or chat with; others are left out.</summary>
    private static async Task<IResult> GetPresence(
        string? ids, CurrentUser user, AccessPolicy policy, PresenceTracker presence, IUserDirectory directory, CancellationToken ct)
    {
        var requested = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => directory.TryNormalizeId(id, out var normalized) ? normalized : null)
            .OfType<string>()
            .Distinct()
            .ToList();
        if (requested.Count == 0) return Problems.Invalid("ids", "Required.");
        if (requested.Count > MaxPresenceLookup) return Problems.Invalid("ids", $"At most {MaxPresenceLookup} ids.");

        var visible = await policy.VisiblePresenceAsync(user, requested, ct);
        return TypedResults.Ok(requested.Where(visible.Contains).Select(presence.Get).ToList());
    }
}
