namespace Mode.Domain.Entities;

public class Workspace
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public List<WorkspaceMember> Members { get; set; } = [];
    public List<Channel> Channels { get; set; } = [];
}

public class WorkspaceMember
{
    public Guid WorkspaceId { get; set; }
    public required string UserId { get; set; }
    public WorkspaceRole Role { get; set; }
    public required string AddedByUserId { get; set; }
    public DateTimeOffset AddedAt { get; set; }

    public Workspace? Workspace { get; set; }
}
