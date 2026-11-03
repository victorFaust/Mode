using Mode.Api.Auditing;
using Mode.Domain.Directory;
using Mode.Infrastructure.Data;

namespace Mode.Api.Realtime;

/// <summary>
/// Tokens are only checked when a connection opens, so people deactivated in the host directory
/// are disconnected here instead of keeping a live connection until their token expires.
/// </summary>
public sealed class RealtimeSessionCheck(ConnectionRegistry registry, IUserDirectory directory, IServiceScopeFactory scopes, ILogger<RealtimeSessionCheck> logger)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var userIds = registry.ConnectedUserIds();
        if (userIds.Count == 0) return 0;
        var known = await directory.GetManyAsync(userIds, ct);
        var removed = userIds.Where(id => !known.TryGetValue(id, out var user) || !user.IsActive).ToList();
        if (removed.Count == 0) return 0;

        await using var scope = scopes.CreateAsyncScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditLog>();
        var aborted = 0;
        foreach (var id in removed)
        {
            aborted += registry.AbortAll(id);
            audit.Record(AuditActions.RealtimeRevoked, "user", id, actorUserId: "system:realtime");
        }
        await scope.ServiceProvider.GetRequiredService<ModeDbContext>().SaveChangesAsync(ct);
        logger.LogInformation("Closed {Count} realtime connections of {People} deactivated people", aborted, removed.Count);
        return aborted;
    }
}

public sealed class RealtimeSessionChecker(RealtimeSessionCheck check, IConfiguration config, ILogger<RealtimeSessionChecker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, config.GetValue("Realtime:RecheckSeconds", 60))));
        while (await timer.WaitForNextTickAsync(stoppingToken).AsTask().ContinueWith(t => !t.IsCanceled && t.Result, CancellationToken.None))
        {
            try
            {
                await check.RunAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Realtime session check failed; will retry");
            }
        }
    }
}
