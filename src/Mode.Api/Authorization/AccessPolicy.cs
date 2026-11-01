using Microsoft.EntityFrameworkCore;
using Mode.Api.Auth;
using Mode.Api.Messaging;
using Mode.Domain.Entities;
using Mode.Infrastructure.Data;

namespace Mode.Api.Authorization;

[Flags]
public enum Permission
{
    None = 0,
    Read = 1,
    Post = 2,
    Manage = 4,
    CreateChannel = 8,
    ViewMembers = 16,
    Edit = 32,
    Delete = 64,
    Restore = 128,
}

public sealed record ReadableRooms(IReadOnlyList<Guid> WorkspaceIds, IReadOnlyList<Guid> ChannelIds, IReadOnlyList<Guid> ConversationIds);

public static class ActivityVisibility
{
    /// <summary>Feed items stay hidden once the user can no longer read the room they came from.</summary>
    public static System.Linq.Expressions.Expression<Func<ActivityItem, bool>> VisibleIn(ReadableRooms rooms)
    {
        var workspaceIds = rooms.WorkspaceIds;
        var channelIds = rooms.ChannelIds;
        var conversationIds = rooms.ConversationIds;
        return a => a.ChannelId != null ? channelIds.Contains(a.ChannelId.Value)
            : a.ConversationId != null ? conversationIds.Contains(a.ConversationId.Value)
            : a.WorkspaceId == null || workspaceIds.Contains(a.WorkspaceId.Value);
    }
}

public static class PermissionExtensions
{
    public static bool Has(this Permission granted, Permission required) => (granted & required) == required;
}

/// <summary>
/// The single place that decides what a user may do. Endpoints and hub methods ask it and never check roles themselves.
/// Workspace membership is required for everything inside a workspace; host-flagged guests are always treated as guests.
/// </summary>
public sealed class AccessPolicy(ModeDbContext db)
{
    public bool CanCreateWorkspace(CurrentUser user) => !user.IsHostGuest;

    /// <summary>Guests may not browse the people directory.</summary>
    public async Task<bool> CanUseDirectoryAsync(CurrentUser user, CancellationToken ct)
    {
        if (user.IsHostGuest) return false;
        var roles = await db.WorkspaceMembers.Where(m => m.UserId == user.Id).Select(m => m.Role).Distinct().ToListAsync(ct);
        return roles.Count == 0 || roles.Any(r => r != WorkspaceRole.Guest);
    }

    public async Task<WorkspaceRole?> GetRoleAsync(CurrentUser user, Guid workspaceId, CancellationToken ct)
    {
        var role = await db.WorkspaceMembers
            .Where(m => m.WorkspaceId == workspaceId && m.UserId == user.Id)
            .Select(m => (WorkspaceRole?)m.Role)
            .SingleOrDefaultAsync(ct);
        return EffectiveRole(role, user);
    }

    public async Task<Permission> ForWorkspaceAsync(CurrentUser user, Guid workspaceId, CancellationToken ct) =>
        WorkspacePermissions(await GetRoleAsync(user, workspaceId, ct));

    public async Task<Permission> ForChannelAsync(CurrentUser user, Guid channelId, CancellationToken ct)
    {
        var row = await db.Channels
            .Where(c => c.Id == channelId)
            .Select(c => new
            {
                c.Type,
                Archived = c.ArchivedAt != null,
                IsCreator = c.CreatedByUserId == user.Id,
                Role = db.WorkspaceMembers
                    .Where(m => m.WorkspaceId == c.WorkspaceId && m.UserId == user.Id)
                    .Select(m => (WorkspaceRole?)m.Role)
                    .FirstOrDefault(),
                IsMember = c.Members.Any(m => m.UserId == user.Id),
            })
            .SingleOrDefaultAsync(ct);

        return row is null
            ? Permission.None
            : ChannelPermissions(EffectiveRole(row.Role, user), row.Type, row.Archived, row.IsMember, row.IsCreator);
    }

    public async Task<Permission> ForConversationAsync(CurrentUser user, Guid conversationId, CancellationToken ct)
    {
        var row = await db.Conversations
            .Where(c => c.Id == conversationId)
            .Select(c => new { c.Type, IsMember = c.Members.Any(m => m.UserId == user.Id) })
            .SingleOrDefaultAsync(ct);

        if (row is null || !row.IsMember) return Permission.None;
        var permission = Permission.Read | Permission.Post | Permission.ViewMembers;
        return row.Type == ConversationType.Group ? permission | Permission.Manage : permission;
    }

