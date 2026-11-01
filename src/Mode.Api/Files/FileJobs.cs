using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mode.Api.Files.Processing;
using Mode.Api.Files.Scanning;
using Mode.Api.Jobs;
using Mode.Api.Realtime;
using Mode.Domain.Entities;
using Mode.Domain.Files;
using Mode.Infrastructure.Data;

namespace Mode.Api.Files;

public sealed record FileVersionJobPayload(Guid VersionId);

/// <summary>Checks the real file type, scans for malware and records the hash. Blocked or infected originals are deleted.</summary>
public sealed class FileScanJob(
    ModeDbContext db, IFileStore store, IVirusScanner scanner, IPreviewProcessor processor, JobQueue queue,
    FileQueries queries, RealtimeNotifier notifier, TimeProvider clock) : IJobHandler
{
    public static readonly HashSet<string> Previewable = new(StringComparer.Ordinal)
        { "png", "jpg", "jpeg", "gif", "webp", "pdf", "docx", "xlsx", "pptx", "txt", "csv", "md" };

    public string Type => JobTypes.FileScan;

    public async Task HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var versionId = payload.Deserialize<FileVersionJobPayload>(JsonSerializerOptions.Web)!.VersionId;
        var version = await db.FileVersions.Include(v => v.FileAsset).SingleOrDefaultAsync(v => v.Id == versionId, ct);
        if (version is null || version.ScanStatus != ScanStatus.Pending) return;

        var extension = FileNames.ExtensionOf(version.FileAsset!.Name);
        var header = new byte[FileTypeSniffer.HeaderLength];
        int headerLength;
        try
        {
            await using var head = await store.OpenReadAsync(version.ObjectKey, ct) ?? throw new FileNotFoundException("Stored file is missing.");
            headerLength = await head.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        }
        catch (IOException ex) when (IsHostAntivirusBlock(ex))
        {
            await FinishAsync(version, ScanStatus.Infected, "Quarantined by the server's antivirus.", ct);
            return;
        }

        if (!FileTypeSniffer.Matches(extension, header.AsSpan(0, headerLength)))
        {
            await FinishAsync(version, ScanStatus.Blocked, $"The content is not a valid .{extension} file.", ct);
            return;
        }

        ScanResult result;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using var content = await store.OpenReadAsync(version.ObjectKey, ct) ?? throw new FileNotFoundException("Stored file is missing.");
            result = await scanner.ScanAsync(new HashingReadStream(content, hash), ct);
        }
        catch (ScanRejectedException ex)
        {
            await FinishAsync(version, ScanStatus.Failed, ex.Message, ct);
            return;
        }
        catch (IOException ex) when (IsHostAntivirusBlock(ex))
        {
            await FinishAsync(version, ScanStatus.Infected, "Quarantined by the server's antivirus.", ct);
            return;
        }

        version.Sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!result.IsClean)
        {
            await FinishAsync(version, ScanStatus.Infected, result.Threat, ct);
            return;
        }

        var preview = processor.IsEnabled && Previewable.Contains(extension);
        version.PreviewStatus = preview ? PreviewStatus.Pending : PreviewStatus.NotSupported;
        if (preview) queue.Enqueue(JobTypes.FilePreview, new FileVersionJobPayload(version.Id));
        await FinishAsync(version, ScanStatus.Clean, null, ct);
    }

    public async Task OnGaveUpAsync(JsonElement payload, string error, CancellationToken ct)
    {
        var versionId = payload.Deserialize<FileVersionJobPayload>(JsonSerializerOptions.Web)!.VersionId;
        var version = await db.FileVersions.Include(v => v.FileAsset).SingleOrDefaultAsync(v => v.Id == versionId, ct);
        if (version is { ScanStatus: ScanStatus.Pending })
            await FinishAsync(version, ScanStatus.Failed, "The file could not be scanned.", ct);
    }

    private async Task FinishAsync(FileVersion version, ScanStatus status, string? detail, CancellationToken ct)
    {
        version.ScanStatus = status;
        version.ScanDetail = detail?.Length > 500 ? detail[..500] : detail;
        version.ScannedAt = clock.GetUtcNow();
        if (status != ScanStatus.Clean)
        {
            version.PreviewStatus = PreviewStatus.NotSupported;
            try
            {
                await store.DeleteAsync(version.ObjectKey, ct);
            }
            catch (IOException ex) when (IsHostAntivirusBlock(ex))
            {
                // The host antivirus already holds the file; nothing can read it.
            }
        }
        await db.SaveChangesAsync(ct);

        if (await queries.GetAsync(version.FileAssetId, ct) is { } dto)
            await notifier.FileUpdatedAsync(dto);
    }

    /// <summary>Windows reports ERROR_VIRUS_INFECTED (225) or ERROR_VIRUS_DELETED (226) when Defender holds the file.</summary>
    private static bool IsHostAntivirusBlock(IOException ex) => (ex.HResult & 0xFFFF) is 225 or 226;

    /// <summary>Hashes bytes as they are read, so the file is read once for scanning and hashing.</summary>
    private sealed class HashingReadStream(Stream inner, IncrementalHash hash) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            hash.AppendData(buffer, offset, read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            hash.AppendData(buffer.Span[..read]);
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Asks the Python processor for thumbnails, a preview image and extracted text.</summary>
public sealed class FilePreviewJob(
    ModeDbContext db, IPreviewProcessor processor, FileLinkSigner signer, IOptions<FilesOptions> options,
    FileQueries queries, RealtimeNotifier notifier) : IJobHandler
{
    public const int MaxTextLength = 1_000_000;
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(15);

    public string Type => JobTypes.FilePreview;

    public static string DerivedKey(Guid versionId, FileVariant variant) =>
        $"derived/{versionId:N}/{variant.ToString().ToLowerInvariant()}.png";

    public async Task HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var versionId = payload.Deserialize<FileVersionJobPayload>(JsonSerializerOptions.Web)!.VersionId;
        var version = await db.FileVersions.Include(v => v.FileAsset).SingleOrDefaultAsync(v => v.Id == versionId, ct);
        if (version is not { ScanStatus: ScanStatus.Clean, PreviewStatus: PreviewStatus.Pending }) return;

        var baseUrl = options.Value.ProcessorCallbackBaseUrl.TrimEnd('/');
        string Link(LinkPurpose purpose, FileVariant variant) =>
            $"{baseUrl}/internal/files/{signer.Create(purpose, versionId, variant, LinkLifetime)}";

        var extension = FileNames.ExtensionOf(version.FileAsset!.Name);
        var result = await processor.ProcessAsync(new PreviewRequest(
            versionId, extension, version.ContentType,
            Link(LinkPurpose.ProcessorRead, FileVariant.Original),
            Link(LinkPurpose.ProcessorWrite, FileVariant.Thumbnail),
            Link(LinkPurpose.ProcessorWrite, FileVariant.Preview)), ct);

        version.PreviewStatus = result.Supported ? PreviewStatus.Ready : PreviewStatus.NotSupported;
        version.ThumbnailKey = result.Thumbnail ? DerivedKey(versionId, FileVariant.Thumbnail) : null;
        version.PreviewKey = result.Preview ? DerivedKey(versionId, FileVariant.Preview) : null;
        version.ExtractedText = CleanText(result.Text);
        await db.SaveChangesAsync(ct);

        if (await queries.GetAsync(version.FileAssetId, ct) is { } dto)
            await notifier.FileUpdatedAsync(dto);
    }

    public async Task OnGaveUpAsync(JsonElement payload, string error, CancellationToken ct)
    {
        var versionId = payload.Deserialize<FileVersionJobPayload>(JsonSerializerOptions.Web)!.VersionId;
        await db.FileVersions.Where(v => v.Id == versionId && v.PreviewStatus == PreviewStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.PreviewStatus, PreviewStatus.Failed), ct);
    }

    /// <summary>PostgreSQL text cannot hold NUL characters.</summary>
    private static string? CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Replace("\0", "");
        return text.Length > MaxTextLength ? text[..MaxTextLength] : text;
    }
}

