using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mode.Api.Files.Scanning;
using Mode.Api.Jobs;
using Mode.Api.Tests.Infrastructure;
using Mode.Domain.Entities;

namespace Mode.Api.Tests.Files;

[Collection(ApiCollection.Name)]
public sealed class JobQueueTests(ApiFixture api)
{
    private async Task EnqueueAsync(params (string Type, object Payload, int MaxAttempts)[] jobs)
    {
        await using var scope = api.Factory.Services.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
        foreach (var (type, payload, maxAttempts) in jobs) queue.Enqueue(type, payload, maxAttempts);
        await scope.ServiceProvider.GetRequiredService<Mode.Infrastructure.Data.ModeDbContext>().SaveChangesAsync();
    }

    [Fact]
    public async Task Concurrent_workers_run_each_job_exactly_once()
    {
        CountingJobHandler.Runs.Clear();
        await EnqueueAsync(Enumerable.Range(0, 40).Select(n => ("test.count", (object)new { n }, 5)).ToArray());
        var runner = api.Factory.Services.GetRequiredService<JobRunner>();

        var counts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => runner.RunAllDueAsync())));

        Assert.Equal(40, CountingJobHandler.Runs.Count);
        Assert.All(CountingJobHandler.Runs.Values, runs => Assert.Equal(1, runs));
        Assert.True(counts.Sum() >= 40);
        await using var db = api.CreateDbContext();
        Assert.False(await db.Jobs.AnyAsync(j => j.Type == "test.count" && j.Status != JobStatus.Done));
    }

    [Fact]
    public async Task Failing_job_is_retried_later_and_gives_up_after_max_attempts()
    {
        var retried = Guid.NewGuid().ToString();
        var final = Guid.NewGuid().ToString();
        await EnqueueAsync(("test.fail", new { id = retried }, 3), ("test.fail", new { id = final }, 1));

        await api.RunJobsAsync();

        await using var db = api.CreateDbContext();
        var jobs = await db.Jobs.Where(j => j.Type == "test.fail").OrderByDescending(j => j.Id).Take(2).ToListAsync();
        var retriedJob = jobs.Single(j => j.MaxAttempts == 3);
        var finalJob = jobs.Single(j => j.MaxAttempts == 1);
        Assert.Equal((JobStatus.Pending, 1, "boom"), (retriedJob.Status, retriedJob.Attempts, retriedJob.LastError));
        Assert.True(retriedJob.RunAfter > DateTimeOffset.UtcNow);
        Assert.Equal(JobStatus.Failed, finalJob.Status);
        Assert.Contains(final, FailingJobHandler.GaveUp);
        Assert.DoesNotContain(retried, FailingJobHandler.GaveUp);
    }
}

/// <summary>Runs only when MODE_TEST_CLAMAV is set to host:port of a running clamd (e.g. localhost:3310).</summary>
public sealed class ClamAvFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "MODE_TEST_CLAMAV";

    public ClamAvFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)))
            Skip = $"{EnvironmentVariable} is not set.";
    }
}

public sealed class ClamAvScannerTests
{
    private static ClamAvScanner Scanner()
    {
        var parts = Environment.GetEnvironmentVariable(ClamAvFactAttribute.EnvironmentVariable)!.Split(':');
        return new ClamAvScanner(Options.Create(new ClamAvOptions { Host = parts[0], Port = int.Parse(parts[1]) }));
    }

    [ClamAvFact]
    public async Task Eicar_is_detected() =>
        Assert.False((await Scanner().ScanAsync(new MemoryStream(Encoding.ASCII.GetBytes(TestFiles.Eicar)), default)).IsClean);

    [ClamAvFact]
    public async Task Ordinary_content_is_clean() =>
        Assert.True((await Scanner().ScanAsync(new MemoryStream(Encoding.UTF8.GetBytes("quarterly report")), default)).IsClean);

    [Fact]
    public async Task Unreachable_scanner_throws_so_the_job_is_retried() =>
        await Assert.ThrowsAnyAsync<SocketException>(() =>
            new ClamAvScanner(Options.Create(new ClamAvOptions { Host = "127.0.0.1", Port = 1 }))
                .ScanAsync(new MemoryStream([1]), default));
}
