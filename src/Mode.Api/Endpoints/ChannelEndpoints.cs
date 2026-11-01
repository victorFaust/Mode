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

public static class ChannelEndpoints
{
    public static RouteGroupBuilder MapChannelEndpoints(this RouteGroupBuilder api)
    {
        var inWorkspace = api.MapGroup("/workspaces/{workspaceId:guid}/channels").WithTags("Channels");
        inWorkspace.MapGet("/", List).Produces<ChannelDto[]>();
        inWorkspace.MapPost("/", Create).Produces<ChannelDto>(StatusCodes.Status201Created);

        var channels = api.MapGroup("/channels/{channelId:guid}").WithTags("Channels");
        channels.MapGet("/", Get).Produces<ChannelDto>();
        channels.MapPatch("/", Update).Produces<ChannelDto>();
        channels.MapPost("/archive", Archive).Produces<ChannelDto>();
        channels.MapGet("/members", ListMembers).Produces<ChannelMemberDto[]>();
        channels.MapPost("/members", AddMember).Produces<ChannelMemberAddedDto>(StatusCodes.Status201Created);
        channels.MapDelete("/members/{userId}", RemoveMember).Produces(StatusCodes.Status204NoContent);
        return api;
    }

    private static async Task<IResult> List(
        Guid workspaceId, bool? includeArchived, CurrentUser user, ModeDbContext db, AccessPolicy policy, CancellationToken ct)
    {
        var role = await policy.GetRoleAsync(user, workspaceId, ct);
        if (role is null) return Problems.NotFound();

        var channels = await db.Channels
            .Where(c => c.WorkspaceId == workspaceId && (includeArchived == true || c.ArchivedAt == null))
            .OrderBy(c => c.NormalizedName)
            .Select(c => new { Channel = c, IsMember = c.Members.Any(m => m.UserId == user.Id) })
            .ToListAsync(ct);

        // Listing shows only channels the caller can read; owners manage hidden private channels elsewhere.
        return TypedResults.Ok(channels
            .Select(c => (c.Channel, Permission: AccessPolicy.ChannelPermissions(
                role, c.Channel.Type, c.Channel.ArchivedAt is not null, c.IsMember, c.Channel.CreatedByUserId == user.Id)))
            .Where(c => c.Permission.Has(Permission.Read))
            .Select(c => ToDto(c.Channel, c.Permission)));
    }

    private static async Task<IResult> Create(
        Guid workspaceId, CreateChannelRequest body, CurrentUser user, ModeDbContext db, AccessPolicy policy,
        IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var permission = await policy.ForWorkspaceAsync(user, workspaceId, ct);
        if (!permission.Has(Permission.Read)) return Problems.NotFound();
        if (!permission.Has(Permission.CreateChannel)) return Problems.Forbidden();
        if (!Input.TryRequired(body.Name, "name", Limits.ChannelName, out var name, out var error)) return error;
        if (!Input.TryOptional(body.Description, "description", Limits.Description, out var description, out error)) return error;

        var normalized = Normalize(name);
        if (await db.Channels.AnyAsync(c => c.WorkspaceId == workspaceId && c.NormalizedName == normalized, ct))
            return Problems.Conflict("A channel with this name already exists in the workspace.");

        var now = clock.GetUtcNow();
        var channel = new Channel
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = name, NormalizedName = normalized, Description = description,
            Type = body.Type ?? ChannelType.Standard, CreatedByUserId = user.Id, CreatedAt = now,
        };
        if (channel.Type == ChannelType.Private)
            channel.Members.Add(new ChannelMember { UserId = user.Id, AddedByUserId = user.Id, AddedAt = now });
        db.Channels.Add(channel);
        audit.Record(AuditActions.ChannelCreated, "channel", channel.Id.ToString(), workspaceId, new { name, channel.Type });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (Problems.IsUniqueViolation(ex))
        {
            return Problems.Conflict("A channel with this name already exists in the workspace.");
        }

