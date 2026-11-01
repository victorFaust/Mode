using System.Text;
using Microsoft.EntityFrameworkCore;
using Mode.Api.Auth;
using Mode.Api.Authorization;
using Mode.Api.Messaging;
using Mode.Domain.Directory;
using Mode.Domain.Entities;
using Mode.Infrastructure.Data;
using NpgsqlTypes;

namespace Mode.Api.Endpoints;

public enum SearchScope { All, Messages, Files, People }

public sealed record TextRange(int Start, int Length);

/// <summary>Plain text with the matched words marked by offsets, so clients never render search output as HTML.</summary>
public sealed record HighlightedText(string Text, IReadOnlyList<TextRange> Highlights);

public sealed record MessageSearchHit(
    Guid MessageId, RoomRef Room, string RoomName, Guid? ParentMessageId, long Sequence, UserDto Author, DateTimeOffset CreatedAt,
    HighlightedText Snippet);

public sealed record FileSearchHit(
    Guid FileId, RoomRef Room, string RoomName, string Name, long SizeBytes, string ContentType, UserDto UploadedBy,
    DateTimeOffset UploadedAt, HighlightedText? Snippet);

public sealed record SearchResponse(IReadOnlyList<MessageSearchHit> Messages, IReadOnlyList<FileSearchHit> Files, IReadOnlyList<UserDto> People);

/// <summary>
/// Postgres full-text search (english stemming, web-style syntax: quotes, OR, -word). Every query is restricted to the
/// caller's readable rooms before ranking, so nothing the caller cannot open is counted, ranked or returned.
/// </summary>
public static class SearchEndpoints
{
    public const int MaxQueryLength = 200;
    public const int MaxResults = 50;
    private const string Vector = "SearchVector";
    private const string StartMark = "[[[";
    private const string StopMark = "]]]";
    private const string HeadlineOptions = "StartSel=[[[, StopSel=]]], MaxWords=30, MinWords=12, MaxFragments=2, FragmentDelimiter=\" … \"";