    public async Task<Permission> ForMessageAsync(CurrentUser user, Guid messageId, CancellationToken ct)
    {
        var message = await db.Messages
            .Where(m => m.Id == messageId)
            .Select(m => new { m.ChannelId, m.ConversationId, m.AuthorUserId, Deleted = m.DeletedAt != null })
            .SingleOrDefaultAsync(ct);
        if (message is null) return Permission.None;

        var room = message.ChannelId is { } channelId
            ? await ForChannelAsync(user, channelId, ct)
            : await ForConversationAsync(user, message.ConversationId!.Value, ct);
        return MessagePermissions(room, message.ChannelId is not null, message.AuthorUserId == user.Id, message.Deleted);
    }

    public async Task<Permission> ForFileAsync(CurrentUser user, Guid fileId, CancellationToken ct)
    {
        var file = await db.FileAssets
            .Where(f => f.Id == fileId)
            .Select(f => new { f.ChannelId, f.ConversationId, f.CreatedByUserId, Deleted = f.DeletedAt != null })
            .SingleOrDefaultAsync(ct);
        if (file is null) return Permission.None;

        var room = file.ChannelId is { } channelId
            ? await ForChannelAsync(user, channelId, ct)
            : await ForConversationAsync(user, file.ConversationId!.Value, ct);
        return FilePermissions(room, file.ChannelId is not null, file.CreatedByUserId == user.Id, file.Deleted);
    }

    public Task<Permission> ForRoomAsync(CurrentUser user, RoomRef room, CancellationToken ct) => room.Type == RoomType.Channel
        ? ForChannelAsync(user, room.Id, ct)
        : ForConversationAsync(user, room.Id, ct);

    /// <summary>
    /// Everyone who can read the room, judged by stored roles. Used to address notifications; the host guest flag is
    /// only known from a user's own token, so reads are still checked per request.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListReadersAsync(RoomRef room, CancellationToken ct)
    {
        if (room.Type == RoomType.Conversation)
            return await db.ConversationMembers.Where(m => m.ConversationId == room.Id).Select(m => m.UserId).ToListAsync(ct);

        var channel = await db.Channels
            .Where(c => c.Id == room.Id)
            .Select(c => new { c.Type, c.WorkspaceId, Archived = c.ArchivedAt != null, c.CreatedByUserId })
            .SingleAsync(ct);
        var explicitMembers = (await db.ChannelMembers.Where(m => m.ChannelId == room.Id).Select(m => m.UserId).ToListAsync(ct)).ToHashSet();
        var workspaceMembers = await db.WorkspaceMembers
            .Where(m => m.WorkspaceId == channel.WorkspaceId)
            .Select(m => new { m.UserId, m.Role })
            .ToListAsync(ct);

        return workspaceMembers
            .Where(m => ChannelPermissions(m.Role, channel.Type, channel.Archived, explicitMembers.Contains(m.UserId), m.UserId == channel.CreatedByUserId)
                .Has(Permission.Read))
            .Select(m => m.UserId)
            .ToList();
    }

    /// <summary>
    /// Every workspace, channel and conversation the user can read, for filtering lists (feed, search, unread counts)
    /// in one query instead of one check per row.
    /// </summary>
    public async Task<ReadableRooms> ReadableRoomsAsync(CurrentUser user, CancellationToken ct)
    {
        var roles = (await db.WorkspaceMembers.Where(m => m.UserId == user.Id).Select(m => new { m.WorkspaceId, m.Role }).ToListAsync(ct))
            .ToDictionary(m => m.WorkspaceId, m => EffectiveRole(m.Role, user));
        var workspaceIds = roles.Keys.ToList();

        var channels = await db.Channels
            .Where(c => workspaceIds.Contains(c.WorkspaceId))
            .Select(c => new
            {
                c.Id,
                c.WorkspaceId,
                c.Type,
                Archived = c.ArchivedAt != null,
                IsCreator = c.CreatedByUserId == user.Id,
                IsMember = c.Members.Any(m => m.UserId == user.Id),
            })
            .ToListAsync(ct);
        var channelIds = channels
            .Where(c => ChannelPermissions(roles[c.WorkspaceId], c.Type, c.Archived, c.IsMember, c.IsCreator).Has(Permission.Read))
            .Select(c => c.Id)
            .ToList();

        var conversationIds = await db.ConversationMembers.Where(m => m.UserId == user.Id).Select(m => m.ConversationId).ToListAsync(ct);
        return new ReadableRooms(workspaceIds, channelIds, conversationIds);
    }

