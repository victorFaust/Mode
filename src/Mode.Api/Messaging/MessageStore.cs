using Microsoft.EntityFrameworkCore;
using Mode.Api.Endpoints;
using Mode.Api.Files;
using Mode.Domain.Directory;
using Mode.Domain.Entities;
using Mode.Infrastructure.Data;

namespace Mode.Api.Messaging;

/// <summary>Reads messages and builds DTOs; also hands out room sequence numbers.</summary>
public sealed class MessageStore(ModeDbContext db, IUserDirectory directory, FileQueries files)
{
    public const int MaxPageSize = 100;

    /// <summary>
    /// Takes the next room sequence. The row lock it acquires is held until the caller's transaction ends,
    /// which serializes writers per room and keeps sequences gap-free.
    /// </summary>
    public async Task<long> NextSequenceAsync(RoomRef room, CancellationToken ct)
    {
        var query = room.Type == RoomType.Channel
            ? db.Database.SqlQuery<long>($"UPDATE \"Channels\" SET \"LastSequence\" = \"LastSequence\" + 1 WHERE \"Id\" = {room.Id} RETURNING \"LastSequence\" AS \"Value\"")
            : db.Database.SqlQuery<long>($"UPDATE \"Conversations\" SET \"LastSequence\" = \"LastSequence\" + 1 WHERE \"Id\" = {room.Id} RETURNING \"LastSequence\" AS \"Value\"");
        return (await query.ToListAsync(ct)).Single();
    }

    public Task<long> CurrentSequenceAsync(RoomRef room, CancellationToken ct) => room.Type == RoomType.Channel
        ? db.Channels.Where(c => c.Id == room.Id).Select(c => c.LastSequence).SingleAsync(ct)
        : db.Conversations.Where(c => c.Id == room.Id).Select(c => c.LastSequence).SingleAsync(ct);

    public IQueryable<Message> InRoom(RoomRef room) => room.Type == RoomType.Channel
        ? db.Messages.Where(m => m.ChannelId == room.Id)
        : db.Messages.Where(m => m.ConversationId == room.Id);

    public async Task<MessagePage> TopLevelPageAsync(RoomRef room, long? before, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, MaxPageSize);
        var query = InRoom(room).Where(m => m.ParentMessageId == null);
        if (before is not null) query = query.Where(m => m.Sequence < before);
        var messages = await WithDetails(query).OrderByDescending(m => m.Sequence).Take(limit + 1).ToListAsync(ct);

