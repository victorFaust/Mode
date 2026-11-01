using Microsoft.EntityFrameworkCore;
using Mode.Api.Auditing;
using Mode.Api.Auth;
using Mode.Api.Authorization;
using Mode.Api.Notifications;
using Mode.Api.Realtime;
using Mode.Domain.Directory;
using Mode.Domain.Entities;
using Mode.Infrastructure.Data;

namespace Mode.Api.Endpoints;

public static class WorkspaceEndpoints
{
    public static RouteGroupBuilder MapWorkspaceEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/workspaces").WithTags("Workspaces");
        group.MapGet("/", ListMine);
        group.MapPost("/", Create);
        group.MapGet("/{workspaceId:guid}", Get);
        group.MapPatch("/{workspaceId:guid}", Update);
        group.MapGet("/{workspaceId:guid}/members", ListMembers);
        group.MapPost("/{workspaceId:guid}/members", AddMember);
        group.MapPatch("/{workspaceId:guid}/members/{userId}", ChangeRole);
        group.MapDelete("/{workspaceId:guid}/members/{userId}", RemoveMember);
        return api;
    }

    private static async Task<IResult> ListMine(CurrentUser user, ModeDbContext db, CancellationToken ct)
    {
        var rows = await db.WorkspaceMembers
            .Where(m => m.UserId == user.Id)
            .OrderBy(m => m.Workspace!.Name)
            .Select(m => new { m.Workspace!.Id, m.Workspace.Name, m.Workspace.Description, m.Role, m.Workspace.CreatedAt })
            .ToListAsync(ct);

        return TypedResults.Ok(rows.Select(r =>
            new WorkspaceDto(r.Id, r.Name, r.Description, AccessPolicy.EffectiveRole(r.Role, user)!.Value, r.CreatedAt)));
    }

    private static async Task<IResult> Create(
        CreateWorkspaceRequest body, CurrentUser user, ModeDbContext db, AccessPolicy policy, IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        if (!policy.CanCreateWorkspace(user)) return Problems.Forbidden("Guests cannot create workspaces.");
        if (!Input.TryRequired(body.Name, "name", Limits.WorkspaceName, out var name, out var error)) return error;
        if (!Input.TryOptional(body.Description, "description", Limits.Description, out var description, out error)) return error;

        var now = clock.GetUtcNow();
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = name, Description = description, CreatedByUserId = user.Id, CreatedAt = now };
        workspace.Members.Add(new WorkspaceMember { UserId = user.Id, Role = WorkspaceRole.Owner, AddedByUserId = user.Id, AddedAt = now });
        var general = new Channel
        {
            Id = Guid.NewGuid(), Name = "General", NormalizedName = "general", Type = ChannelType.Standard,
            CreatedByUserId = user.Id, CreatedAt = now,
        };
        workspace.Channels.Add(general);
        db.Workspaces.Add(workspace);

        audit.Record(AuditActions.WorkspaceCreated, "workspace", workspace.Id.ToString(), workspace.Id, new { name });
        audit.Record(AuditActions.ChannelCreated, "channel", general.Id.ToString(), workspace.Id, new { general.Name, general.Type });
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/api/v1/workspaces/{workspace.Id}",
            new WorkspaceDto(workspace.Id, name, description, WorkspaceRole.Owner, now));
    }

    private static async Task<IResult> Get(Guid workspaceId, CurrentUser user, ModeDbContext db, AccessPolicy policy, CancellationToken ct)
    {
        var role = await policy.GetRoleAsync(user, workspaceId, ct);
        if (role is null) return Problems.NotFound();

        var workspace = await db.Workspaces.SingleAsync(w => w.Id == workspaceId, ct);
        return TypedResults.Ok(new WorkspaceDto(workspace.Id, workspace.Name, workspace.Description, role.Value, workspace.CreatedAt));
    }

    private static async Task<IResult> Update(
        Guid workspaceId, UpdateWorkspaceRequest body, CurrentUser user, ModeDbContext db, AccessPolicy policy, IAuditLog audit, CancellationToken ct)
    {
        var permission = await policy.ForWorkspaceAsync(user, workspaceId, ct);
        if (!permission.Has(Permission.Read)) return Problems.NotFound();
        if (!permission.Has(Permission.Manage)) return Problems.Forbidden();

        var workspace = await db.Workspaces.SingleAsync(w => w.Id == workspaceId, ct);
        if (body.Name is not null)
        {
            if (!Input.TryRequired(body.Name, "name", Limits.WorkspaceName, out var name, out var error)) return error;
            workspace.Name = name;
        }
        if (body.Description is not null)
        {
            if (!Input.TryOptional(body.Description, "description", Limits.Description, out var description, out var error)) return error;
            workspace.Description = description;
        }

        audit.Record(AuditActions.WorkspaceUpdated, "workspace", workspaceId.ToString(), workspaceId, new { workspace.Name });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new WorkspaceDto(workspace.Id, workspace.Name, workspace.Description, WorkspaceRole.Owner, workspace.CreatedAt));
    }

    private static async Task<IResult> ListMembers(
        Guid workspaceId, CurrentUser user, ModeDbContext db, AccessPolicy policy, IUserDirectory directory, CancellationToken ct)
    {
        var permission = await policy.ForWorkspaceAsync(user, workspaceId, ct);
        if (!permission.Has(Permission.Read)) return Problems.NotFound();
        if (!permission.Has(Permission.ViewMembers)) return Problems.Forbidden();

        var members = await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspaceId).ToListAsync(ct);
        var users = await directory.GetManyAsync(members.Select(m => m.UserId), ct);
        return TypedResults.Ok(members
            .Select(m => new WorkspaceMemberDto(ToUserDto(users, m.UserId), m.Role, m.AddedAt))
            .OrderBy(m => m.User.DisplayName, StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<IResult> AddMember(
        Guid workspaceId, AddWorkspaceMemberRequest body, CurrentUser user, ModeDbContext db, AccessPolicy policy,
        IUserDirectory directory, IAuditLog audit, NotificationService notifications, TimeProvider clock, CancellationToken ct)
    {
        var permission = await policy.ForWorkspaceAsync(user, workspaceId, ct);
        if (!permission.Has(Permission.Read)) return Problems.NotFound();
        if (!permission.Has(Permission.Manage)) return Problems.Forbidden();
        if (body.Role is null) return Problems.Invalid("role", "Required.");

        var target = string.IsNullOrWhiteSpace(body.UserId) ? null : await directory.GetByIdAsync(body.UserId, ct);
        if (target is null || !target.IsActive) return Problems.Invalid("userId", "No active user with this id.");

        if (await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == workspaceId && m.UserId == target.Id, ct))
            return Problems.Conflict("User is already a member of this workspace.");

        var member = new WorkspaceMember
        {
            WorkspaceId = workspaceId, UserId = target.Id, Role = body.Role.Value, AddedByUserId = user.Id, AddedAt = clock.GetUtcNow(),
        };
        db.WorkspaceMembers.Add(member);
        audit.Record(AuditActions.MemberAdded, "user", target.Id, workspaceId, new { role = member.Role });
        var activity = await notifications.PrepareAsync(
            [new(target.Id, ActivityType.AddedToWorkspace, user.Id, workspaceId, null, null, null)], ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (Problems.IsUniqueViolation(ex))
        {
            return Problems.Conflict("User is already a member of this workspace.");
        }
        await notifications.DeliverAsync(activity, ct);

        return TypedResults.Created($"/api/v1/workspaces/{workspaceId}/members/{target.Id}",
            new WorkspaceMemberDto(UserDto.From(target), member.Role, member.AddedAt));
    }

    private static async Task<IResult> ChangeRole(
        Guid workspaceId, string userId, ChangeRoleRequest body, CurrentUser user, ModeDbContext db, AccessPolicy policy,
        IUserDirectory directory, IAuditLog audit, AccessRevoker revoker, CancellationToken ct)
    {
        if (body.Role is null) return Problems.Invalid("role", "Required.");
        if (!directory.TryNormalizeId(userId, out var targetId)) return Problems.NotFound();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockWorkspaceAsync(db, workspaceId, ct);

        // Checked after the lock so two owners demoting each other cannot both pass with stale roles.
        var permission = await policy.ForWorkspaceAsync(user, workspaceId, ct);
        if (!permission.Has(Permission.Read)) return Problems.NotFound();
        if (!permission.Has(Permission.Manage)) return Problems.Forbidden();

        var member = await db.WorkspaceMembers.SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == targetId, ct);
        if (member is null) return Problems.NotFound();

        if (member.Role == WorkspaceRole.Owner && body.Role != WorkspaceRole.Owner && await IsLastOwnerAsync(db, workspaceId, ct))
            return Problems.Conflict("A workspace must keep at least one owner.");

        var previous = member.Role;
        member.Role = body.Role.Value;
        audit.Record(AuditActions.MemberRoleChanged, "user", targetId, workspaceId, new { from = previous, to = member.Role });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await revoker.ReevaluateAsync(targetId, ct);

        var users = await directory.GetManyAsync([targetId], ct);
        return TypedResults.Ok(new WorkspaceMemberDto(ToUserDto(users, targetId), member.Role, member.AddedAt));
    }

    private static async Task<IResult> RemoveMember(
        Guid workspaceId, string userId, CurrentUser user, ModeDbContext db, AccessPolicy policy,
        IUserDirectory directory, IAuditLog audit, AccessRevoker revoker, CancellationToken ct)
    {
        if (!directory.TryNormalizeId(userId, out var targetId)) return Problems.NotFound();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockWorkspaceAsync(db, workspaceId, ct);

        var permission = await policy.ForWorkspaceAsync(user, workspaceId, ct);
        if (!permission.Has(Permission.Read)) return Problems.NotFound();
        var leavingSelf = targetId == user.Id;
        if (!leavingSelf && !permission.Has(Permission.Manage)) return Problems.Forbidden();

        var member = await db.WorkspaceMembers.SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == targetId, ct);
        if (member is null) return Problems.NotFound();

        if (member.Role == WorkspaceRole.Owner && await IsLastOwnerAsync(db, workspaceId, ct))
            return Problems.Conflict("A workspace must keep at least one owner.");

        db.WorkspaceMembers.Remove(member);
        // Drop channel memberships too, so re-adding the user later does not silently restore private channels.
        await db.ChannelMembers
            .Where(cm => cm.UserId == targetId && cm.Channel!.WorkspaceId == workspaceId)
            .ExecuteDeleteAsync(ct);

        audit.Record(AuditActions.MemberRemoved, "user", targetId, workspaceId, new { role = member.Role, leftVoluntarily = leavingSelf });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await revoker.ReevaluateAsync(targetId, ct);
        return TypedResults.NoContent();
    }

    private static Task LockWorkspaceAsync(ModeDbContext db, Guid workspaceId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR UPDATE", ct);

    private static async Task<bool> IsLastOwnerAsync(ModeDbContext db, Guid workspaceId, CancellationToken ct) =>
        await db.WorkspaceMembers.CountAsync(m => m.WorkspaceId == workspaceId && m.Role == WorkspaceRole.Owner, ct) <= 1;

    internal static UserDto ToUserDto(IReadOnlyDictionary<string, DirectoryUser> users, string id) =>
        users.TryGetValue(id, out var found) ? UserDto.From(found) : UserDto.Unknown(id);
}
