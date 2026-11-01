using Microsoft.EntityFrameworkCore;
using Mode.Api.Endpoints;
using Mode.Api.Messaging;
using Mode.Domain.Directory;
using Mode.Domain.Entities;
using Mode.Infrastructure.Data;

namespace Mode.Api.Files;

public sealed record FileVersionDto(
    int VersionNo, long SizeBytes, string ContentType, ScanStatus ScanStatus, string? ScanDetail, PreviewStatus PreviewStatus,
    bool HasThumbnail, bool HasPreview, UserDto UploadedBy, DateTimeOffset UploadedAt);

public sealed record FileDto(
    Guid Id, RoomRef Room, string Name, int CurrentVersionNo, int VersionCount, FileVersionDto Current,
    DateTimeOffset CreatedAt, DateTimeOffset? DeletedAt);

public sealed record AttachmentDto(
    Guid FileId, string Name, int VersionNo, long SizeBytes, string ContentType, ScanStatus ScanStatus, PreviewStatus PreviewStatus);

public sealed record StartUploadRequest(RoomRef? Room, string? Name, long? Size);

public sealed record StartUploadResponse(Guid UploadId, string UploadUrl, string Method, DateTimeOffset ExpiresAt, long MaxBytes);

public sealed record FileLinkResponse(string Url, DateTimeOffset ExpiresAt);

public sealed class FileQueries(ModeDbContext db, IUserDirectory directory)
{
    public static RoomRef RoomOf(FileAsset asset) => asset.ChannelId is { } channelId
        ? new RoomRef(RoomType.Channel, channelId)
        : new RoomRef(RoomType.Conversation, asset.ConversationId!.Value);

    public IQueryable<FileAsset> InRoom(RoomRef room) => room.Type == RoomType.Channel
        ? db.FileAssets.Where(f => f.ChannelId == room.Id)
        : db.FileAssets.Where(f => f.ConversationId == room.Id);

    public async Task<FileDto?> GetAsync(Guid fileId, CancellationToken ct) =>
        (await ToDtosAsync(await db.FileAssets.Include(f => f.Versions).AsNoTracking().Where(f => f.Id == fileId).ToListAsync(ct), ct))
        .SingleOrDefault();

    public async Task<IReadOnlyList<FileDto>> ToDtosAsync(IReadOnlyList<FileAsset> assets, CancellationToken ct)
    {
        var uploaders = await directory.GetManyAsync(assets.SelectMany(a => a.Versions.Select(v => v.UploadedByUserId)).Distinct(), ct);
        return assets.Select(a => new FileDto(
            a.Id, RoomOf(a), a.Name, a.CurrentVersionNo, a.Versions.Count,
            ToDto(a.Versions.Single(v => v.VersionNo == a.CurrentVersionNo), uploaders),
            a.CreatedAt, a.DeletedAt)).ToList();
    }

    public async Task<IReadOnlyList<FileVersionDto>> VersionsAsync(Guid fileId, CancellationToken ct)
    {
        var versions = await db.FileVersions.Where(v => v.FileAssetId == fileId).OrderByDescending(v => v.VersionNo).AsNoTracking().ToListAsync(ct);
        var uploaders = await directory.GetManyAsync(versions.Select(v => v.UploadedByUserId).Distinct(), ct);
        return versions.Select(v => ToDto(v, uploaders)).ToList();
    }

    /// <summary>Attachment details keyed by message id; files that were deleted are left out.</summary>
    public async Task<Dictionary<Guid, List<AttachmentDto>>> AttachmentsAsync(IReadOnlyCollection<Guid> messageIds, CancellationToken ct)
    {
        if (messageIds.Count == 0) return [];
        var rows = await (
                from a in db.MessageAttachments
                where messageIds.Contains(a.MessageId) && a.FileAsset!.DeletedAt == null
                join v in db.FileVersions on new { a.FileAssetId, a.VersionNo } equals new { v.FileAssetId, v.VersionNo }
                select new { a.MessageId, a.FileAssetId, a.FileAsset!.Name, v.VersionNo, v.SizeBytes, v.ContentType, v.ScanStatus, v.PreviewStatus })
            .AsNoTracking()
            .ToListAsync(ct);
        return rows
            .GroupBy(r => r.MessageId)
            .ToDictionary(g => g.Key, g => g
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r => new AttachmentDto(r.FileAssetId, r.Name, r.VersionNo, r.SizeBytes, r.ContentType, r.ScanStatus, r.PreviewStatus))
                .ToList());
    }

    private static FileVersionDto ToDto(FileVersion v, IReadOnlyDictionary<string, DirectoryUser> uploaders) => new(
        v.VersionNo, v.SizeBytes, v.ContentType, v.ScanStatus, v.ScanDetail, v.PreviewStatus,
        v.ThumbnailKey is not null, v.PreviewKey is not null,
        WorkspaceEndpoints.ToUserDto(uploaders, v.UploadedByUserId), v.UploadedAt);
}