        var hasMore = messages.Count > limit;
        var page = messages.Take(limit).ToList();
        return new MessagePage(await ToDtosAsync(page, ct), hasMore ? page[^1].Sequence : null, null);
    }

    /// <summary>Oldest first from just after <paramref name="after"/>; used to open a room at the first unread message.</summary>
    public async Task<MessagePage> TopLevelPageAfterAsync(RoomRef room, long after, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, MaxPageSize);
        var query = InRoom(room).Where(m => m.ParentMessageId == null && m.Sequence > after);
        var messages = await WithDetails(query).OrderBy(m => m.Sequence).Take(limit + 1).ToListAsync(ct);

        var hasMore = messages.Count > limit;
        var page = messages.Take(limit).ToList();
        return new MessagePage(await ToDtosAsync(page, ct), page.Count > 0 ? page[0].Sequence : null, hasMore ? page[^1].Sequence : null);
    }

    public async Task<IReadOnlyList<MessageDto>> RepliesAsync(Guid parentId, long? after, int limit, CancellationToken ct)
    {
        var query = db.Messages.Where(m => m.ParentMessageId == parentId);
        if (after is not null) query = query.Where(m => m.Sequence > after);
        var replies = await WithDetails(query).OrderBy(m => m.Sequence).Take(Math.Clamp(limit, 1, MaxPageSize)).ToListAsync(ct);
        return await ToDtosAsync(replies, ct);
    }

    /// <summary>Messages created or changed after <paramref name="afterSequence"/>, oldest change first.</summary>
    public async Task<IReadOnlyList<MessageDto>> ChangesSinceAsync(RoomRef room, long afterSequence, int limit, CancellationToken ct)
    {
        var changed = await WithDetails(InRoom(room).Where(m => m.UpdatedSequence > afterSequence))
            .OrderBy(m => m.UpdatedSequence)
            .Take(limit)
            .ToListAsync(ct);
        return await ToDtosAsync(changed, ct);
    }

    public async Task<MessageDto> GetDtoAsync(Guid messageId, CancellationToken ct) =>
        (await ToDtosAsync([await WithDetails(db.Messages).SingleAsync(m => m.Id == messageId, ct)], ct))[0];

    public async Task<IReadOnlyList<ReactionSummaryDto>> ReactionSummaryAsync(Guid messageId, CancellationToken ct) =>
        Summarize(await db.Reactions.Where(r => r.MessageId == messageId).ToListAsync(ct));

    private static IQueryable<Message> WithDetails(IQueryable<Message> query) =>
        query.Include(m => m.Reactions).Include(m => m.Mentions).Include(m => m.Pin).AsSplitQuery().AsNoTracking();

    public async Task<IReadOnlyList<PinDto>> PinsAsync(RoomRef room, CancellationToken ct)
    {
        var messages = await WithDetails(InRoom(room).Where(m => m.Pin != null)).ToListAsync(ct);
        var dtos = await ToDtosAsync(messages, ct);
        var pinners = await directory.GetManyAsync(messages.Select(m => m.Pin!.PinnedByUserId).Distinct(), ct);
        return messages.Zip(dtos)
            .Select(x => new PinDto(x.Second, WorkspaceEndpoints.ToUserDto(pinners, x.First.Pin!.PinnedByUserId), x.First.Pin!.PinnedAt))
            .OrderByDescending(p => p.PinnedAt)
            .ToList();
    }

    private async Task<IReadOnlyList<MessageDto>> ToDtosAsync(IReadOnlyList<Message> messages, CancellationToken ct)
    {
        var people = messages.Select(m => m.AuthorUserId)
            .Concat(messages.Where(m => m.ForwardedFromUserId is not null).Select(m => m.ForwardedFromUserId!));
        var authors = await directory.GetManyAsync(people.Distinct(), ct);
        var attachments = await files.AttachmentsAsync(messages.Where(m => m.DeletedAt is null).Select(m => m.Id).ToList(), ct);
        return messages.Select(m =>
        {
            var deleted = m.DeletedAt is not null;
            return new MessageDto(
                m.Id, RoomRef.Of(m), m.Sequence, m.UpdatedSequence, m.ParentMessageId,
                WorkspaceEndpoints.ToUserDto(authors, m.AuthorUserId),
                deleted ? "" : m.BodyMarkdown,
                m.CreatedAt, m.EditedAt, deleted, deleted && m.DeletedByUserId == m.AuthorUserId, m.ReplyCount, m.LastReplyAt,
                deleted ? [] : Summarize(m.Reactions),
                deleted ? [] : m.Mentions.Select(x => new MentionDto(x.Kind, x.TargetUserId)).ToList(),
                attachments.GetValueOrDefault(m.Id) ?? [],
                m.Pin is not null,
                deleted || m.ForwardedBodyMarkdown is null ? null : new ForwardDto(
                    m.ForwardedFromMessageId, WorkspaceEndpoints.ToUserDto(authors, m.ForwardedFromUserId!),
                    m.ForwardedFromCreatedAt!.Value, m.ForwardedBodyMarkdown));
        }).ToList();
    }

    private static IReadOnlyList<ReactionSummaryDto> Summarize(IEnumerable<Reaction> reactions) => reactions
        .GroupBy(r => r.Emoji)
        .OrderBy(g => g.Min(r => r.CreatedAt))
        .Select(g => new ReactionSummaryDto(g.Key, g.Count(), g.OrderBy(r => r.CreatedAt).Select(r => r.UserId).ToList()))
        .ToList();
}
