using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mode.Domain.Entities;
using Mode.Infrastructure.Data;

namespace Mode.Api.Jobs;

public sealed class JobOptions
{
    public const string SectionName = "Jobs";

    /// <summary>Runs the background worker. Tests turn it off and drive <see cref="JobRunner"/> directly.</summary>
    public bool Enabled { get; set; } = true;

    public int Workers { get; set; } = 2;
    public int PollSeconds { get; set; } = 2;
    public int LockMinutes { get; set; } = 10;
    /// <summary>How often recurring jobs (file purge, activity expiry, email digest) are queued; capped at 60.</summary>
    public int PurgeIntervalMinutes { get; set; } = 60;
}

public interface IJobHandler
{
    string Type { get; }

    Task HandleAsync(JsonElement payload, CancellationToken cancellationToken);

    /// <summary>Called once when the last attempt has failed.</summary>
    Task OnGaveUpAsync(JsonElement payload, string error, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Adds jobs to the current unit of work, so they commit (or roll back) with the change that caused them.</summary>
public sealed class JobQueue(ModeDbContext db, TimeProvider clock)
{
    public void Enqueue(string type, object payload, int maxAttempts = 5)
    {
        var now = clock.GetUtcNow();
        db.Jobs.Add(new Job
        {
            Type = type,
            PayloadJson = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
            Status = JobStatus.Pending,
            MaxAttempts = maxAttempts,
            RunAfter = now,
            CreatedAt = now,
        });
    }
}

public sealed class JobRunner(IServiceScopeFactory scopes, IOptions<JobOptions> options, TimeProvider clock, ILogger<JobRunner> logger)
{
    /// <summary>Claims and runs one due job. Returns false when none is due.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ModeDbContext>();
        var now = clock.GetUtcNow();
        var lockedUntil = now.AddMinutes(options.Value.LockMinutes);

        // SKIP LOCKED lets several workers poll at once without ever claiming the same job; expired locks are retried.
        var claimed = await db.Jobs.FromSql($"""
            UPDATE "Jobs" SET "Status" = 'Running', "Attempts" = "Attempts" + 1, "LockedUntil" = {lockedUntil}
            WHERE "Id" = (
                SELECT "Id" FROM "Jobs"
                WHERE ("Status" = 'Pending' AND "RunAfter" <= {now}) OR ("Status" = 'Running' AND "LockedUntil" < {now})
                ORDER BY "Id"
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING *
            """).AsNoTracking().ToListAsync(ct);
        if (claimed.Count == 0) return false;

        var job = claimed[0];
        var handler = scope.ServiceProvider.GetServices<IJobHandler>().FirstOrDefault(h => h.Type == job.Type);
        using var payload = JsonDocument.Parse(job.PayloadJson);
        try
        {
            if (handler is null) throw new InvalidOperationException($"No handler for job type '{job.Type}'.");
            await handler.HandleAsync(payload.RootElement, ct);
            await db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Done)
                .SetProperty(j => j.CompletedAt, clock.GetUtcNow())
                .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var giveUp = handler is null || job.Attempts >= job.MaxAttempts;
            var error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            if (giveUp)
                logger.LogError(ex, "Job {JobId} ({JobType}) gave up after {Attempt} attempts", job.Id, job.Type, job.Attempts);
            else
                logger.LogWarning(ex, "Job {JobId} ({JobType}) failed on attempt {Attempt}", job.Id, job.Type, job.Attempts);

            await db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, giveUp ? JobStatus.Failed : JobStatus.Pending)
                .SetProperty(j => j.RunAfter, clock.GetUtcNow().AddSeconds(Math.Min(3600, 10 * Math.Pow(2, job.Attempts))))
                .SetProperty(j => j.LastError, error)
                .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null), ct);

            if (giveUp && handler is not null)
                await handler.OnGaveUpAsync(payload.RootElement, error, ct);
        }
        return true;
    }

    /// <summary>Runs jobs until none is due. Returns how many ran.</summary>
    public async Task<int> RunAllDueAsync(CancellationToken ct = default)
    {
        var count = 0;
        while (await RunOnceAsync(ct)) count++;
        return count;
    }
}

public sealed class JobWorker(JobRunner runner, IServiceScopeFactory scopes, IOptions<JobOptions> options, ILogger<JobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled) return;

        var loops = Enumerable.Range(0, Math.Max(1, settings.Workers)).Select(_ => PollAsync(settings, stoppingToken)).ToList();
        loops.Add(ScheduleAsync(settings, stoppingToken));
        await Task.WhenAll(loops);
    }

    private async Task PollAsync(JobOptions settings, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await runner.RunOnceAsync(ct)) continue;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Job worker loop failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(settings.PollSeconds), ct).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    private static readonly string[] Recurring = [JobTypes.FilesPurge, JobTypes.ActivityExpire, JobTypes.EmailDigest, JobTypes.RetentionApply];

    /// <summary>Queues each recurring job unless one is already waiting. At least hourly, so daily digests hit their hour.</summary>
    private async Task ScheduleAsync(JobOptions settings, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Clamp(settings.PurgeIntervalMinutes, 1, 60)));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ModeDbContext>();
                var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
                var waiting = await db.Jobs
                    .Where(j => Recurring.Contains(j.Type) && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running))
                    .Select(j => j.Type)
                    .ToListAsync(ct);
                foreach (var type in Recurring.Except(waiting))
                    queue.Enqueue(type, new { });
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Scheduling recurring jobs failed");
            }
        } while (await timer.WaitForNextTickAsync(ct).AsTask().ContinueWith(t => !t.IsCanceled && t.Result, CancellationToken.None));
    }
}

public static class JobTypes
{
    public const string FileScan = "file.scan";
    public const string FilePreview = "file.preview";
    public const string FilesPurge = "files.purge";
    public const string RetentionApply = "retention.apply";
    public const string ComplianceExport = "compliance.export";
    public const string PushSend = "push.send";
    public const string ActivityExpire = "activity.expire";
    public const string EmailDigest = "email.digest";
}