/// <summary>Removes files deleted longer ago than the recovery window, and abandoned uploads.</summary>
public sealed class FilePurgeJob(ModeDbContext db, IFileStore store, IOptions<FilesOptions> options, TimeProvider clock) : IJobHandler
{
    private const int BatchSize = 200;

    public string Type => JobTypes.FilesPurge;

    public async Task HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var cutoff = now.AddDays(-options.Value.RecoveryDays);

        var expired = await db.FileAssets.Include(f => f.Versions)
            .Where(f => f.DeletedAt != null && f.DeletedAt < cutoff)
            .OrderBy(f => f.DeletedAt)
            .Take(BatchSize)
            .ToListAsync(ct);
        foreach (var asset in expired)
        {
            foreach (var version in asset.Versions)
                foreach (var key in new[] { version.ObjectKey, version.ThumbnailKey, version.PreviewKey }.OfType<string>())
                    await store.DeleteAsync(key, ct);
            db.FileAssets.Remove(asset);
        }

        var abandoned = await db.UploadSessions
            .Where(u => u.CompletedAt == null && u.ExpiresAt < now.AddHours(-1))
            .OrderBy(u => u.ExpiresAt)
            .Take(BatchSize)
            .ToListAsync(ct);
        foreach (var session in abandoned)
            await store.DeleteAsync(session.StorageKey, ct);
        db.UploadSessions.RemoveRange(abandoned);

        await db.SaveChangesAsync(ct);
    }
}
