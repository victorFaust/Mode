using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mode.Api.Auth;
using Mode.Api.Authorization;
using Mode.Api.Files;
using Mode.Api.Governance;
using Mode.Api.Messaging;
using Mode.Api.Notifications;
using Mode.Api.Realtime;
using Mode.Domain.Directory;
using Mode.Domain.Entities;
using Mode.Infrastructure.Data;

namespace Mode.Api.Endpoints;

public static class MessageEndpoints
{
    public const int MaxPinsPerRoom = 20;
    public static RouteGroupBuilder MapMessageEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/channels/{channelId:guid}/messages",
            (Guid channelId, long? before, long? after, int? limit, CurrentUser user, AccessPolicy policy, MessageStore store, CancellationToken ct) =>
                List(new RoomRef(RoomType.Channel, channelId), before, after, limit, user, policy, store, ct)).WithTags("Messages").Produces<MessagePage>();
        api.MapGet("/conversations/{conversationId:guid}/messages",
            (Guid conversationId, long? before, long? after, int? limit, CurrentUser user, AccessPolicy policy, MessageStore store, CancellationToken ct) =>
                List(new RoomRef(RoomType.Conversation, conversationId), before, after, limit, user, policy, store, ct)).WithTags("Messages").Produces<MessagePage>();
        api.MapPost("/channels/{channelId:guid}/messages",
            (Guid channelId, SendMessageRequest body, IOptions<FilesOptions> files, DlpService dlp, [AsParameters] MessageDependencies deps, CancellationToken ct) =>
                Send(new RoomRef(RoomType.Channel, channelId), body, files.Value, dlp, deps, ct)).WithTags("Messages")
            .Produces<MessageDto>(StatusCodes.Status201Created).Produces<MessageDto>();
        api.MapPost("/conversations/{conversationId:guid}/messages",
            (Guid conversationId, SendMessageRequest body, IOptions<FilesOptions> files, DlpService dlp, [AsParameters] MessageDependencies deps, CancellationToken ct) =>
                Send(new RoomRef(RoomType.Conversation, conversationId), body, files.Value, dlp, deps, ct)).WithTags("Messages")
            .Produces<MessageDto>(StatusCodes.Status201Created).Produces<MessageDto>();
        api.MapGet("/channels/{channelId:guid}/pins",
            (Guid channelId, CurrentUser user, AccessPolicy policy, MessageStore store, CancellationToken ct) =>
                Pins(new RoomRef(RoomType.Channel, channelId), user, policy, store, ct)).WithTags("Messages").Produces<PinDto[]>();
        api.MapGet("/conversations/{conversationId:guid}/pins",
            (Guid conversationId, CurrentUser user, AccessPolicy policy, MessageStore store, CancellationToken ct) =>
                Pins(new RoomRef(RoomType.Conversation, conversationId), user, policy, store, ct)).WithTags("Messages").Produces<PinDto[]>();

        var messages = api.MapGroup("/messages/{messageId:guid}").WithTags("Messages");
        messages.MapGet("/", Get).Produces<MessageDto>();
        messages.MapGet("/replies", Replies).Produces<MessageDto[]>();
        messages.MapGet("/history", History).Produces<MessageVersionDto[]>();
        messages.MapPatch("/", Edit).Produces<MessageDto>();
        messages.MapDelete("/", Delete).Produces(StatusCodes.Status204NoContent);
        messages.MapPost("/restore", Restore).Produces<MessageDto>();
        messages.MapPost("/forward", Forward).Produces<MessageDto>(StatusCodes.Status201Created).Produces<MessageDto>();
        messages.MapPut("/pin", Pin).Produces<MessageDto>();
        messages.MapDelete("/pin", Unpin).Produces<MessageDto>();
        messages.MapPut("/reactions/{emoji}", AddReaction).Produces<ReactionSummaryDto[]>();
        messages.MapDelete("/reactions/{emoji}", RemoveReaction).Produces<ReactionSummaryDto[]>();
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

    private static async Task<IResult> Pins(RoomRef room, CurrentUser user, AccessPolicy policy, MessageStore store, CancellationToken ct)
    {
        if (!(await policy.ForRoomAsync(user, room, ct)).Has(Permission.Read)) return Problems.NotFound();
        return TypedResults.Ok(await store.PinsAsync(room, ct));
    }

    /// <summary>The author's own earlier versions. Text removed by a delete is never included.</summary>
    private static async Task<IResult> History(Guid messageId, CurrentUser user, AccessPolicy policy, ModeDbContext db, CancellationToken ct)
    {
        var permission = await policy.ForMessageAsync(user, messageId, ct);
        if (!permission.Has(Permission.Read)) return Problems.NotFound();
        if (!permission.Has(Permission.ViewHistory)) return Problems.Forbidden("Only the author can see earlier versions.");

        var message = await db.Messages.Where(m => m.Id == messageId).Select(m => new { m.BodyMarkdown, m.CreatedAt }).SingleAsync(ct);
        var edits = await db.MessageEdits
            .Where(e => e.MessageId == messageId && e.Kind == MessageChangeKind.Edit)
            .OrderBy(e => e.ChangedAt).ThenBy(e => e.Id)
            .Select(e => new { e.PreviousBodyMarkdown, e.ChangedAt })
            .ToListAsync(ct);

        var versions = new List<MessageVersionDto>(edits.Count + 1);
        var writtenAt = message.CreatedAt;
        foreach (var edit in edits)
        {
            versions.Add(new MessageVersionDto(edit.PreviousBodyMarkdown, writtenAt));
            writtenAt = edit.ChangedAt;
        }
        versions.Add(new MessageVersionDto(message.BodyMarkdown, writtenAt));
        return TypedResults.Ok(versions);
    }

    /// <summary>
    /// Copies a message into another room the user can post in, under an optional note. Mentions in the copy become
    /// plain names and files stay behind (listed by name), so forwarding never widens access or notifies anyone again.
    /// </summary>
    private static async Task<IResult> Forward(
        Guid messageId, ForwardMessageRequest body, IUserDirectory directory, DlpService dlp, [AsParameters] MessageDependencies deps,
        CancellationToken ct)
    {
        var (user, db, policy, _, _, _, _, _) = deps;
        if (!(await policy.ForMessageAsync(user, messageId, ct)).Has(Permission.Read)) return Problems.NotFound();
        if (body.To is not { } to) return Problems.Invalid("to", "Choose a chat to forward to.");
        var target = await policy.ForRoomAsync(user, to, ct);
        if (!target.Has(Permission.Read)) return Problems.Invalid("to", "Choose a chat you belong to.");
        if (!target.Has(Permission.Post)) return Problems.Forbidden("This channel is archived.");
        if (body.Id is not { } id || id == Guid.Empty) return Problems.Invalid("id", "A client-generated message id is required.");

        var note = new SanitizedMessage("", ParsedMentions.None);
        if (!string.IsNullOrWhiteSpace(body.BodyMarkdown) && !MessageContent.TrySanitize(body.BodyMarkdown, out note, out var error))
            return Problems.Invalid("bodyMarkdown", error);

        var original = await db.Messages.AsNoTracking().SingleAsync(m => m.Id == messageId, ct);
        if (original.DeletedAt is not null) return Problems.Conflict("The message was deleted.");

        // Forwarding a forward without a note passes on the original card rather than nesting cards.
        var snapshot = original.ForwardedBodyMarkdown is not null && original.BodyMarkdown.Trim().Length == 0
            ? new ForwardSnapshot(original.ForwardedFromMessageId, original.ForwardedFromUserId!, original.ForwardedFromCreatedAt!.Value,
                original.ForwardedBodyMarkdown)
            : new ForwardSnapshot(original.Id, original.AuthorUserId, original.CreatedAt, await ForwardedCopyAsync(original, db, directory, ct));

        var draft = new NewMessage(id, to, note.Markdown, note.Mentions, null, [], snapshot);
        if (await Problems.DlpGateAsync(dlp, note.Markdown + "\n" + snapshot.BodyMarkdown, body.AcknowledgeDlpWarnings == true, ct) is { } dlpProblem)
            return dlpProblem;
        return await ExistingAsync(draft, deps, ct) ?? await InsertAsync(draft, deps, ct);
    }

    private static async Task<string> ForwardedCopyAsync(Message original, ModeDbContext db, IUserDirectory directory, CancellationToken ct)
    {
        var people = await directory.GetManyAsync(MessageContent.MentionedUserIds(original.BodyMarkdown), ct);
        var copy = MessageContent.FlattenMentions(original.BodyMarkdown,
            id => directory.TryNormalizeId(id, out var normalized) ? people.GetValueOrDefault(normalized)?.DisplayName : null);

        var fileNames = await db.MessageAttachments
            .Where(a => a.MessageId == original.Id)
            .Select(a => a.FileAsset!.Name)
            .ToListAsync(ct);
        if (fileNames.Count > 0)
            copy += "\n\n*Files not forwarded: " + string.Join(", ", fileNames.Select(MessageContent.EscapeText)) + "*";
        return copy;
    }

    private static Task<IResult> Pin(Guid messageId, [AsParameters] MessageDependencies deps, CancellationToken ct) =>
        ChangePin(messageId, pin: true, deps, ct);

    private static Task<IResult> Unpin(Guid messageId, [AsParameters] MessageDependencies deps, CancellationToken ct) =>
        ChangePin(messageId, pin: false, deps, ct);

    private static async Task<IResult> ChangePin(Guid messageId, bool pin, MessageDependencies deps, CancellationToken ct)
    {
        var (user, db, policy, store, _, _, notifier, clock) = deps;
        var permission = await policy.ForMessageAsync(user, messageId, ct);
        if (!permission.Has(Permission.Read)) return Problems.NotFound();
        if (!permission.Has(Permission.Pin)) return Problems.Forbidden("Deleted messages and archived channels can't be pinned.");

        var current = await db.Messages.AsNoTracking().SingleAsync(m => m.Id == messageId, ct);
        if (current.ParentMessageId is not null) return Problems.Invalid("messageId", "Only messages in the chat itself can be pinned, not thread replies.");
        var room = RoomRef.Of(current);

        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var sequence = await store.NextSequenceAsync(room, ct);
            var message = await db.Messages.Include(m => m.Pin).SingleAsync(m => m.Id == messageId, ct);
            if (message.DeletedAt is not null) return Problems.Conflict("The message was deleted.");
            if (pin == (message.Pin is not null))
            {
                await transaction.RollbackAsync(ct);
                return TypedResults.Ok(await store.GetDtoAsync(messageId, ct));
            }

            if (pin)
            {
                if (await store.InRoom(room).CountAsync(m => m.Pin != null, ct) >= MaxPinsPerRoom)
                    return Problems.Conflict($"At most {MaxPinsPerRoom} messages can be pinned. Unpin one first.");
                db.PinnedMessages.Add(new PinnedMessage { MessageId = messageId, PinnedByUserId = user.Id, PinnedAt = clock.GetUtcNow() });
            }
            else
            {
                db.PinnedMessages.Remove(message.Pin!);
            }
            message.UpdatedSequence = sequence;

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        var dto = await store.GetDtoAsync(messageId, ct);
        await notifier.MessageEditedAsync(dto);
        return TypedResults.Ok(dto);
    }

    private static async Task<IResult> Send(
        RoomRef room, SendMessageRequest body, FilesOptions fileOptions, DlpService dlp, MessageDependencies deps, CancellationToken ct)
    {
        var (user, db, policy, store, _, _, _, _) = deps;
        var permission = await policy.ForRoomAsync(user, room, ct);
        if (!permission.Has(Permission.Read)) return Problems.NotFound();
        if (!permission.Has(Permission.Post)) return Problems.Forbidden("This channel is archived.");
        if (body.Id is not { } id || id == Guid.Empty) return Problems.Invalid("id", "A client-generated message id is required.");
        var content = new SanitizedMessage("", ParsedMentions.None);
        var filesOnly = string.IsNullOrWhiteSpace(body.BodyMarkdown) && body.FileIds is { Count: > 0 };
        if (!filesOnly && !MessageContent.TrySanitize(body.BodyMarkdown, out content, out var error)) return Problems.Invalid("bodyMarkdown", error);

        var draft = new NewMessage(id, room, content.Markdown, content.Mentions, body.ParentMessageId, [], null);
        if (await ExistingAsync(draft, deps, ct) is { } existing) return existing;
        if (await Problems.DlpGateAsync(dlp, content.Markdown, body.AcknowledgeDlpWarnings == true, ct) is { } dlpProblem) return dlpProblem;

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

        return await InsertAsync(draft with { Files = files }, deps, ct);
    }

    /// <summary>A message about to be created, by <see cref="Send"/>, <see cref="Forward"/> or an incoming webhook.</summary>
    internal sealed record NewMessage(
        Guid Id, RoomRef Room, string BodyMarkdown, ParsedMentions Mentions, Guid? ParentMessageId,
        IReadOnlyList<AttachmentTarget> Files, ForwardSnapshot? Forward, string? BotName = null);

    internal sealed record ForwardSnapshot(Guid? MessageId, string UserId, DateTimeOffset CreatedAt, string BodyMarkdown);

    /// <summary>
    /// Handles a client retrying with an id that is already used: the same request gets the stored message back,
    /// anything else is a conflict. Returns null when the id is free.
    /// </summary>
    internal static async Task<IResult?> ExistingAsync(NewMessage draft, MessageDependencies deps, CancellationToken ct)
    {
        var existing = await deps.Db.Messages.AsNoTracking().SingleOrDefaultAsync(m => m.Id == draft.Id, ct);
        if (existing is null) return null;

        var isRetry = existing.AuthorUserId == deps.User.Id && RoomRef.Of(existing) == draft.Room &&
            existing.ParentMessageId == draft.ParentMessageId &&
            existing.ForwardedFromMessageId == draft.Forward?.MessageId &&
            (existing.EditedAt is not null || existing.DeletedAt is not null || existing.BodyMarkdown == draft.BodyMarkdown);
        return isRetry
            ? TypedResults.Ok(await deps.Store.GetDtoAsync(existing.Id, ct))
            : Problems.Conflict("This message id is already in use.");
    }

    internal static async Task<IResult> InsertAsync(NewMessage draft, MessageDependencies deps, CancellationToken ct)
    {
        var (user, db, _, store, mentions, notifications, notifier, clock) = deps;
        var (id, room) = (draft.Id, draft.Room);
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
                ParentMessageId = draft.ParentMessageId,
                Sequence = sequence,
                UpdatedSequence = sequence,
                AuthorUserId = user.Id,
                BotName = draft.BotName,
                BodyMarkdown = draft.BodyMarkdown,
                CreatedAt = now,
                ForwardedBodyMarkdown = draft.Forward?.BodyMarkdown,
                ForwardedFromMessageId = draft.Forward?.MessageId,
                ForwardedFromUserId = draft.Forward?.UserId,
                ForwardedFromCreatedAt = draft.Forward?.CreatedAt,
            };
            foreach (var file in draft.Files)
                message.Attachments.Add(new MessageAttachment { FileAssetId = file.Id, VersionNo = file.CurrentVersionNo });
            db.Messages.Add(message);

            var requests = await mentions.ApplyAsync(message, room, workspaceId, draft.Mentions, ct);
            NotificationRequest Request(string recipient, ActivityType type) =>
                new(recipient, type, user.Id, workspaceId, message.ChannelId, message.ConversationId, id);

            if (draft.ParentMessageId is { } replyTo)
            {
                var parent = await db.Messages.SingleAsync(m => m.Id == replyTo, ct);
                parent.ReplyCount++;
                parent.LastReplyAt = now;
                parent.UpdatedSequence = sequence;
                requests.Add(Request(parent.AuthorUserId, ActivityType.Reply));
            }
            if (room.Type == RoomType.Conversation)
            {
                requests.AddRange((await db.ConversationMembers.Where(m => m.ConversationId == room.Id).Select(m => m.UserId).ToListAsync(ct))
                    .Select(member => Request(member, ActivityType.ChatMessage)));
                await db.ConversationMembers.Where(m => m.ConversationId == room.Id && m.IsHidden)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsHidden, false), ct);
            }

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
                return (await ExistingAsync(draft, deps, ct))!;
            }
        }

        var dto = await store.GetDtoAsync(id, ct);
        await notifier.MessageCreatedAsync(dto);
        await notifications.DeliverAsync(activity, ct);
        return TypedResults.Created($"/api/v1/messages/{id}", dto);
    }

    private static async Task<IResult> Edit(
        Guid messageId, EditMessageRequest body, DlpService dlp, [AsParameters] MessageDependencies deps, CancellationToken ct)
    {
        var (user, db, policy, store, mentions, notifications, notifier, clock) = deps;
        var permission = await policy.ForMessageAsync(user, messageId, ct);
        if (!permission.Has(Permission.Read)) return Problems.NotFound();
        if (!permission.Has(Permission.Edit)) return Problems.Forbidden("Only the author can edit, and not after deletion.");
        if (!MessageContent.TrySanitize(body.BodyMarkdown, out var content, out var error)) return Problems.Invalid("bodyMarkdown", error);
        if (await Problems.DlpGateAsync(dlp, content.Markdown, body.AcknowledgeDlpWarnings == true, ct) is { } dlpProblem) return dlpProblem;

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
            await db.PinnedMessages.Where(p => p.MessageId == messageId).ExecuteDeleteAsync(ct);

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        await notifier.MessageDeletedAsync(room, sequence, messageId, now);
        return TypedResults.NoContent();
    }

    /// <summary>Undo for the author's own delete: brings back the text kept in MessageEdits.</summary>
    private static async Task<IResult> Restore(Guid messageId, [AsParameters] MessageDependencies deps, CancellationToken ct)
    {
        var (user, db, policy, store, _, _, notifier, clock) = deps;
        var permission = await policy.ForMessageAsync(user, messageId, ct);
        if (!permission.Has(Permission.Read)) return Problems.NotFound();
        if (!permission.Has(Permission.Restore)) return Problems.Forbidden("Only the author can undo deleting their own message.");

        var room = RoomRef.Of(await db.Messages.AsNoTracking().SingleAsync(m => m.Id == messageId, ct));
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var sequence = await store.NextSequenceAsync(room, ct);
            var message = await db.Messages.SingleAsync(m => m.Id == messageId, ct);
            if (message.DeletedAt is null) return TypedResults.Ok(await store.GetDtoAsync(messageId, ct));

            var deletion = await db.MessageEdits
                .Where(e => e.MessageId == messageId && e.Kind == MessageChangeKind.Delete)
                .OrderByDescending(e => e.ChangedAt)
                .FirstAsync(ct);
            db.MessageEdits.Add(new MessageEdit
            {
                Id = Guid.NewGuid(), MessageId = messageId, Kind = MessageChangeKind.Restore,
                PreviousBodyMarkdown = "", ChangedByUserId = user.Id, ChangedAt = clock.GetUtcNow(),
            });
            message.BodyMarkdown = deletion.PreviousBodyMarkdown;
            message.DeletedAt = null;
            message.DeletedByUserId = null;
            message.UpdatedSequence = sequence;

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        var dto = await store.GetDtoAsync(messageId, ct);
        await notifier.MessageEditedAsync(dto);
        return TypedResults.Ok(dto);
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

    internal sealed record AttachmentTarget(Guid Id, int CurrentVersionNo);
}
