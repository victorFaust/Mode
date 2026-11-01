using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mode.Api.Auth;
using Mode.Api.Authorization;
using Mode.Api.Endpoints;
using Mode.Api.Jobs;
using Mode.Api.Messaging;
using Mode.Api.Realtime;
using Mode.Domain.Entities;
using Mode.Domain.Files;
using Mode.Infrastructure.Data;

namespace Mode.Api.Files;

public static class FileEndpoints
{
    private const long MaxDerivedImageBytes = 20 * 1024 * 1024;

    /// <summary>Services shared by the file handlers.</summary>
    public sealed record FileDependencies(
        CurrentUser User, ModeDbContext Db, AccessPolicy Policy, FileQueries Queries, IFileStore Store,
        IOptions<FilesOptions> Options, RealtimeNotifier Notifier, TimeProvider Clock);

    public static RouteGroupBuilder MapFileEndpoints(this RouteGroupBuilder api)
    {
        var files = api.MapGroup("/files").WithTags("Files");
        files.MapPost("/uploads", StartUpload).Produces<StartUploadResponse>(StatusCodes.Status201Created);
        files.MapPut("/uploads/{uploadId:guid}/content", UploadContent).Produces(StatusCodes.Status204NoContent);
        files.MapPost("/uploads/{uploadId:guid}/complete", CompleteUpload).Produces<FileDto>(StatusCodes.Status201Created);
        files.MapGet("/{fileId:guid}", Get).Produces<FileDto>();
        files.MapGet("/{fileId:guid}/versions", Versions).Produces<FileVersionDto[]>();
        files.MapGet("/{fileId:guid}/link", Link).Produces<FileLinkResponse>();
        files.MapDelete("/{fileId:guid}", Delete).Produces(StatusCodes.Status204NoContent);
        files.MapPost("/{fileId:guid}/restore", Restore).Produces<FileDto>();
        files.MapGet("/content/{token}", Content).AllowAnonymous().Produces(StatusCodes.Status200OK, contentType: "application/octet-stream");

        api.MapGet("/channels/{channelId:guid}/files",
            (Guid channelId, bool? deleted, [AsParameters] FileDependencies deps, CancellationToken ct) =>
                List(new RoomRef(RoomType.Channel, channelId), deleted, deps, ct)).WithTags("Files").Produces<FileDto[]>();
        api.MapGet("/conversations/{conversationId:guid}/files",
            (Guid conversationId, bool? deleted, [AsParameters] FileDependencies deps, CancellationToken ct) =>
                List(new RoomRef(RoomType.Conversation, conversationId), deleted, deps, ct)).WithTags("Files").Produces<FileDto[]>();
        return api;
    }

