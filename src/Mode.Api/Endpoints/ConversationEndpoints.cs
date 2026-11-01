using Microsoft.EntityFrameworkCore;
using Mode.Api.Auth;
using Mode.Api.Authorization;
using Mode.Api.Messaging;
using Mode.Api.Realtime;
using Mode.Domain.Directory;
using Mode.Domain.Entities;
using Mode.Infrastructure.Data;

namespace Mode.Api.Endpoints;

public static class ConversationEndpoints
{
    public const int MaxGroupMembers = 250;
    public const int MaxNameLength = 100;

    public static RouteGroupBuilder MapConversationEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/conversations").WithTags("Conversations");
        group.MapGet("/", ListMine).Produces<ConversationDto[]>();
        group.MapPost("/", Create).Produces<ConversationDto>(StatusCodes.Status201Created).Produces<ConversationDto>();
        group.MapGet("/{conversationId:guid}", Get).Produces<ConversationDto>();
        group.MapPatch("/{conversationId:guid}", Rename).Produces<ConversationDto>();
        group.MapPost("/{conversationId:guid}/members", AddMember).Produces<UserDto>(StatusCodes.Status201Created);
        group.MapDelete("/{conversationId:guid}/members/{userId}", RemoveMember).Produces(StatusCodes.Status204NoContent);
        return api;
    }

    private static async Task<IResult> ListMine(CurrentUser user, ModeDbContext db, IUserDirectory directory, CancellationToken ct)
    {
        var conversations = await db.Conversations
            .Where(c => c.Members.Any(m => m.UserId == user.Id))
            .Include(c => c.Members)
            .AsNoTracking()
            .ToListAsync(ct);
        var ids = conversations.Select(c => (Guid?)c.Id).ToList();
        var latest = await db.Messages
            .Where(m => ids.Contains(m.ConversationId))
            .GroupBy(m => m.ConversationId)
            .Select(g => new { Id = g.Key!.Value, At = g.Max(m => m.CreatedAt) })
            .ToDictionaryAsync(x => x.Id, x => x.At, ct);

        var dtos = await ToDtosAsync(conversations, directory, ct);
        return TypedResults.Ok(dtos.OrderByDescending(c => latest.GetValueOrDefault(c.Id, c.CreatedAt)));
    }

    private static async Task<IResult> Get(
        Guid conversationId, CurrentUser user, ModeDbContext db, AccessPolicy policy, IUserDirectory directory, CancellationToken ct)
    {
        if (!(await policy.ForConversationAsync(user, conversationId, ct)).Has(Permission.Read)) return Problems.NotFound();
        var conversation = await db.Conversations.Include(c => c.Members).AsNoTracking().SingleAsync(c => c.Id == conversationId, ct);
        return TypedResults.Ok((await ToDtosAsync([conversation], directory, ct))[0]);
    }

    private static async Task<IResult> Create(
        CreateConversationRequest body, CurrentUser user, ModeDbContext db, AccessPolicy policy, IUserDirectory directory,
        TimeProvider clock, CancellationToken ct)
    {
        var type = body.Type ?? (body.UserIds is null ? ConversationType.Direct : ConversationType.Group);
        IEnumerable<string?> requested = type == ConversationType.Direct ? [body.UserId] : body.UserIds ?? Enumerable.Empty<string?>();

        var others = new List<string>();
        foreach (var raw in requested)
        {
            if (!directory.TryNormalizeId(raw, out var id)) return Problems.Invalid("userIds", "Every user id must be valid.");
            if (id != user.Id && !others.Contains(id)) others.Add(id);
        }

        if (type == ConversationType.Direct && others.Count != 1) return Problems.Invalid("userId", "Choose one other person.");
        if (type == ConversationType.Group && others.Count < 2) return Problems.Invalid("userIds", "A group chat needs at least 3 people.");
        if (others.Count + 1 > MaxGroupMembers) return Problems.Invalid("userIds", $"A group chat can have at most {MaxGroupMembers} people.");
        if (!Input.TryOptional(body.Name, "name", MaxNameLength, out var name, out var error)) return error;

        var found = await directory.GetManyAsync(others, ct);
        if (others.Any(id => !found.TryGetValue(id, out var u) || !u.IsActive)) return Problems.Invalid("userIds", "Every person must be an active user.");
        if (!await policy.CanStartConversationWithAsync(user, others, ct)) return Problems.Forbidden("Guests can only chat with people in their workspaces.");

        var directKey = type == ConversationType.Direct ? DirectKey(user.Id, others[0]) : null;
        if (directKey is not null)
        {
            var existingId = await db.Conversations.Where(c => c.DirectKey == directKey).Select(c => (Guid?)c.Id).SingleOrDefaultAsync(ct);
            if (existingId is { } id) return await ExistingAsync(id);
        }

        var now = clock.GetUtcNow();
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(), Type = type, Name = type == ConversationType.Group ? name : null, DirectKey = directKey,
            CreatedByUserId = user.Id, CreatedAt = now,
        };
        foreach (var memberId in others.Prepend(user.Id))
            conversation.Members.Add(new ConversationMember { UserId = memberId, AddedAt = now });
        db.Conversations.Add(conversation);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (directKey is not null && Problems.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return await ExistingAsync(await db.Conversations.Where(c => c.DirectKey == directKey).Select(c => c.Id).SingleAsync(ct));
        }

        return TypedResults.Created($"/api/v1/conversations/{conversation.Id}", (await ToDtosAsync([conversation], directory, ct))[0]);

        async Task<IResult> ExistingAsync(Guid id)
        {
            var existing = await db.Conversations.Include(c => c.Members).AsNoTracking().SingleAsync(c => c.Id == id, ct);
            return TypedResults.Ok((await ToDtosAsync([existing], directory, ct))[0]);
        }
    }

    private static async Task<IResult> Rename(
        Guid conversationId, RenameConversationRequest body, CurrentUser user, ModeDbContext db, AccessPolicy policy,
        IUserDirectory directory, CancellationToken ct)
    {
        var permission = await policy.ForConversationAsync(user, conversationId, ct);
        if (!permission.Has(Permission.Read)) return Problems.NotFound();
        if (!permission.Has(Permission.Manage)) return Problems.Forbidden("Only group chats can be renamed.");
        if (!Input.TryOptional(body.Name, "name", MaxNameLength, out var name, out var error)) return error;

        var conversation = await db.Conversations.Include(c => c.Members).SingleAsync(c => c.Id == conversationId, ct);
        conversation.Name = name;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok((await ToDtosAsync([conversation], directory, ct))[0]);
    }

    private static async Task<IResult> AddMember(
        Guid conversationId, AddConversationMemberRequest body, CurrentUser user, ModeDbContext db, AccessPolicy policy,
        IUserDirectory directory, TimeProvider clock, CancellationToken ct)
    {
        var permission = await policy.ForConversationAsync(user, conversationId, ct);
        if (!permission.Has(Permission.Read)) return Problems.NotFound();
        if (!permission.Has(Permission.Manage)) return Problems.Forbidden("People can only be added to group chats.");

        var target = body.UserId is null ? null : await directory.GetByIdAsync(body.UserId, ct);
        if (target is null || !target.IsActive) return Problems.Invalid("userId", "No active user with this id.");
        if (!await policy.CanStartConversationWithAsync(user, [target.Id], ct)) return Problems.Forbidden("Guests can only chat with people in their workspaces.");
        if (await db.ConversationMembers.AnyAsync(m => m.ConversationId == conversationId && m.UserId == target.Id, ct))
            return Problems.Conflict("Already in this chat.");
        if (await db.ConversationMembers.CountAsync(m => m.ConversationId == conversationId, ct) >= MaxGroupMembers)
            return Problems.Conflict($"A group chat can have at most {MaxGroupMembers} people.");

        db.ConversationMembers.Add(new ConversationMember { ConversationId = conversationId, UserId = target.Id, AddedAt = clock.GetUtcNow() });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (Problems.IsUniqueViolation(ex))
        {
            return Problems.Conflict("Already in this chat.");
        }
        return TypedResults.Created($"/api/v1/conversations/{conversationId}/members/{target.Id}", UserDto.From(target));
    }

    private static async Task<IResult> RemoveMember(
        Guid conversationId, string userId, CurrentUser user, ModeDbContext db, AccessPolicy policy, IUserDirectory directory,
        AccessRevoker revoker, CancellationToken ct)
    {
        var permission = await policy.ForConversationAsync(user, conversationId, ct);
        if (!permission.Has(Permission.Read)) return Problems.NotFound();
        if (!directory.TryNormalizeId(userId, out var targetId)) return Problems.NotFound();

        var conversation = await db.Conversations.SingleAsync(c => c.Id == conversationId, ct);
        if (conversation.Type == ConversationType.Direct) return Problems.Forbidden("People cannot leave a direct chat.");
        // Anyone may leave; only the person who started the group may remove others.
        if (targetId != user.Id && conversation.CreatedByUserId != user.Id) return Problems.Forbidden();

        var member = await db.ConversationMembers.SingleOrDefaultAsync(m => m.ConversationId == conversationId && m.UserId == targetId, ct);
        if (member is null) return Problems.NotFound();

        db.ConversationMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        await revoker.ReevaluateAsync(targetId, ct);
        return TypedResults.NoContent();
    }

    internal static string DirectKey(string a, string b) =>
        string.CompareOrdinal(a, b) < 0 ? $"{a}|{b}" : $"{b}|{a}";

    private static async Task<IReadOnlyList<ConversationDto>> ToDtosAsync(
        IReadOnlyList<Conversation> conversations, IUserDirectory directory, CancellationToken ct)
    {
        var users = await directory.GetManyAsync(conversations.SelectMany(c => c.Members.Select(m => m.UserId)).Distinct(), ct);
        return conversations.Select(c => new ConversationDto(
            c.Id, c.Type, c.Name,
            c.Members.OrderBy(m => m.AddedAt).Select(m => WorkspaceEndpoints.ToUserDto(users, m.UserId)).ToList(),
            c.LastSequence, c.CreatedAt)).ToList();
    }
}
