namespace Mode.Domain.Entities;

/// <summary>How long a workspace keeps channel content. Chats are not covered and are kept forever (D46).</summary>
public class RetentionPolicy
{
    public Guid WorkspaceId { get; set; }

    /// <summary>Null keeps content forever.</summary>
    public int? KeepDays { get; set; }

    public required string UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum HoldTarget { Channel, Conversation, User }

/// <summary>While active, nothing in the target (or written by the target user) is deleted by retention.</summary>
public class LegalHold
{
    public Guid Id { get; set; }
    public HoldTarget TargetType { get; set; }
    public required string TargetId { get; set; }
    public required string Reason { get; set; }
    public required string CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? ReleasedByUserId { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
}

/// <summary>An incoming webhook that posts into one room as a named bot. Only a hash of the secret is stored.</summary>
public class Webhook
{
    public Guid Id { get; set; }
    public Guid? ChannelId { get; set; }
    public Guid? ConversationId { get; set; }
    public required string Name { get; set; }
    public required string SecretHash { get; set; }
    public required string CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RotatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

public enum DlpAction { Warn, Block }

public sealed record DlpPattern(string Name, string Pattern, DlpAction Action);

/// <summary>Organization-wide data-loss settings; a single row with <see cref="Id"/> 1.</summary>
public class DlpSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public List<string> BlockedExtensions { get; set; } = [];
    public List<DlpPattern> Patterns { get; set; } = [];
    public string? UpdatedByUserId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum ExportStatus { Pending, Ready, Failed }

public class ComplianceExport
{
    public Guid Id { get; set; }
    public required string RequestedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string FiltersJson { get; set; }
    public ExportStatus Status { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? StorageKey { get; set; }
    public long SizeBytes { get; set; }
    public int MessageCount { get; set; }
    public int FileCount { get; set; }
    public string? ManifestSha256 { get; set; }
    public string? ManifestSignature { get; set; }
    public string? Error { get; set; }
}