    public static RouteGroupBuilder MapSearchEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/search", Search).WithTags("Search").Produces<SearchResponse>();
        return api;
    }

    private static async Task<IResult> Search(
        string? q, SearchScope? scope, Guid? channelId, Guid? conversationId, string? authorId, DateTimeOffset? from, DateTimeOffset? to,
        int? limit, CurrentUser user, ModeDbContext db, AccessPolicy policy, IUserDirectory directory, CancellationToken ct)
    {
        var query = q?.Trim() ?? "";
        if (query.Length < 2) return Problems.Invalid("q", "Enter at least 2 characters.");
        if (query.Length > MaxQueryLength) return Problems.Invalid("q", $"At most {MaxQueryLength} characters.");
        if (channelId is not null && conversationId is not null) return Problems.Invalid("channelId", "Filter by a channel or a chat, not both.");
        string? author = null;
        if (authorId is not null && !directory.TryNormalizeId(authorId, out author)) return Problems.Invalid("authorId", "Unknown user id.");

        var take = Math.Clamp(limit ?? 20, 1, MaxResults);
        var what = scope ?? SearchScope.All;
        var rooms = await policy.ReadableRoomsAsync(user, ct);

        // A room filter the caller can't read simply narrows to nothing; it must not reveal whether the room exists.
        var channelIds = channelId is { } c ? rooms.ChannelIds.Where(id => id == c).ToList() : conversationId is null ? rooms.ChannelIds.ToList() : [];
        var conversationIds = conversationId is { } v ? rooms.ConversationIds.Where(id => id == v).ToList() : channelId is null ? rooms.ConversationIds.ToList() : [];

        var messages = what is SearchScope.All or SearchScope.Messages
            ? await SearchMessagesAsync(db, query, channelIds, conversationIds, author, from, to, take, ct)
            : [];
        var files = what is SearchScope.All or SearchScope.Files
            ? await SearchFilesAsync(db, query, channelIds, conversationIds, author, from, to, take, ct)
            : [];
        var people = what is SearchScope.All or SearchScope.People && channelId is null && conversationId is null && author is null
            && await policy.CanUseDirectoryAsync(user, ct)
            ? (await directory.SearchAsync(query, Math.Min(take, Limits.SearchResults), ct)).Select(UserDto.From).ToList()
            : [];

        var names = await RoomNamesAsync(db, directory, user,
            messages.Select(m => m.Room).Concat(files.Select(f => f.Room)).Distinct().ToList(), ct);
        var userIds = messages.Select(m => m.AuthorId).Concat(files.Select(f => f.UploadedBy)).Distinct().ToList();
        var users = await directory.GetManyAsync(userIds, ct);

        return TypedResults.Ok(new SearchResponse(
            messages.Select(m => new MessageSearchHit(m.Id, m.Room, names[m.Room], m.ParentMessageId, m.Sequence,
                WorkspaceEndpoints.ToUserDto(users, m.AuthorId), m.CreatedAt, ParseHeadline(m.Headline))).ToList(),
            files.Select(f => new FileSearchHit(f.Id, f.Room, names[f.Room], f.Name, f.SizeBytes, f.ContentType,
                WorkspaceEndpoints.ToUserDto(users, f.UploadedBy), f.UploadedAt, f.Headline is null ? null : ParseHeadline(f.Headline))).ToList(),
            people));
    }

    private sealed record MessageRow(
        Guid Id, RoomRef Room, Guid? ParentMessageId, long Sequence, string AuthorId, DateTimeOffset CreatedAt, string Headline);

    private sealed record FileRow(
        Guid Id, RoomRef Room, string Name, long SizeBytes, string ContentType, string UploadedBy, DateTimeOffset UploadedAt, string? Headline);

    private static async Task<List<MessageRow>> SearchMessagesAsync(
        ModeDbContext db, string q, List<Guid> channelIds, List<Guid> conversationIds, string? author,
        DateTimeOffset? from, DateTimeOffset? to, int take, CancellationToken ct)
    {
        var messages = db.Messages.Where(m => m.DeletedAt == null &&
            ((m.ChannelId != null && channelIds.Contains(m.ChannelId.Value)) ||
             (m.ConversationId != null && conversationIds.Contains(m.ConversationId.Value))));
        if (author is not null) messages = messages.Where(m => m.AuthorUserId == author);
        if (from is { } start) messages = messages.Where(m => m.CreatedAt >= start);
        if (to is { } end) messages = messages.Where(m => m.CreatedAt < end);

        // EF.Functions calls must stay inside each lambda so they are translated to SQL rather than run here.
        var rows = await messages
            .Where(m => EF.Property<NpgsqlTsVector>(m, Vector).Matches(EF.Functions.WebSearchToTsQuery("english", q)))
            .OrderByDescending(m => EF.Property<NpgsqlTsVector>(m, Vector).Rank(EF.Functions.WebSearchToTsQuery("english", q)))
            .ThenByDescending(m => m.CreatedAt)
            .Take(take)
            .Select(m => new
            {
                m.Id, m.ChannelId, m.ConversationId, m.ParentMessageId, m.Sequence, m.AuthorUserId, m.CreatedAt,
                Headline = EF.Functions.WebSearchToTsQuery("english", q).GetResultHeadline(m.BodyMarkdown, HeadlineOptions),
            })
            .ToListAsync(ct);
        return rows.Select(r => new MessageRow(r.Id, Room(r.ChannelId, r.ConversationId), r.ParentMessageId, r.Sequence, r.AuthorUserId,
            r.CreatedAt, r.Headline)).ToList();
    }

    /// <summary>Current versions that passed scanning only; matches extracted text or the file name.</summary>
    private static async Task<List<FileRow>> SearchFilesAsync(
        ModeDbContext db, string q, List<Guid> channelIds, List<Guid> conversationIds, string? author,
        DateTimeOffset? from, DateTimeOffset? to, int take, CancellationToken ct)
    {
        var namePattern = "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var files =
            from f in db.FileAssets
            join version in db.FileVersions on new { Id = f.Id, No = f.CurrentVersionNo } equals new { Id = version.FileAssetId, No = version.VersionNo }
            where f.DeletedAt == null && version.ScanStatus == ScanStatus.Clean &&
                  ((f.ChannelId != null && channelIds.Contains(f.ChannelId.Value)) ||
                   (f.ConversationId != null && conversationIds.Contains(f.ConversationId.Value)))
            select new { f, version };
        if (author is not null) files = files.Where(x => x.version.UploadedByUserId == author);
        if (from is { } start) files = files.Where(x => x.version.UploadedAt >= start);
        if (to is { } end) files = files.Where(x => x.version.UploadedAt < end);

        var rows = await files
            .Where(x => EF.Property<NpgsqlTsVector>(x.version, Vector).Matches(EF.Functions.WebSearchToTsQuery("english", q)) ||
                        EF.Functions.ILike(x.f.Name, namePattern, "\\"))
            .OrderByDescending(x => EF.Property<NpgsqlTsVector>(x.version, Vector).Rank(EF.Functions.WebSearchToTsQuery("english", q)))
            .ThenByDescending(x => x.version.UploadedAt)
            .Take(take)
            .Select(x => new
            {
                x.f.Id, x.f.ChannelId, x.f.ConversationId, x.f.Name, x.version.SizeBytes, x.version.ContentType, x.version.UploadedByUserId,
                x.version.UploadedAt,
                Headline = x.version.ExtractedText == null || !EF.Property<NpgsqlTsVector>(x.version, Vector).Matches(EF.Functions.WebSearchToTsQuery("english", q))
                    ? null
                    : EF.Functions.WebSearchToTsQuery("english", q).GetResultHeadline(x.version.ExtractedText, HeadlineOptions),
            })
            .ToListAsync(ct);
        return rows.Select(r => new FileRow(r.Id, Room(r.ChannelId, r.ConversationId), r.Name, r.SizeBytes, r.ContentType,
            r.UploadedByUserId, r.UploadedAt, r.Headline)).ToList();
    }

    private static RoomRef Room(Guid? channelId, Guid? conversationId) => channelId is { } id
        ? new RoomRef(RoomType.Channel, id)
        : new RoomRef(RoomType.Conversation, conversationId!.Value);

    /// <summary>Channel name, group chat name, or the other people's names for an unnamed chat.</summary>
    private static async Task<Dictionary<RoomRef, string>> RoomNamesAsync(
        ModeDbContext db, IUserDirectory directory, CurrentUser user, List<RoomRef> rooms, CancellationToken ct)
    {
        var channelIds = rooms.Where(r => r.Type == RoomType.Channel).Select(r => r.Id).ToList();
        var conversationIds = rooms.Where(r => r.Type == RoomType.Conversation).Select(r => r.Id).ToList();
        var channels = await db.Channels.Where(c => channelIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var conversations = await db.Conversations.Where(c => conversationIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, Members = c.Members.Select(m => m.UserId).ToList() })
            .ToListAsync(ct);
        var people = await directory.GetManyAsync(conversations.SelectMany(c => c.Members).Distinct(), ct);

        var names = channels.ToDictionary(c => new RoomRef(RoomType.Channel, c.Key), c => c.Value);
        foreach (var conversation in conversations)
            names[new RoomRef(RoomType.Conversation, conversation.Id)] = conversation.Name ?? string.Join(", ",
                conversation.Members.Where(id => id != user.Id).Select(id => WorkspaceEndpoints.ToUserDto(people, id).DisplayName).Order());
        return names;
    }

    /// <summary>Turns ts_headline output with [[[ ]]] marks into plain text plus highlight offsets.</summary>
    internal static HighlightedText ParseHeadline(string headline)
    {
        var text = new StringBuilder(headline.Length);
        var highlights = new List<TextRange>();
        var position = 0;
        while (position < headline.Length)
        {
            var start = headline.IndexOf(StartMark, position, StringComparison.Ordinal);
            if (start < 0) break;
            var stop = headline.IndexOf(StopMark, start + StartMark.Length, StringComparison.Ordinal);
            if (stop < 0) break;

            text.Append(headline, position, start - position);
            var word = headline.Substring(start + StartMark.Length, stop - start - StartMark.Length);
            highlights.Add(new TextRange(text.Length, word.Length));
            text.Append(word);
            position = stop + StopMark.Length;
        }
        text.Append(headline, position, headline.Length - position);
        return new HighlightedText(text.ToString(), highlights);
    }
}