    /// <summary>Presence is visible for yourself and for people you share a workspace or conversation with.</summary>
    public async Task<IReadOnlySet<string>> VisiblePresenceAsync(CurrentUser user, IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        var myWorkspaces = db.WorkspaceMembers.Where(m => m.UserId == user.Id).Select(m => m.WorkspaceId);
        var myConversations = db.ConversationMembers.Where(m => m.UserId == user.Id).Select(m => m.ConversationId);
        var visible = new HashSet<string>(await db.WorkspaceMembers
            .Where(m => userIds.Contains(m.UserId) && myWorkspaces.Contains(m.WorkspaceId))
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync(ct));
        visible.UnionWith(await db.ConversationMembers
            .Where(m => userIds.Contains(m.UserId) && myConversations.Contains(m.ConversationId))
            .Select(m => m.UserId)
            .Distinct()
            .ToListAsync(ct));
        if (userIds.Contains(user.Id)) visible.Add(user.Id);
        return visible;
    }

    /// <summary>People who may use the directory can chat with anyone; guests only with people they share a workspace with.</summary>
    public async Task<bool> CanStartConversationWithAsync(CurrentUser user, IReadOnlyCollection<string> otherUserIds, CancellationToken ct)
    {
        if (await CanUseDirectoryAsync(user, ct)) return true;

        var myWorkspaces = db.WorkspaceMembers.Where(m => m.UserId == user.Id).Select(m => m.WorkspaceId);
        var reachable = await db.WorkspaceMembers
            .Where(m => otherUserIds.Contains(m.UserId) && myWorkspaces.Contains(m.WorkspaceId))
            .Select(m => m.UserId)
            .Distinct()
            .CountAsync(ct);
        return reachable == otherUserIds.Count;
    }

    internal static WorkspaceRole? EffectiveRole(WorkspaceRole? stored, CurrentUser user) =>
        stored is not null && user.IsHostGuest ? WorkspaceRole.Guest : stored;

    internal static Permission WorkspacePermissions(WorkspaceRole? role) => role switch
    {
        WorkspaceRole.Owner => Permission.Read | Permission.Manage | Permission.CreateChannel | Permission.ViewMembers,
        WorkspaceRole.Member => Permission.Read | Permission.CreateChannel | Permission.ViewMembers,
        WorkspaceRole.Guest => Permission.Read,
        _ => Permission.None,
    };

    internal static Permission ChannelPermissions(WorkspaceRole? role, ChannelType type, bool archived, bool isChannelMember, bool isCreator)
    {
        if (role is null) return Permission.None;

        // Private: members only. Standard: every owner and member; guests only when explicitly added.
        var canRead = type == ChannelType.Private ? isChannelMember : role != WorkspaceRole.Guest || isChannelMember;
        var canManage = role == WorkspaceRole.Owner || (isCreator && role != WorkspaceRole.Guest);

        var permission = Permission.None;
        if (canRead) permission |= Permission.Read | Permission.ViewMembers;
        if (canRead && !archived) permission |= Permission.Post;
        if (canManage) permission |= Permission.Manage | Permission.ViewMembers;
        return permission;
    }

    internal static Permission MessagePermissions(Permission room, bool inChannel, bool isAuthor, bool deleted)
    {
        if (!room.Has(Permission.Read)) return Permission.None;

        var permission = Permission.Read;
        if (room.Has(Permission.Post)) permission |= Permission.Post;
        if (!deleted && isAuthor && room.Has(Permission.Post)) permission |= Permission.Edit | Permission.Delete;
        if (!deleted && inChannel && room.Has(Permission.Manage)) permission |= Permission.Delete;
        return permission;
    }

    /// <summary>A deleted file is invisible except to people who could have deleted it, who may restore it.</summary>
    internal static Permission FilePermissions(Permission room, bool inChannel, bool isUploader, bool deleted)
    {
        if (!room.Has(Permission.Read)) return Permission.None;

        var canDelete = (isUploader && room.Has(Permission.Post)) || (inChannel && room.Has(Permission.Manage));
        if (deleted) return canDelete ? Permission.Restore : Permission.None;
        return canDelete ? Permission.Read | Permission.Delete : Permission.Read;
    }
}
