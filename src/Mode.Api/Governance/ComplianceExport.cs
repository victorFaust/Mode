using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mode.Api.Jobs;
using Mode.Domain.Entities;
using Mode.Domain.Files;
using Mode.Infrastructure.Data;

namespace Mode.Api.Governance;

public sealed class ComplianceOptions
{
    public const string SectionName = "Compliance";

    /// <summary>HMAC key for export manifests; base64, at least 32 bytes. Exports fail until it is set.</summary>
    public string SigningKey { get; set; } = "";

    public int MaxMessages { get; set; } = 100_000;
    public int MaxFiles { get; set; } = 1_000;

    internal byte[]? Key()
    {
        try
        {
            var key = Convert.FromBase64String(SigningKey);
            return key.Length >= 32 ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

public sealed record ExportFilters(string? UserId, Guid? ChannelId, Guid? ConversationId, DateTimeOffset? From, DateTimeOffset? To);

/// <summary>
/// Builds a ZIP of messages (with every earlier and deleted version) and files, plus manifest.json listing a SHA-256
/// per entry and manifest.sig, an HMAC-SHA256 of manifest.json. Anyone holding the key can prove nothing was changed.
/// </summary>
public sealed class ComplianceExportJob(ModeDbContext db, IFileStore store, IOptions<ComplianceOptions> options, TimeProvider clock) : IJobHandler
{
    public string Type => JobTypes.ComplianceExport;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web) { WriteIndented = false };

    public async Task HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var exportId = payload.GetProperty("exportId").GetGuid();
        var export = await db.ComplianceExports.SingleAsync(e => e.Id == exportId, ct);
        if (export.Status != ExportStatus.Pending) return;

        var settings = options.Value;
        if (settings.Key() is not { } key)
        {
            await FailAsync(export, "Compliance:SigningKey is not configured.", ct);
            return;
        }
        var filters = JsonSerializer.Deserialize<ExportFilters>(export.FiltersJson, Json)!;

        var messages = db.Messages.AsNoTracking();
        if (filters.UserId is { } userId) messages = messages.Where(m => m.AuthorUserId == userId);
        if (filters.ChannelId is { } channelId) messages = messages.Where(m => m.ChannelId == channelId);
        if (filters.ConversationId is { } conversationId) messages = messages.Where(m => m.ConversationId == conversationId);
        if (filters.From is { } from) messages = messages.Where(m => m.CreatedAt >= from);
        if (filters.To is { } to) messages = messages.Where(m => m.CreatedAt < to);
        var rows = await messages.Include(m => m.Edits).OrderBy(m => m.CreatedAt).Take(settings.MaxMessages + 1).ToListAsync(ct);
        if (rows.Count > settings.MaxMessages)
        {
            await FailAsync(export, $"More than {settings.MaxMessages} messages match; narrow the filters.", ct);
            return;
        }

        var files = db.FileAssets.AsNoTracking();
        if (filters.UserId is { } fileUser) files = files.Where(f => f.CreatedByUserId == fileUser);
        if (filters.ChannelId is { } fileChannel) files = files.Where(f => f.ChannelId == fileChannel);
        if (filters.ConversationId is { } fileConversation) files = files.Where(f => f.ConversationId == fileConversation);
        if (filters.From is { } fileFrom) files = files.Where(f => f.CreatedAt >= fileFrom);
        if (filters.To is { } fileTo) files = files.Where(f => f.CreatedAt < fileTo);
        var fileRows = await files.Include(f => f.Versions).OrderBy(f => f.CreatedAt).Take(settings.MaxFiles + 1).ToListAsync(ct);
        if (fileRows.Count > settings.MaxFiles)
        {
            await FailAsync(export, $"More than {settings.MaxFiles} files match; narrow the filters.", ct);
            return;
        }

        var entries = new List<object>();
        var temp = Path.GetTempFileName();
        try
        {
            await using (var zipStream = File.Create(temp))
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
            {
                var lines = new StringBuilder();
                foreach (var m in rows)
                {
                    lines.AppendLine(JsonSerializer.Serialize(new
                    {
                        m.Id, m.ChannelId, m.ConversationId, m.ParentMessageId, m.AuthorUserId, m.BotName, m.CreatedAt, m.EditedAt,
                        m.DeletedAt, m.DeletedByUserId, m.BodyMarkdown, m.ForwardedFromMessageId, m.ForwardedBodyMarkdown,
                        History = m.Edits.OrderBy(e => e.ChangedAt)
                            .Select(e => new { Kind = e.Kind.ToString(), e.PreviousBodyMarkdown, e.ChangedByUserId, e.ChangedAt }),
                    }, Json));
                }
                entries.Add(await AddAsync(zip, "messages.jsonl", new MemoryStream(Encoding.UTF8.GetBytes(lines.ToString())), ct));

                var fileIndex = new List<object>();
                foreach (var f in fileRows)
                foreach (var v in f.Versions.OrderBy(v => v.VersionNo))
                {
                    var path = v.ScanStatus == ScanStatus.Clean ? $"files/{f.Id:N}/v{v.VersionNo}{Path.GetExtension(f.Name)}" : null;
                    fileIndex.Add(new
                    {
                        FileId = f.Id, f.Name, f.ChannelId, f.ConversationId, f.CreatedByUserId, f.DeletedAt, v.VersionNo,
                        v.SizeBytes, v.Sha256, ScanStatus = v.ScanStatus.ToString(), v.UploadedByUserId, v.UploadedAt, Path = path,
                    });
                    if (path is null) continue;
                    await using var content = await store.OpenReadAsync(v.ObjectKey, ct);
                    if (content is not null) entries.Add(await AddAsync(zip, path, content, ct));
                }
                entries.Add(await AddAsync(zip, "files.json", new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(fileIndex, Json)), ct));

                var manifest = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    ExportId = export.Id, export.RequestedByUserId, GeneratedAt = clock.GetUtcNow(), Filters = filters,
                    MessageCount = rows.Count, FileCount = fileRows.Count, Entries = entries,
                }, new JsonSerializerOptions(Json) { WriteIndented = true });
                var signature = Convert.ToHexStringLower(HMACSHA256.HashData(key, manifest));
                await AddAsync(zip, "manifest.json", new MemoryStream(manifest), ct);
                await AddAsync(zip, "manifest.sig", new MemoryStream(Encoding.ASCII.GetBytes(signature + "\n")), ct);

                export.ManifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifest));
                export.ManifestSignature = signature;
            }

            var storageKey = $"exports/{export.Id:N}.zip";
            await using (var read = File.OpenRead(temp))
                await store.WriteAsync(storageKey, read, ct);
            export.StorageKey = storageKey;
            export.SizeBytes = new FileInfo(temp).Length;
            export.MessageCount = rows.Count;
            export.FileCount = fileRows.Count;
            export.Status = ExportStatus.Ready;
            export.CompletedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public async Task OnGaveUpAsync(JsonElement payload, string error, CancellationToken ct)
    {
        var exportId = payload.GetProperty("exportId").GetGuid();
        var export = await db.ComplianceExports.SingleAsync(e => e.Id == exportId, ct);
        await FailAsync(export, error, ct);
    }

    private async Task FailAsync(ComplianceExport export, string error, CancellationToken ct)
    {
        export.Status = ExportStatus.Failed;
        export.Error = error;
        export.CompletedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    private static async Task<object> AddAsync(ZipArchive zip, string path, Stream content, CancellationToken ct)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        await using var target = entry.Open();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long size = 0;
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            size += read;
        }
        await content.DisposeAsync();
        return new { Path = path, Size = size, Sha256 = Convert.ToHexStringLower(sha.GetHashAndReset()) };
    }
}
