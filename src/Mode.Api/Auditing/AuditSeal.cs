using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mode.Domain.Entities;
using Mode.Infrastructure.Data;

namespace Mode.Api.Auditing;

public sealed class AuditOptions
{
    public const string SectionName = "Audit";

    /// <summary>Base64, at least 32 bytes. Kept outside the database so rewriting entries can't recompute the chain.</summary>
    public string ChainKey { get; set; } = "";

    /// <summary>Entries are sealed once they are this old, so transactions still in flight keep their place in the chain.</summary>
    public int SealDelaySeconds { get; set; } = 60;

    public int SealIntervalSeconds { get; set; } = 60;

    internal byte[]? KeyBytes()
    {
        try
        {
            var key = Convert.FromBase64String(ChainKey);
            return key.Length >= 32 ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

public sealed record AuditVerifyResult(bool Intact, long Checked, long? BrokenAtId, long Unsealed, long? LastSealedId);

/// <summary>
/// Makes the append-only audit log tamper-evident: each entry's Hash is an HMAC over the previous Hash and the entry,
/// so editing, inserting or deleting a sealed entry breaks every link after it.
/// </summary>
public sealed class AuditSealer(ModeDbContext db, IOptions<AuditOptions> options, TimeProvider clock, ILogger<AuditSealer> logger)
{
    private const long LockKey = 7_243_100;
    private const int Batch = 1000;

    public async Task<int> SealAsync(CancellationToken ct)
    {
        var key = options.Value.KeyBytes() ?? throw new InvalidOperationException("Audit:ChainKey is not configured.");
        var total = 0;
        while (true)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({LockKey})", ct);

            var last = await db.AuditLogs.AsNoTracking().Where(a => a.Hash != null).OrderByDescending(a => a.Id)
                .Select(a => new { a.Id, a.Hash }).FirstOrDefaultAsync(ct);
            var cutoff = clock.GetUtcNow().AddSeconds(-options.Value.SealDelaySeconds);
            var lastId = last?.Id ?? 0;
            var rows = await db.AuditLogs.AsNoTracking()
                .Where(a => a.Hash == null && a.Id > lastId && a.At <= cutoff)
                .OrderBy(a => a.Id).Take(Batch).ToListAsync(ct);

            var previous = last?.Hash;
            foreach (var row in rows)
            {
                var hash = Link(key, previous, row);
                await db.AuditLogs.Where(a => a.Id == row.Id && a.Hash == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.Hash, hash), ct);
                previous = hash;
            }
            await transaction.CommitAsync(ct);
            total += rows.Count;

            if (rows.Count > 0)
                // The latest link also goes to the log platform, an anchor outside the database for the newest entries.
                logger.LogInformation("Audit log sealed through entry {AuditId} with hash {AuditHash}", rows[^1].Id, previous);
            if (rows.Count < Batch) return total;
        }
    }

    public async Task<AuditVerifyResult> VerifyAsync(CancellationToken ct)
    {
        var key = options.Value.KeyBytes() ?? throw new InvalidOperationException("Audit:ChainKey is not configured.");
        string? previous = null;
        long count = 0, lastId = 0;
        long? brokenAt = null;
        await foreach (var row in db.AuditLogs.AsNoTracking().Where(a => a.Hash != null).OrderBy(a => a.Id).AsAsyncEnumerable().WithCancellation(ct))
        {
            var expected = Link(key, previous, row);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(row.Hash!.Trim())))
            {
                brokenAt = row.Id;
                break;
            }
            previous = row.Hash!.Trim();
            lastId = row.Id;
            count++;
        }
        return new AuditVerifyResult(brokenAt is null, count, brokenAt, await UnsealedAsync(ct), lastId == 0 ? null : lastId);
    }

    // Older than the seal delay plus a margin: these should have been sealed, so they were added out of order or the sealer stopped.
    private Task<int> UnsealedAsync(CancellationToken ct)
    {
        var overdue = clock.GetUtcNow().AddSeconds(-options.Value.SealDelaySeconds - Math.Max(300, 2 * options.Value.SealIntervalSeconds));
        return db.AuditLogs.CountAsync(a => a.Hash == null && a.At < overdue, ct);
    }

    internal static string Link(byte[] key, string? previous, AuditLog row)
    {
        var canonical = JsonSerializer.Serialize(new object?[]
        {
            row.Id, row.At.UtcDateTime.ToString("O"), row.ActorUserId, row.Action, row.TargetType, row.TargetId,
            row.WorkspaceId?.ToString(), row.DataJson, row.IpAddress,
        });
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes((previous ?? "") + "\n" + canonical)));
    }
}

public sealed class AuditSealWorker(IServiceScopeFactory scopes, IOptions<AuditOptions> options, ILogger<AuditSealWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.Value.SealIntervalSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken).AsTask().ContinueWith(t => !t.IsCanceled && t.Result, CancellationToken.None))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<AuditSealer>().SealAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Sealing the audit log failed; will retry");
            }
        }
    }
}
