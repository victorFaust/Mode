namespace Mode.Domain.Entities;

public class Channel
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public string? Description { get; set; }
    public ChannelType Type { get; set; }
    public required string CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    /// <summary>Highest message sequence issued in this channel; incremented atomically per send.</summary>
    public long LastSequence { get; set; }

    public Workspace? Workspace { get; set; }
    public List<ChannelMember> Members { get; set; } = [];
}

/// <summary>Required for private channels and for guests in standard channels.</summary>
public class ChannelMember
{
    public Guid ChannelId { get; set; }
    public required string UserId { get; set; }
    public required string AddedByUserId { get; set; }
    public DateTimeOffset AddedAt { get; set; }

    public Channel? Channel { get; set; }
}
