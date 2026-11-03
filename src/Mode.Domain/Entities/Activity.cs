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

    /// <summary>Set once the item has been included in an email digest.</summary>
    public DateTimeOffset? EmailedAt { get; set; }
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

    /// <summary>HMAC chain link set by the audit sealer: HMAC(key, previous Hash + this entry). Null until sealed.</summary>
    public string? Hash { get; set; }
}

/// <summary>Mode-only preferences keyed by the host user id. There is no Mode user table.</summary>
public class UserSettings
{
    public required string UserId { get; set; }
    public string PreferencesJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Per-user state for one channel or conversation: last-read position and mute.</summary>
public class ReadMarker
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public Guid? ChannelId { get; set; }
    public Guid? ConversationId { get; set; }
    public long LastReadSequence { get; set; }
    public bool IsMuted { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A browser registered for Web Push (RFC 8030/8291).</summary>
public class PushSubscription
{
    public Guid Id { get; set; }
    public required string UserId { get; set; }
    public required string Endpoint { get; set; }
    public required string P256dh { get; set; }
    public required string Auth { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}