        return TypedResults.Created($"/api/v1/channels/{channel.Id}", ToDto(channel, await policy.ForChannelAsync(user, channel.Id, ct)));
    }

    private static async Task<IResult> Get(Guid channelId, CurrentUser user, ModeDbContext db, AccessPolicy policy, CancellationToken ct)
    {
        var permission = await policy.ForChannelAsync(user, channelId, ct);
        if (permission == Permission.None) return Problems.NotFound();

        return TypedResults.Ok(ToDto(await db.Channels.SingleAsync(c => c.Id == channelId, ct), permission));
    }

    private static async Task<IResult> Update(
        Guid channelId, UpdateChannelRequest body, CurrentUser user, ModeDbContext db, AccessPolicy policy, IAuditLog audit, CancellationToken ct)
    {
        var permission = await policy.ForChannelAsync(user, channelId, ct);
        if (permission == Permission.None) return Problems.NotFound();
        if (!permission.Has(Permission.Manage)) return Problems.Forbidden();

        var channel = await db.Channels.SingleAsync(c => c.Id == channelId, ct);
        if (channel.ArchivedAt is not null) return Problems.Conflict("Archived channels are read-only.");

        if (body.Name is not null)
        {
            if (!Input.TryRequired(body.Name, "name", Limits.ChannelName, out var name, out var error)) return error;
            var normalized = Normalize(name);
            if (normalized != channel.NormalizedName &&
                await db.Channels.AnyAsync(c => c.WorkspaceId == channel.WorkspaceId && c.NormalizedName == normalized, ct))
                return Problems.Conflict("A channel with this name already exists in the workspace.");
            channel.Name = name;
            channel.NormalizedName = normalized;
        }
        if (body.Description is not null)
        {
            if (!Input.TryOptional(body.Description, "description", Limits.Description, out var description, out var error)) return error;
            channel.Description = description;
        }

        audit.Record(AuditActions.ChannelUpdated, "channel", channelId.ToString(), channel.WorkspaceId, new { channel.Name });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (Problems.IsUniqueViolation(ex))
        {
            return Problems.Conflict("A channel with this name already exists in the workspace.");
        }

        return TypedResults.Ok(ToDto(channel, permission));
    }

    private static async Task<IResult> Archive(
        Guid channelId, CurrentUser user, ModeDbContext db, AccessPolicy policy, IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var permission = await policy.ForChannelAsync(user, channelId, ct);
        if (permission == Permission.None) return Problems.NotFound();
        if (!permission.Has(Permission.Manage)) return Problems.Forbidden();

        var channel = await db.Channels.SingleAsync(c => c.Id == channelId, ct);
        if (channel.ArchivedAt is null)
        {
            channel.ArchivedAt = clock.GetUtcNow();
            audit.Record(AuditActions.ChannelArchived, "channel", channelId.ToString(), channel.WorkspaceId);
            await db.SaveChangesAsync(ct);
        }

        return TypedResults.Ok(ToDto(channel, permission & ~Permission.Post));
    }

    private static async Task<IResult> ListMembers(
        Guid channelId, CurrentUser user, ModeDbContext db, AccessPolicy policy, IUserDirectory directory, CancellationToken ct)
    {
        var permission = await policy.ForChannelAsync(user, channelId, ct);
        if (permission == Permission.None) return Problems.NotFound();
        if (!permission.Has(Permission.ViewMembers)) return Problems.Forbidden();

        var channel = await db.Channels.SingleAsync(c => c.Id == channelId, ct);
        var explicitMembers = db.ChannelMembers.Where(cm => cm.ChannelId == channelId).Select(cm => cm.UserId);
        var members = await db.WorkspaceMembers
            .Where(m => m.WorkspaceId == channel.WorkspaceId &&
                (explicitMembers.Contains(m.UserId) || (channel.Type == ChannelType.Standard && m.Role != WorkspaceRole.Guest)))
            .Select(m => new { m.UserId, m.Role })
            .ToListAsync(ct);

        var users = await directory.GetManyAsync(members.Select(m => m.UserId), ct);
        return TypedResults.Ok(members
            .Select(m => new ChannelMemberDto(WorkspaceEndpoints.ToUserDto(users, m.UserId), m.Role))
            .OrderBy(m => m.User.DisplayName, StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<IResult> AddMember(
        Guid channelId, AddChannelMemberRequest body, CurrentUser user, ModeDbContext db, AccessPolicy policy,
        IUserDirectory directory, IAuditLog audit, NotificationService notifications, TimeProvider clock, CancellationToken ct)
    {
        var permission = await policy.ForChannelAsync(user, channelId, ct);
        if (permission == Permission.None) return Problems.NotFound();
        if (!permission.Has(Permission.Manage)) return Problems.Forbidden();

        var channel = await db.Channels.SingleAsync(c => c.Id == channelId, ct);
        if (channel.ArchivedAt is not null) return Problems.Conflict("Archived channels are read-only.");

        if (!directory.TryNormalizeId(body.UserId, out var targetId) ||
            !await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == channel.WorkspaceId && m.UserId == targetId, ct))
            return Problems.Invalid("userId", "The user must be a member of the workspace first.");

        if (await db.ChannelMembers.AnyAsync(cm => cm.ChannelId == channelId && cm.UserId == targetId, ct))
            return Problems.Conflict("User is already a member of this channel.");

        db.ChannelMembers.Add(new ChannelMember { ChannelId = channelId, UserId = targetId, AddedByUserId = user.Id, AddedAt = clock.GetUtcNow() });
        audit.Record(AuditActions.ChannelMemberAdded, "user", targetId, channel.WorkspaceId, new { channelId });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (Problems.IsUniqueViolation(ex))
        {
            return Problems.Conflict("User is already a member of this channel.");
        }
        // After the membership is saved, so the notification's reader check sees the new member.
        var activity = await notifications.PrepareAsync(
            [new(targetId, ActivityType.AddedToChannel, user.Id, channel.WorkspaceId, channelId, null, null)], ct);
        await db.SaveChangesAsync(ct);
        await notifications.DeliverAsync(activity, ct);

        return TypedResults.Created($"/api/v1/channels/{channelId}/members/{targetId}", new ChannelMemberAddedDto(targetId));
    }

    private static async Task<IResult> RemoveMember(
        Guid channelId, string userId, CurrentUser user, ModeDbContext db, AccessPolicy policy,
        IUserDirectory directory, IAuditLog audit, AccessRevoker revoker, CancellationToken ct)
    {
        var permission = await policy.ForChannelAsync(user, channelId, ct);
        if (permission == Permission.None) return Problems.NotFound();
        if (!directory.TryNormalizeId(userId, out var targetId)) return Problems.NotFound();
        if (targetId != user.Id && !permission.Has(Permission.Manage)) return Problems.Forbidden();

        var member = await db.ChannelMembers.SingleOrDefaultAsync(cm => cm.ChannelId == channelId && cm.UserId == targetId, ct);
        if (member is null) return Problems.NotFound();

        var workspaceId = await db.Channels.Where(c => c.Id == channelId).Select(c => c.WorkspaceId).SingleAsync(ct);
        db.ChannelMembers.Remove(member);
        audit.Record(AuditActions.ChannelMemberRemoved, "user", targetId, workspaceId, new { channelId });
        await db.SaveChangesAsync(ct);
        await revoker.ReevaluateAsync(targetId, ct);
        return TypedResults.NoContent();
    }

    private static string Normalize(string name) => name.Trim().ToLowerInvariant();

    private static ChannelDto ToDto(Channel channel, Permission permission) => new(
        channel.Id, channel.WorkspaceId, channel.Name, channel.Description, channel.Type, channel.ArchivedAt is not null,
        channel.CreatedAt, permission.Has(Permission.Post), permission.Has(Permission.Manage));
}