    /// <summary>Used only by the file processor; each request carries a signed, short-lived token.</summary>
    public static IEndpointRouteBuilder MapInternalFileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal/files").AllowAnonymous().ExcludeFromDescription();
        group.MapGet("/{token}", ProcessorRead);
        group.MapPut("/{token}", ProcessorWrite);
        return app;
    }

    private static async Task<IResult> StartUpload(StartUploadRequest body, [AsParameters] FileDependencies deps, CancellationToken ct)
    {
        var (user, db, policy, _, _, options, _, clock) = deps;
        var settings = options.Value;
        if (body.Room is not { } room) return Problems.Invalid("room", "Required.");

        var permission = await policy.ForRoomAsync(user, room, ct);
        if (!permission.Has(Permission.Read)) return Problems.NotFound();
        if (!permission.Has(Permission.Post)) return Problems.Forbidden("This channel is archived.");
        if (!FileNames.TrySanitize(body.Name, settings.Extensions, out var name, out var extension, out var error))
            return Problems.Invalid("name", error);
        if (body.Size is not > 0) return Problems.Invalid("size", "Required.");
        if (body.Size > settings.MaxSizeBytes) return Problems.Invalid("size", $"Files can be at most {settings.MaxSizeMb} MB.");

        var now = clock.GetUtcNow();
        var session = new UploadSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ChannelId = room.Type == RoomType.Channel ? room.Id : null,
            ConversationId = room.Type == RoomType.Conversation ? room.Id : null,
            FileName = name,
            NormalizedName = FileNames.Normalize(name),
            DeclaredSize = body.Size.Value,
            ContentType = FileNames.ContentTypeFor(extension),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(settings.UploadSessionMinutes),
            StorageKey = "",
        };
        session.StorageKey = $"files/{now.ToString("yyyy/MM", CultureInfo.InvariantCulture)}/{session.Id:N}";
        db.UploadSessions.Add(session);
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/api/v1/files/uploads/{session.Id}",
            new StartUploadResponse(session.Id, $"/api/v1/files/uploads/{session.Id}/content", "PUT", session.ExpiresAt, session.DeclaredSize));
    }

    private static async Task<IResult> UploadContent(Guid uploadId, HttpContext http, [AsParameters] FileDependencies deps, CancellationToken ct)
    {
        var (user, db, policy, _, store, _, _, clock) = deps;
        var session = await db.UploadSessions.SingleOrDefaultAsync(u => u.Id == uploadId && u.UserId == user.Id, ct);
        if (session is null) return Problems.NotFound();
        if (session.CompletedAt is not null) return Problems.Conflict("This upload is already complete.");
        if (session.ExpiresAt <= clock.GetUtcNow()) return Expired();
        if (await CheckPostAsync(policy, user, RoomOf(session), ct) is { } denied) return denied;
        if (http.Request.ContentLength is { } length && length != session.DeclaredSize)
            return Problems.Invalid("content", $"Expected {session.DeclaredSize} bytes, but the request declares {length}.");

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = session.DeclaredSize;

        try
        {
            await store.WriteAsync(session.StorageKey, new LimitedReadStream(http.Request.Body, session.DeclaredSize), ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        {
            await store.DeleteAsync(session.StorageKey, ct);
            return Problems.Invalid("content", "The file is larger than declared.");
        }

        var size = await store.GetSizeAsync(session.StorageKey, ct);
        if (size != session.DeclaredSize)
        {
            await store.DeleteAsync(session.StorageKey, ct);
            return Problems.Invalid("content", $"Expected {session.DeclaredSize} bytes but received {size ?? 0}.");
        }

        session.ReceivedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> CompleteUpload(
        Guid uploadId, JobQueue queue, [AsParameters] FileDependencies deps, CancellationToken ct)
    {
        var (user, db, policy, queries, store, _, notifier, clock) = deps;
        var session = await db.UploadSessions.SingleOrDefaultAsync(u => u.Id == uploadId && u.UserId == user.Id, ct);
        if (session is null) return Problems.NotFound();
        if (session.CompletedAt is not null) return Problems.Conflict("This upload is already complete.");
        if (session.ExpiresAt <= clock.GetUtcNow()) return Expired();
        if (session.ReceivedAt is null) return Problems.Invalid("upload", "Upload the file content first.");

        var room = RoomOf(session);
        if (await CheckPostAsync(policy, user, room, ct) is { } denied) return denied;
        if (await store.GetSizeAsync(session.StorageKey, ct) != session.DeclaredSize)
            return Problems.Invalid("upload", "The uploaded content is missing or incomplete.");

        Guid assetId;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            // Serializes uploads per room so two same-name uploads cannot claim the same version number.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({room.Id.ToString()}, 4))", ct);

            var now = clock.GetUtcNow();
            var asset = await queries.InRoom(room).SingleOrDefaultAsync(f => f.DeletedAt == null && f.NormalizedName == session.NormalizedName, ct);
            if (asset is null)
            {
                asset = new FileAsset
                {
                    Id = Guid.NewGuid(),
                    ChannelId = session.ChannelId,
                    ConversationId = session.ConversationId,
                    Name = session.FileName,
                    NormalizedName = session.NormalizedName,
                    CreatedByUserId = user.Id,
                    CreatedAt = now,
                };
                db.FileAssets.Add(asset);
            }

            var version = new FileVersion
            {
                Id = Guid.NewGuid(),
                FileAssetId = asset.Id,
                VersionNo = asset.CurrentVersionNo + 1,
                ObjectKey = session.StorageKey,
                SizeBytes = session.DeclaredSize,
                ContentType = session.ContentType,
                ScanStatus = ScanStatus.Pending,
                PreviewStatus = PreviewStatus.Pending,
                UploadedByUserId = user.Id,
                UploadedAt = now,
            };
            db.FileVersions.Add(version);
            asset.CurrentVersionNo = version.VersionNo;
            session.CompletedAt = now;
            queue.Enqueue(JobTypes.FileScan, new FileVersionJobPayload(version.Id));

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            assetId = asset.Id;
        }

        var dto = (await queries.GetAsync(assetId, ct))!;
        await notifier.FileUpdatedAsync(dto);
        return TypedResults.Created($"/api/v1/files/{assetId}", dto);
    }

    private static async Task<IResult> Get(Guid fileId, [AsParameters] FileDependencies deps, CancellationToken ct)
    {
        if (!(await deps.Policy.ForFileAsync(deps.User, fileId, ct)).Has(Permission.Read)) return Problems.NotFound();
        return TypedResults.Ok(await deps.Queries.GetAsync(fileId, ct));
    }

    private static async Task<IResult> Versions(Guid fileId, [AsParameters] FileDependencies deps, CancellationToken ct)
    {
        if (!(await deps.Policy.ForFileAsync(deps.User, fileId, ct)).Has(Permission.Read)) return Problems.NotFound();
        return TypedResults.Ok(await deps.Queries.VersionsAsync(fileId, ct));
    }

    private static async Task<IResult> Link(
        Guid fileId, string? variant, int? version, FileLinkSigner signer, [AsParameters] FileDependencies deps, CancellationToken ct)
    {
        var (user, db, policy, _, _, options, _, clock) = deps;
        if (!(await policy.ForFileAsync(user, fileId, ct)).Has(Permission.Read)) return Problems.NotFound();

        var kind = FileVariant.Original;
        if (variant is not null && (!Enum.TryParse(variant, ignoreCase: true, out kind) || !Enum.IsDefined(kind)))
            return Problems.Invalid("variant", "Use original, thumbnail or preview.");

        var versionNo = version ?? await db.FileAssets.Where(f => f.Id == fileId).Select(f => f.CurrentVersionNo).SingleAsync(ct);
        var target = await db.FileVersions.AsNoTracking().SingleOrDefaultAsync(v => v.FileAssetId == fileId && v.VersionNo == versionNo, ct);
        if (target is null) return Problems.NotFound();
        if (target.ScanStatus == ScanStatus.Pending) return Problems.Conflict("The file is still being scanned.");
        if (target.ScanStatus != ScanStatus.Clean) return Problems.Forbidden($"This file is blocked: {target.ScanDetail ?? target.ScanStatus.ToString()}");
        if ((kind == FileVariant.Thumbnail && target.ThumbnailKey is null) || (kind == FileVariant.Preview && target.PreviewKey is null))
            return Problems.NotFound();

        var expires = clock.GetUtcNow().AddMinutes(options.Value.DownloadLinkMinutes);
        var token = signer.Create(new FileLink(LinkPurpose.Download, target.Id, kind, expires));
        return TypedResults.Ok(new FileLinkResponse($"/api/v1/files/content/{token}", expires));
    }

    private static async Task<IResult> Content(string token, HttpContext http, FileLinkSigner signer, ModeDbContext db, IFileStore store, CancellationToken ct)
    {
        if (signer.Verify(token, LinkPurpose.Download) is not { } link)
            return Problems.Forbidden("This link is invalid or has expired.");

        var version = await db.FileVersions.Include(v => v.FileAsset).AsNoTracking().SingleOrDefaultAsync(v => v.Id == link.VersionId, ct);
        if (version is not { ScanStatus: ScanStatus.Clean } || version.FileAsset!.DeletedAt is not null) return Problems.NotFound();

        var key = link.Variant switch
        {
            FileVariant.Thumbnail => version.ThumbnailKey,
            FileVariant.Preview => version.PreviewKey,
            _ => version.ObjectKey,
        };
        var stream = key is null ? null : await store.OpenReadAsync(key, ct);
        if (stream is null) return Problems.NotFound();

        // User content never runs as a page: no sniffing, no scripts, and originals always download.
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        http.Response.Headers.CacheControl = "private, max-age=300";
        return link.Variant == FileVariant.Original
            ? TypedResults.File(stream, version.ContentType, version.FileAsset.Name, enableRangeProcessing: true)
            : TypedResults.File(stream, "image/png");
    }

    private static async Task<IResult> Delete(Guid fileId, [AsParameters] FileDependencies deps, CancellationToken ct)
    {
        var (user, db, policy, queries, _, _, notifier, clock) = deps;
        var permission = await policy.ForFileAsync(user, fileId, ct);
        if (!permission.Has(Permission.Read)) return Problems.NotFound();
        if (!permission.Has(Permission.Delete)) return Problems.Forbidden("Only the uploader or a channel manager can delete this file.");

        var asset = await db.FileAssets.SingleAsync(f => f.Id == fileId, ct);
        asset.DeletedAt = clock.GetUtcNow();
        asset.DeletedByUserId = user.Id;
        await db.SaveChangesAsync(ct);

        await notifier.FileUpdatedAsync((await queries.GetAsync(fileId, ct))!);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> Restore(Guid fileId, [AsParameters] FileDependencies deps, CancellationToken ct)
    {
        var (user, db, policy, queries, _, options, notifier, clock) = deps;
        var permission = await policy.ForFileAsync(user, fileId, ct);
        if (permission.Has(Permission.Read)) return Problems.Conflict("The file is not deleted.");
        if (!permission.Has(Permission.Restore)) return Problems.NotFound();

        var asset = await db.FileAssets.SingleAsync(f => f.Id == fileId, ct);
        if (asset.DeletedAt < clock.GetUtcNow().AddDays(-options.Value.RecoveryDays)) return Problems.NotFound();
        if (await queries.InRoom(FileQueries.RoomOf(asset)).AnyAsync(f => f.DeletedAt == null && f.NormalizedName == asset.NormalizedName, ct))
            return Problems.Conflict("Another file with this name now exists here. Rename or delete it first.");

        asset.DeletedAt = null;
        asset.DeletedByUserId = null;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (Problems.IsUniqueViolation(ex))
        {
            return Problems.Conflict("Another file with this name now exists here. Rename or delete it first.");
        }

        var dto = (await queries.GetAsync(fileId, ct))!;
        await notifier.FileUpdatedAsync(dto);
        return TypedResults.Ok(dto);
    }

    private static async Task<IResult> List(RoomRef room, bool? deleted, FileDependencies deps, CancellationToken ct)
    {
        var (user, db, policy, queries, _, options, _, clock) = deps;
        var roomPermission = await policy.ForRoomAsync(user, room, ct);
        if (!roomPermission.Has(Permission.Read)) return Problems.NotFound();

        if (deleted != true)
        {
            var live = await queries.InRoom(room).Where(f => f.DeletedAt == null)
                .OrderByDescending(f => f.CreatedAt).Take(500).Include(f => f.Versions).AsNoTracking().ToListAsync(ct);
            return TypedResults.Ok(await queries.ToDtosAsync(live, ct));
        }

        // Recycle bin: deleted files still in the recovery window that this user may restore.
        var since = clock.GetUtcNow().AddDays(-options.Value.RecoveryDays);
        var recoverable = await queries.InRoom(room).Where(f => f.DeletedAt != null && f.DeletedAt >= since)
            .OrderByDescending(f => f.DeletedAt).Take(500).Include(f => f.Versions).AsNoTracking().ToListAsync(ct);
        var restorable = recoverable
            .Where(f => AccessPolicy.FilePermissions(roomPermission, room.Type == RoomType.Channel, f.CreatedByUserId == user.Id, deleted: true)
                .Has(Permission.Restore))
            .ToList();
        return TypedResults.Ok(await queries.ToDtosAsync(restorable, ct));
    }

    private static async Task<IResult> ProcessorRead(string token, FileLinkSigner signer, ModeDbContext db, IFileStore store, CancellationToken ct)
    {
        if (signer.Verify(token, LinkPurpose.ProcessorRead) is not { Variant: FileVariant.Original } link) return Problems.Forbidden();
        var version = await db.FileVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == link.VersionId, ct);
        if (version is not { ScanStatus: ScanStatus.Clean }) return Problems.NotFound();

        var stream = await store.OpenReadAsync(version.ObjectKey, ct);
        return stream is null ? Problems.NotFound() : TypedResults.Stream(stream, "application/octet-stream");
    }

    private static async Task<IResult> ProcessorWrite(
        string token, HttpContext http, FileLinkSigner signer, ModeDbContext db, IFileStore store, CancellationToken ct)
    {
        if (signer.Verify(token, LinkPurpose.ProcessorWrite) is not { Variant: FileVariant.Thumbnail or FileVariant.Preview } link)
            return Problems.Forbidden();
        var version = await db.FileVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == link.VersionId, ct);
        if (version is not { ScanStatus: ScanStatus.Clean, PreviewStatus: PreviewStatus.Pending }) return Problems.NotFound();

        var buffer = new MemoryStream();
        try
        {
            await new LimitedReadStream(http.Request.Body, MaxDerivedImageBytes).CopyToAsync(buffer, ct);
        }
        catch (InvalidDataException)
        {
            return Problems.Invalid("content", "Preview images can be at most 20 MB.");
        }
        if (!FileTypeSniffer.Matches("png", buffer.GetBuffer().AsSpan(0, (int)buffer.Length)))
            return Problems.Invalid("content", "Previews must be PNG images.");

        buffer.Position = 0;
        await store.WriteAsync(FilePreviewJob.DerivedKey(version.Id, link.Variant), buffer, ct);
        return TypedResults.NoContent();
    }

    private static RoomRef RoomOf(UploadSession session) => session.ChannelId is { } channelId
        ? new RoomRef(RoomType.Channel, channelId)
        : new RoomRef(RoomType.Conversation, session.ConversationId!.Value);

    private static async Task<IResult?> CheckPostAsync(AccessPolicy policy, CurrentUser user, RoomRef room, CancellationToken ct)
    {
        var permission = await policy.ForRoomAsync(user, room, ct);
        if (!permission.Has(Permission.Read)) return Problems.NotFound();
        return permission.Has(Permission.Post) ? null : Problems.Forbidden("This channel is archived.");
    }

    private static IResult Expired() =>
        TypedResults.Problem(statusCode: StatusCodes.Status410Gone, title: "Upload expired", detail: "Start the upload again.");
}
