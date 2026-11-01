namespace Mode.Domain.Entities;

public class ActivityItem
{
    public Guid Id { get; set; }

    /// <summary>Recipient.</summary>
    public required string UserId { get; set; }

    public ActivityType Type { get; set; }
    public required string ActorUserId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public Guid? ChannelId { get; set; }
    public Guid? ConversationId { get; set; }
    public Guid? MessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>Append-only. Holds ids as plain values (no foreign keys) so entries outlive what they describe.</summary>
public class AuditLog
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string? ActorUserId { get; set; }
    public required string Action { get; set; }
    public required string TargetType { get; set; }
    public string? TargetId { get; set; }
    public Guid? WorkspaceId { get; set; }
    public string? DataJson { get; set; }
    public string? IpAddress { get; set; }
}

/// <summary>Mode-only preferences keyed by the host user id. There is no Mode user table.</summary>
public class UserSettings
{
    public required string UserId { get; set; }
    public string PreferencesJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Last-read position of a user in one channel or conversation.</summary>
public class ReadMarker
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public Guid? ChannelId { get; set; }
    public Guid? ConversationId { get; set; }
    public long LastReadSequence { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
