using Mode.Domain.Directory;
using Mode.Domain.Entities;

namespace Mode.Api.Endpoints;

public sealed record UserDto(string Id, string DisplayName, string? Email, string? AvatarUrl, bool IsActive)
{
    public static UserDto From(DirectoryUser user) => new(user.Id, user.DisplayName, user.Email, user.AvatarUrl, user.IsActive);

    /// <summary>For ids no longer present in the host directory.</summary>
    public static UserDto Unknown(string id) => new(id, "Unknown user", null, null, false);
}

public sealed record MeDto(UserDto User, bool IsAdmin, bool IsGuest);

public sealed record WorkspaceDto(Guid Id, string Name, string? Description, WorkspaceRole MyRole, DateTimeOffset CreatedAt);

public sealed record WorkspaceMemberDto(UserDto User, WorkspaceRole Role, DateTimeOffset AddedAt);

public sealed record ChannelDto(
    Guid Id,
    Guid WorkspaceId,
    string Name,
    string? Description,
    ChannelType Type,
    bool IsArchived,
    DateTimeOffset CreatedAt,
    bool CanPost,
    bool CanManage);

public sealed record ChannelMemberDto(UserDto User, WorkspaceRole Role);

public sealed record CreateWorkspaceRequest(string? Name, string? Description);

public sealed record UpdateWorkspaceRequest(string? Name, string? Description);

public sealed record AddWorkspaceMemberRequest(string? UserId, WorkspaceRole? Role);

public sealed record ChangeRoleRequest(WorkspaceRole? Role);

public sealed record CreateChannelRequest(string? Name, string? Description, ChannelType? Type);

public sealed record UpdateChannelRequest(string? Name, string? Description);

public sealed record AddChannelMemberRequest(string? UserId);
