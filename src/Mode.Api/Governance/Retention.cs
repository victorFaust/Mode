using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mode.Api.Auditing;
using Mode.Api.Jobs;
using Mode.Domain.Entities;
using Mode.Infrastructure.Data;

namespace Mode.Api.Governance;

public sealed record RetentionRun(Guid WorkspaceId, int KeepDays, int MessagesDeleted, int EditsDeleted, int FilesDeleted);

/// <summary>
/// Applies each workspace's policy to its channels. Chats are never touched (D46). An active hold on a channel or on
/// a user wins over any delete rule: held channels are skipped, and messages and files by held users are kept.
/// A thread is removed only once every reply in it has expired.
/// </summary>
public sealed class RetentionService(ModeDbContext db, IAuditLog audit, TimeProvider clock)
{
    public const string SystemActor = "system:retention";

    public async Task<IReadOnlyList<RetentionRun>> ApplyAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var policies = await db.RetentionPolicies.AsNoTracking().Where(p => p.KeepDays != null).ToListAsync(ct);
        var heldChannels = await ActiveHoldsAsync(HoldTarget.Channel, ct);
        var heldUsers = await ActiveHoldsAsync(HoldTarget.User, ct);

        var runs = new List<RetentionRun>();
        foreach (var policy in policies)
        {
            var cutoff = now.AddDays(-policy.KeepDays!.Value);
            var channelIds = await db.Channels
                .Where(c => c.WorkspaceId == policy.WorkspaceId && !heldChannels.Contains(c.Id.ToString()))
                .Select(c => (Guid?)c.Id)
                .ToListAsync(ct);
            if (channelIds.Count == 0) continue;

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var inScope = db.Messages.Where(m => channelIds.Contains(m.ChannelId) && !heldUsers.Contains(m.AuthorUserId));

            var replies = await inScope
                .Where(m => m.ParentMessageId != null && m.CreatedAt < cutoff)
                .ExecuteDeleteAsync(ct);
            var topLevel = await inScope
                .Where(m => m.ParentMessageId == null && m.CreatedAt < cutoff && !db.Messages.Any(r => r.ParentMessageId == m.Id))
                .ExecuteDeleteAsync(ct);
            await db.Messages
                .Where(m => channelIds.Contains(m.ChannelId) && m.ParentMessageId == null && m.ReplyCount > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ReplyCount, m => db.Messages.Count(r => r.ParentMessageId == m.Id)), ct);

            // Earlier versions of messages that are kept also expire with the policy.
            var edits = await db.MessageEdits
                .Where(e => e.ChangedAt < cutoff && channelIds.Contains(e.Message!.ChannelId) && !heldUsers.Contains(e.Message.AuthorUserId))
                .ExecuteDeleteAsync(ct);

            // Files go through the normal soft-delete, so the purge job removes the bytes after the recovery window.
            var files = await db.FileAssets
                .Where(f => channelIds.Contains(f.ChannelId) && f.DeletedAt == null && !heldUsers.Contains(f.CreatedByUserId)
                    && f.Versions.Max(v => v.UploadedAt) < cutoff
                    && !db.MessageAttachments.Any(a => a.FileAssetId == f.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.DeletedAt, now)
                    .SetProperty(f => f.DeletedByUserId, SystemActor), ct);

            var run = new RetentionRun(policy.WorkspaceId, policy.KeepDays.Value, replies + topLevel, edits, files);
            audit.Record(AuditActions.RetentionApplied, "workspace", policy.WorkspaceId.ToString(), policy.WorkspaceId, run, SystemActor);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            runs.Add(run);
        }
        return runs;
    }

    private Task<List<string>> ActiveHoldsAsync(HoldTarget type, CancellationToken ct) =>
        db.LegalHolds.Where(h => h.TargetType == type && h.ReleasedAt == null).Select(h => h.TargetId).ToListAsync(ct);
}

/// <summary>Queued hourly like the other recurring jobs, but applies retention at most once every 20 hours.</summary>
public sealed class RetentionJob(ModeDbContext db, RetentionService retention, TimeProvider clock) : IJobHandler
{
    public string Type => JobTypes.RetentionApply;

    public async Task HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var since = clock.GetUtcNow().AddHours(-20);
        if (await db.AuditLogs.AnyAsync(a => a.Action == AuditActions.RetentionChecked && a.At > since, ct)) return;
        await retention.ApplyAsync(ct);
        db.AuditLogs.Add(new AuditLog
        {
            At = clock.GetUtcNow(), ActorUserId = RetentionService.SystemActor, Action = AuditActions.RetentionChecked, TargetType = "system",
        });
        await db.SaveChangesAsync(ct);
    }
}
