using System.Text.Json;
using Mode.Api.Auth;
using Mode.Domain.Entities;
using Mode.Infrastructure.Data;

namespace Mode.Api.Auditing;

public static class AuditActions
{
    public const string SignIn = "auth.sign_in";
    public const string RealtimeConnected = "auth.realtime_connected";
    public const string RealtimeRevoked = "auth.realtime_revoked";
    public const string WorkspaceCreated = "workspace.created";
    public const string WorkspaceUpdated = "workspace.updated";
    public const string MemberAdded = "workspace.member_added";
    public const string MemberRoleChanged = "workspace.member_role_changed";
    public const string MemberRemoved = "workspace.member_removed";
    public const string ChannelCreated = "channel.created";
    public const string ChannelUpdated = "channel.updated";
    public const string ChannelArchived = "channel.archived";
    public const string ChannelMemberAdded = "channel.member_added";
    public const string ChannelMemberRemoved = "channel.member_removed";
    public const string RetentionChanged = "retention.changed";
    public const string RetentionApplied = "retention.applied";
    public const string RetentionChecked = "retention.checked";
    public const string HoldPlaced = "hold.placed";
    public const string HoldReleased = "hold.released";
    public const string DlpUpdated = "dlp.updated";
    public const string WebhookCreated = "webhook.created";
    public const string WebhookRotated = "webhook.rotated";
    public const string WebhookRevoked = "webhook.revoked";
    public const string ExportRequested = "compliance.export_requested";
    public const string ExportDownloaded = "compliance.export_downloaded";
    public const string AuditExported = "audit.exported";
}

/// <summary>Adds entries to the current unit of work; they are saved with the change they describe.</summary>
public interface IAuditLog
{
    void Record(string action, string targetType, string? targetId, Guid? workspaceId = null, object? data = null, string? actorUserId = null);
}

internal sealed class AuditLogWriter(ModeDbContext db, IHttpContextAccessor http, TimeProvider clock) : IAuditLog
{
    public void Record(string action, string targetType, string? targetId, Guid? workspaceId = null, object? data = null, string? actorUserId = null)
    {
        var context = http.HttpContext;
        db.AuditLogs.Add(new AuditLog
        {
            At = clock.GetUtcNow(),
            ActorUserId = actorUserId ?? context?.User.FindFirst(ModeClaims.UserId)?.Value,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            WorkspaceId = workspaceId,
            DataJson = data is null ? null : JsonSerializer.Serialize(data, JsonSerializerOptions.Web),
            IpAddress = context?.Connection.RemoteIpAddress?.ToString(),
        });
    }
}
