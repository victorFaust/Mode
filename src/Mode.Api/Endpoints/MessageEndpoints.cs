using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mode.Api.Auth;
using Mode.Api.Authorization;
using Mode.Api.Files;
using Mode.Api.Messaging;
using Mode.Api.Notifications;
using Mode.Api.Realtime;
using Mode.Domain.Entities;
using Mode.Infrastructure.Data;

namespace Mode.Api.Endpoints;

public static class MessageEndpoints
{
    public static RouteGroupBuilder MapMessageEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/channels/{channelId:guid}/messages",
            (Guid channelId, long? before, long? after, int? limit, CurrentUser user, AccessPolicy policy, MessageStore store, CancellationToken ct) =>
                List(new RoomRef(RoomType.Channel, channelId), before, after, limit, user, policy, store, ct)).WithTags("Messages");
        api.MapGet("/conversations/{conversationId:guid}/messages",
            (Guid conversationId, long? before, long? after, int? limit, CurrentUser user, AccessPolicy policy, MessageStore store, CancellationToken ct) =>
                List(new RoomRef(RoomType.Conversation, conversationId), before, after, limit, user, policy, store, ct)).WithTags("Messages");
        api.MapPost("/channels/{channelId:guid}/messages",
            (Guid channelId, SendMessageRequest body, IOptions<FilesOptions> files, [AsParameters] MessageDependencies deps, CancellationToken ct) =>
                Send(new RoomRef(RoomType.Channel, channelId), body, files.Value, deps, ct)).WithTags("Messages");
        api.MapPost("/conversations/{conversationId:guid}/messages",
            (Guid conversationId, SendMessageRequest body, IOptions<FilesOptions> files, [AsParameters] MessageDependencies deps, CancellationToken ct) =>
                Send(new RoomRef(RoomType.Conversation, conversationId), body, files.Value, deps, ct)).WithTags("Messages");

        var messages = api.MapGroup("/messages/{messageId:guid}").WithTags("Messages");
        messages.MapGet("/", Get);
        messages.MapGet("/replies", Replies);
        messages.MapPatch("/", Edit);
        messages.MapDelete("/", Delete);
        messages.MapPut("/reactions/{emoji}", AddReaction);
        messages.MapDelete("/reactions/{emoji}", RemoveReaction);
        return api;
    }

    /// <summary>Services shared by the write handlers.</summary>
    public sealed record MessageDependencies(
        CurrentUser User, ModeDbContext Db, AccessPolicy Policy, MessageStore Store, MentionProcessor Mentions,
        NotificationService Notifications, RealtimeNotifier Notifier, TimeProvider Clock);

    /// <summary>
    /// Newest first by default. With <c>after</c> (e.g. the last read sequence) returns the oldest messages after it,
    /// so a client can open a room at the first unread message.
    /// </summary>
    private static async Task<IResult> List(
        RoomRef room, long? before, long? after, int? limit, CurrentUser user, AccessPolicy policy, MessageStore store, CancellationToken ct)
    {
        if (!(await policy.ForRoomAsync(user, room, ct)).Has(Permission.Read)) return Problems.NotFound();
        if (before is not null && after is not null) return Problems.Invalid("after", "Use either before or after, not both.");
        return TypedResults.Ok(after is { } from
            ? await store.TopLevelPageAfterAsync(room, from, limit ?? 50, ct)
            : await store.TopLevelPageAsync(room, before, limit ?? 50, ct));
    }

    private static async Task<IResult> Get(Guid messageId, CurrentUser user, AccessPolicy policy, MessageStore store, CancellationToken ct)
    {
        if (!(await policy.ForMessageAsync(user, messageId, ct)).Has(Permission.Read)) return Problems.NotFound();
        return TypedResults.Ok(await store.GetDtoAsync(messageId, ct));
    }

    private static async Task<IResult> Replies(
        Guid messageId, long? after, int? limit, CurrentUser user, AccessPolicy policy, MessageStore store, CancellationToken ct)
    {
        if (!(await policy.ForMessageAsync(user, messageId, ct)).Has(Permission.Read)) return Problems.NotFound();
        return TypedResults.Ok(await store.RepliesAsync(messageId, after, limit ?? 50, ct));
    }

    private static async Task<IResult> Send(RoomRef room, SendMessageRequest body, FilesOptions fileOptions, MessageDependencies deps, CancellationToken ct)
    {
        var (user, db, policy, store, mentions, notifications, notifier, clock) = deps;
        var permission = await policy.ForRoomAsync(user, room, ct);
        if (!permission.Has(Permission.Read)) return Problems.NotFound();
        if (!permission.Has(Permission.Post)) return Problems.Forbidden("This channel is archived.");
        if (body.Id is not { } id || id == Guid.Empty) return Problems.Invalid("id", "A client-generated message id is required.");
        if (!MessageContent.TrySanitize(body.BodyMarkdown, out var content, out var error)) return Problems.Invalid("bodyMarkdown", error);

        bool IsRetryOf(Message existing) =>
            existing.AuthorUserId == user.Id && RoomRef.Of(existing) == room && existing.ParentMessageId == body.ParentMessageId &&
            (existing.EditedAt is not null || existing.DeletedAt is not null || existing.BodyMarkdown == content.Markdown);

        async Task<IResult> ExistingAsync(Message existing) => IsRetryOf(existing)
            ? TypedResults.Ok(await store.GetDtoAsync(existing.Id, ct))
            : Problems.Conflict("This message id is already in use.");

        var existing = await db.Messages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, ct);
        if (existing is not null) return await ExistingAsync(existing);

        if (body.ParentMessageId is { } parentId)
        {
            var parent = await store.InRoom(room).AsNoTracking().SingleOrDefaultAsync(m => m.Id == parentId, ct);
            if (parent is null) return Problems.Invalid("parentMessageId", "The parent message is not in this room.");
            if (parent.ParentMessageId is not null) return Problems.Invalid("parentMessageId", "Replies can only be one level deep.");
            if (parent.DeletedAt is not null) return Problems.Invalid("parentMessageId", "Cannot reply to a deleted message.");
        }

        var fileIds = body.FileIds?.Distinct().ToList() ?? [];
        if (fileIds.Count > fileOptions.MaxAttachmentsPerMessage)
            return Problems.Invalid("fileIds", $"Attach at most {fileOptions.MaxAttachmentsPerMessage} files.");
        List<AttachmentTarget> files = fileIds.Count == 0 ? [] : await db.FileAssets
            .Where(f => fileIds.Contains(f.Id) && f.DeletedAt == null &&
                (room.Type == RoomType.Channel ? f.ChannelId == room.Id : f.ConversationId == room.Id))
            .Select(f => new AttachmentTarget(f.Id, f.CurrentVersionNo))
            .ToListAsync(ct);
        if (files.Count != fileIds.Count) return Problems.Invalid("fileIds", "Attach only files uploaded to this channel or chat.");

        var workspaceId = room.Type == RoomType.Channel
            ? await db.Channels.Where(c => c.Id == room.Id).Select(c => (Guid?)c.WorkspaceId).SingleAsync(ct)
            : null;

        IReadOnlyList<PreparedNotification> activity;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var sequence = await store.NextSequenceAsync(room, ct);
            var now = clock.GetUtcNow();
            var message = new Message
            {
                Id = id,
                ChannelId = room.Type == RoomType.Channel ? room.Id : null,
                ConversationId = room.Type == RoomType.Conversation ? room.Id : null,
                ParentMessageId = body.ParentMessageId,
                Sequence = sequence,
                UpdatedSequence = sequence,
                AuthorUserId = user.Id,
                BodyMarkdown = content.Markdown,
                CreatedAt = now,
            };
            foreach (var file in files)
                message.Attachments.Add(new MessageAttachment { FileAssetId = file.Id, VersionNo = file.CurrentVersionNo });
            db.Messages.Add(message);

            var requests = await mentions.ApplyAsync(message, room, workspaceId, content.Mentions, ct);
            NotificationRequest Request(string recipient, ActivityType type) =>
                new(recipient, type, user.Id, workspaceId, message.ChannelId, message.ConversationId, id);

            if (body.ParentMessageId is { } replyTo)
            {
                var parent = await db.Messages.SingleAsync(m => m.Id == replyTo, ct);
                parent.ReplyCount++;
                parent.LastReplyAt = now;
                parent.UpdatedSequence = sequence;
                requests.Add(Request(parent.AuthorUserId, ActivityType.Reply));
            }
            if (room.Type == RoomType.Conversation)
                requests.AddRange((await db.ConversationMembers.Where(m => m.ConversationId == room.Id).Select(m => m.UserId).ToListAsync(ct))
                    .Select(member => Request(member, ActivityType.ChatMessage)));

            activity = await notifications.PrepareAsync(requests, ct);
            try
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateException ex) when (Problems.IsUniqueViolation(ex))
            {
                // A concurrent retry with the same id won the race.
                await transaction.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                return await ExistingAsync(await db.Messages.AsNoTracking().SingleAsync(m => m.Id == id, ct));
            }
        }

        var dto = await store.GetDtoAsync(id, ct);
        await notifier.MessageCreatedAsync(dto);
        await notifications.DeliverAsync(activity, ct);
        return TypedResults.Created($"/api/v1/messages/{id}", dto);
    }

    private static async Task<IResult> Edit(Guid messageId, EditMessageRequest body, [AsParameters] MessageDependencies deps, CancellationToken ct)
    {
        var (user, db, policy, store, mentions, notifications, notifier, clock) = deps;
        var permission = await policy.ForMessageAsync(user, messageId, ct);
        if (!permission.Has(Permission.Read)) return Problems.NotFound();
        if (!permission.Has(Permission.Edit)) return Problems.Forbidden("Only the author can edit, and not after deletion.");
        if (!MessageContent.TrySanitize(body.BodyMarkdown, out var content, out var error)) return Problems.Invalid("bodyMarkdown", error);

        var room = RoomRef.Of(await db.Messages.AsNoTracking().SingleAsync(m => m.Id == messageId, ct));
        var workspaceId = room.Type == RoomType.Channel
            ? await db.Channels.Where(c => c.Id == room.Id).Select(c => (Guid?)c.WorkspaceId).SingleAsync(ct)
            : null;

        IReadOnlyList<PreparedNotification> activity;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var sequence = await store.NextSequenceAsync(room, ct);
            // Loaded after taking the room lock so concurrent edits never overwrite each other's history.
            var message = await db.Messages.Include(m => m.Mentions).SingleAsync(m => m.Id == messageId, ct);
            if (message.DeletedAt is not null) return Problems.Conflict("The message was deleted.");
            if (message.BodyMarkdown == content.Markdown) return TypedResults.Ok(await store.GetDtoAsync(messageId, ct));

            var now = clock.GetUtcNow();
            db.MessageEdits.Add(new MessageEdit
            {
                Id = Guid.NewGuid(), MessageId = messageId, Kind = MessageChangeKind.Edit,
                PreviousBodyMarkdown = message.BodyMarkdown, ChangedByUserId = user.Id, ChangedAt = now,
            });
            message.BodyMarkdown = content.Markdown;
            message.EditedAt = now;
            message.UpdatedSequence = sequence;
            activity = await notifications.PrepareAsync(await mentions.ApplyAsync(message, room, workspaceId, content.Mentions, ct), ct);

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        var dto = await store.GetDtoAsync(messageId, ct);
        await notifier.MessageEditedAsync(dto);
        await notifications.DeliverAsync(activity, ct);
        return TypedResults.Ok(dto);
    }

    private static async Task<IResult> Delete(Guid messageId, [AsParameters] MessageDependencies deps, CancellationToken ct)
    {
        var (user, db, policy, store, _, _, notifier, clock) = deps;
        var permission = await policy.ForMessageAsync(user, messageId, ct);
        if (!permission.Has(Permission.Read)) return Problems.NotFound();
        if (!permission.Has(Permission.Delete)) return Problems.Forbidden();

        var room = RoomRef.Of(await db.Messages.AsNoTracking().SingleAsync(m => m.Id == messageId, ct));
        long sequence;
        DateTimeOffset now;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            sequence = await store.NextSequenceAsync(room, ct);
            var message = await db.Messages.SingleAsync(m => m.Id == messageId, ct);
            if (message.DeletedAt is not null) return TypedResults.NoContent();

            now = clock.GetUtcNow();
            // The original stays in MessageEdits for compliance; the visible body is emptied and drops out of search.
            db.MessageEdits.Add(new MessageEdit
            {
                Id = Guid.NewGuid(), MessageId = messageId, Kind = MessageChangeKind.Delete,
                PreviousBodyMarkdown = message.BodyMarkdown, ChangedByUserId = user.Id, ChangedAt = now,
            });
            message.BodyMarkdown = "";
            message.DeletedAt = now;
            message.DeletedByUserId = user.Id;
            message.UpdatedSequence = sequence;

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        await notifier.MessageDeletedAsync(room, sequence, messageId, now);
        return TypedResults.NoContent();
    }

    private static Task<IResult> AddReaction(Guid messageId, string emoji, [AsParameters] MessageDependencies deps, CancellationToken ct) =>
        ChangeReaction(messageId, emoji, add: true, deps, ct);

    private static Task<IResult> RemoveReaction(Guid messageId, string emoji, [AsParameters] MessageDependencies deps, CancellationToken ct) =>
        ChangeReaction(messageId, emoji, add: false, deps, ct);

    private static async Task<IResult> ChangeReaction(Guid messageId, string emoji, bool add, MessageDependencies deps, CancellationToken ct)
    {
        var (user, db, policy, store, _, notifications, notifier, clock) = deps;
        var permission = await policy.ForMessageAsync(user, messageId, ct);
        if (!permission.Has(Permission.Read)) return Problems.NotFound();
        if (!permission.Has(Permission.Post)) return Problems.Forbidden("This channel is archived.");
        if (!IsValidEmoji(emoji)) return Problems.Invalid("emoji", "Use a single emoji or a :shortcode: of up to 32 characters.");

        var room = RoomRef.Of(await db.Messages.AsNoTracking().SingleAsync(m => m.Id == messageId, ct));
        var workspaceId = room.Type == RoomType.Channel
            ? await db.Channels.Where(c => c.Id == room.Id).Select(c => (Guid?)c.WorkspaceId).SingleAsync(ct)
            : null;
        long sequence;
        IReadOnlyList<PreparedNotification> activity = [];
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            sequence = await store.NextSequenceAsync(room, ct);
            var message = await db.Messages.SingleAsync(m => m.Id == messageId, ct);
            if (message.DeletedAt is not null) return Problems.Conflict("The message was deleted.");

            var existing = await db.Reactions.SingleOrDefaultAsync(r => r.MessageId == messageId && r.UserId == user.Id && r.Emoji == emoji, ct);
            if (add == (existing is not null))
            {
                // Already in the requested state: roll back so no sequence number is spent.
                await transaction.RollbackAsync(ct);
                return TypedResults.Ok(await store.ReactionSummaryAsync(messageId, ct));
            }

            if (add)
            {
                db.Reactions.Add(new Reaction { MessageId = messageId, UserId = user.Id, Emoji = emoji, CreatedAt = clock.GetUtcNow() });
                activity = await notifications.PrepareAsync(
                    [new(message.AuthorUserId, ActivityType.Reaction, user.Id, workspaceId, message.ChannelId, message.ConversationId, messageId)], ct);
            }
            else
            {
                db.Reactions.Remove(existing!);
            }
            message.UpdatedSequence = sequence;

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        var reactions = await store.ReactionSummaryAsync(messageId, ct);
        await notifier.ReactionChangedAsync(room, sequence, messageId, reactions);
        await notifications.DeliverAsync(activity, ct);
        return TypedResults.Ok(reactions);
    }

    private static bool IsValidEmoji(string emoji) =>
        emoji.Length is > 0 and <= 32 && !emoji.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '<' or '>' or '&' or '"' or '\'');

    private sealed record AttachmentTarget(Guid Id, int CurrentVersionNo);
}
