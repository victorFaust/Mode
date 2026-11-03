using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mode.Api.Auditing;
using Mode.Api.Auth;
using Mode.Api.Authorization;
using Mode.Api.Endpoints;
using Mode.Api.Jobs;
using Mode.Api.Realtime;
using Mode.Domain.Directory;
using Mode.Domain.Entities;
using Mode.Domain.Files;
using Mode.Infrastructure.Data;

namespace Mode.Api.Governance;

public sealed record AdminWorkspaceDto(
    Guid Id, string Name, string? Description, DateTimeOffset CreatedAt, int MemberCount, int ChannelCount, int? RetentionDays);

public sealed record RetentionRequest(int? KeepDays);

public sealed record AuditEntryDto(
    long Id, DateTimeOffset At, string? ActorUserId, string? ActorName, string Action, string TargetType, string? TargetId,
    Guid? WorkspaceId, JsonElement? Data, string? IpAddress);

public sealed record AuditPage(IReadOnlyList<AuditEntryDto> Items, long? NextBefore);

public sealed record LegalHoldDto(
    Guid Id, HoldTarget TargetType, string TargetId, string TargetName, string Reason, UserDto CreatedBy, DateTimeOffset CreatedAt,
    DateTimeOffset? ReleasedAt);

public sealed record CreateHoldRequest(HoldTarget? TargetType, string? TargetId, string? Reason);

public sealed record DlpSettingsDto(IReadOnlyList<string> BlockedExtensions, IReadOnlyList<DlpPattern> Patterns, DateTimeOffset? UpdatedAt);

public sealed record UpdateDlpRequest(IReadOnlyList<string>? BlockedExtensions, IReadOnlyList<DlpPattern>? Patterns);

public sealed record WebhookDto(
    Guid Id, string Name, Guid? ConversationId, string RoomName, string Url, DateTimeOffset CreatedAt, DateTimeOffset? RotatedAt,
    DateTimeOffset? RevokedAt, DateTimeOffset? LastUsedAt);

/// <summary>The only time the secret is shown; Mode keeps just its hash.</summary>
public sealed record WebhookSecretDto(WebhookDto Webhook, string Secret, string Header);

public sealed record CreateWebhookRequest(Guid? ConversationId, string? Name);

public sealed record ExportDto(
    Guid Id, DateTimeOffset CreatedAt, UserDto RequestedBy, ExportFilters Filters, ExportStatus Status, DateTimeOffset? CompletedAt,
    long SizeBytes, int MessageCount, int FileCount, string? ManifestSha256, string? ManifestSignature, string? Error);

/// <summary>Governance for users whose host token carries the admin role. User accounts stay in the host application.</summary>
public static class AdminEndpoints
{
    public const int MaxRetentionDays = 36_500;
    public const int MaxCsvRows = 100_000;
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").WithTags("Admin").AddEndpointFilter(async (context, next) =>
            context.HttpContext.RequestServices.GetRequiredService<CurrentUser>().IsAdmin
                ? await next(context)
                : Problems.Forbidden("Only Mode administrators can do this."));

        admin.MapGet("/workspaces", ListWorkspaces).Produces<AdminWorkspaceDto[]>();
        admin.MapPatch("/workspaces/{workspaceId:guid}", UpdateWorkspace).Produces<AdminWorkspaceDto>();
        admin.MapGet("/workspaces/{workspaceId:guid}/members", ListMembers).Produces<WorkspaceMemberDto[]>();
        admin.MapPatch("/workspaces/{workspaceId:guid}/members/{userId}", ChangeRole).Produces<WorkspaceMemberDto>();
        admin.MapPut("/workspaces/{workspaceId:guid}/retention", SetRetention).Produces<AdminWorkspaceDto>();

        admin.MapGet("/audit", ListAudit).Produces<AuditPage>();
        admin.MapGet("/audit/export", ExportAudit).Produces(StatusCodes.Status200OK, contentType: "text/csv");
        admin.MapPost("/audit/verify", VerifyAudit).Produces<AuditVerifyResult>();

        admin.MapGet("/holds", ListHolds).Produces<LegalHoldDto[]>();
        admin.MapPost("/holds", PlaceHold).Produces<LegalHoldDto>(StatusCodes.Status201Created);
        admin.MapPost("/holds/{holdId:guid}/release", ReleaseHold).Produces<LegalHoldDto>();

        admin.MapGet("/dlp", GetDlp).Produces<DlpSettingsDto>();
        admin.MapPut("/dlp", UpdateDlp).Produces<DlpSettingsDto>();

        admin.MapGet("/webhooks", ListWebhooks).Produces<WebhookDto[]>();
        admin.MapPost("/webhooks", CreateWebhook).Produces<WebhookSecretDto>(StatusCodes.Status201Created);
        admin.MapPost("/webhooks/{webhookId:guid}/rotate", RotateWebhook).Produces<WebhookSecretDto>();
        admin.MapDelete("/webhooks/{webhookId:guid}", RevokeWebhook).Produces(StatusCodes.Status204NoContent);

        admin.MapGet("/exports", ListExports).Produces<ExportDto[]>();
        admin.MapPost("/exports", RequestExport).RequireRateLimiting(Security.RateLimits.Export).Produces<ExportDto>(StatusCodes.Status202Accepted);
        admin.MapGet("/exports/{exportId:guid}/download", DownloadExport).Produces(StatusCodes.Status200OK, contentType: "application/zip");
        return api;
    }

    // ---- Workspaces -------------------------------------------------------------------------------

    private static async Task<IResult> ListWorkspaces(ModeDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await WorkspaceQuery(db, db.Workspaces.OrderBy(w => w.Name)).ToListAsync(ct));

    private static Task<AdminWorkspaceDto> WorkspaceAsync(ModeDbContext db, Guid id, CancellationToken ct) =>
        WorkspaceQuery(db, db.Workspaces.Where(w => w.Id == id)).SingleAsync(ct);

    private static IQueryable<AdminWorkspaceDto> WorkspaceQuery(ModeDbContext db, IQueryable<Workspace> source) => source.Select(w => new AdminWorkspaceDto(
        w.Id, w.Name, w.Description, w.CreatedAt, w.Members.Count, w.Channels.Count,
        db.RetentionPolicies.Where(p => p.WorkspaceId == w.Id).Select(p => p.KeepDays).FirstOrDefault()));

    private static async Task<IResult> UpdateWorkspace(
        Guid workspaceId, UpdateWorkspaceRequest body, ModeDbContext db, IAuditLog audit, CancellationToken ct)
    {
        var workspace = await db.Workspaces.SingleOrDefaultAsync(w => w.Id == workspaceId, ct);
        if (workspace is null) return Problems.NotFound();
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
        audit.Record(AuditActions.WorkspaceUpdated, "workspace", workspaceId.ToString(), workspaceId, new { workspace.Name, byAdmin = true });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await WorkspaceAsync(db, workspaceId, ct));
    }

    private static async Task<IResult> ListMembers(Guid workspaceId, ModeDbContext db, IUserDirectory directory, CancellationToken ct)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId, ct)) return Problems.NotFound();
        var members = await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspaceId).ToListAsync(ct);
        var users = await directory.GetManyAsync(members.Select(m => m.UserId), ct);
        return TypedResults.Ok(members
            .Select(m => new WorkspaceMemberDto(WorkspaceEndpoints.ToUserDto(users, m.UserId), m.Role, m.AddedAt))
            .OrderBy(m => m.User.DisplayName, StringComparer.OrdinalIgnoreCase));
    }

    private static async Task<IResult> ChangeRole(
        Guid workspaceId, string userId, ChangeRoleRequest body, ModeDbContext db, IUserDirectory directory, IAuditLog audit,
        AccessRevoker revoker, CancellationToken ct)
    {
        if (body.Role is null) return Problems.Invalid("role", "Required.");
        if (!directory.TryNormalizeId(userId, out var targetId)) return Problems.NotFound();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await WorkspaceEndpoints.LockWorkspaceAsync(db, workspaceId, ct);
        var member = await db.WorkspaceMembers.SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == targetId, ct);
        if (member is null) return Problems.NotFound();
        if (member.Role == WorkspaceRole.Owner && body.Role != WorkspaceRole.Owner && await WorkspaceEndpoints.IsLastOwnerAsync(db, workspaceId, ct))
            return Problems.Conflict("A workspace must keep at least one owner.");

        var previous = member.Role;
        member.Role = body.Role.Value;
        audit.Record(AuditActions.MemberRoleChanged, "user", targetId, workspaceId, new { from = previous, to = member.Role, byAdmin = true });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await revoker.ReevaluateAsync(targetId, ct);

        var users = await directory.GetManyAsync([targetId], ct);
        return TypedResults.Ok(new WorkspaceMemberDto(WorkspaceEndpoints.ToUserDto(users, targetId), member.Role, member.AddedAt));
    }

    private static async Task<IResult> SetRetention(
        Guid workspaceId, RetentionRequest body, CurrentUser user, ModeDbContext db, IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId, ct)) return Problems.NotFound();
        if (body.KeepDays is < 1 or > MaxRetentionDays)
            return Problems.Invalid("keepDays", $"Use a number of days from 1 to {MaxRetentionDays}, or leave it empty to keep forever.");

        var policy = await db.RetentionPolicies.SingleOrDefaultAsync(p => p.WorkspaceId == workspaceId, ct);
        var previous = policy?.KeepDays;
        if (policy is null)
        {
            policy = new RetentionPolicy { WorkspaceId = workspaceId, UpdatedByUserId = user.Id };
            db.RetentionPolicies.Add(policy);
        }
        policy.KeepDays = body.KeepDays;
        policy.UpdatedByUserId = user.Id;
        policy.UpdatedAt = clock.GetUtcNow();
        audit.Record(AuditActions.RetentionChanged, "workspace", workspaceId.ToString(), workspaceId, new { from = previous, to = body.KeepDays });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await WorkspaceAsync(db, workspaceId, ct));
    }

    // ---- Audit log --------------------------------------------------------------------------------

    private static IQueryable<AuditLog> FilterAudit(
        ModeDbContext db, DateTimeOffset? from, DateTimeOffset? to, string? actor, string? action, Guid? workspaceId)
    {
        var query = db.AuditLogs.AsNoTracking();
        if (from is { } start) query = query.Where(a => a.At >= start);
        if (to is { } end) query = query.Where(a => a.At < end);
        if (!string.IsNullOrWhiteSpace(actor)) query = query.Where(a => a.ActorUserId == actor.Trim());
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(a => a.Action.StartsWith(action.Trim()));
        if (workspaceId is { } id) query = query.Where(a => a.WorkspaceId == id);
        return query;
    }

    private static async Task<IResult> ListAudit(
        DateTimeOffset? from, DateTimeOffset? to, string? actor, string? action, Guid? workspaceId, long? before, int? limit,
        ModeDbContext db, IUserDirectory directory, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 50, 1, 200);
        var query = FilterAudit(db, from, to, actor, action, workspaceId);
        if (before is { } cursor) query = query.Where(a => a.Id < cursor);
        var rows = await query.OrderByDescending(a => a.Id).Take(take + 1).ToListAsync(ct);
        var page = rows.Take(take).ToList();
        var names = await directory.GetManyAsync(page.Where(a => a.ActorUserId is not null).Select(a => a.ActorUserId!).Distinct(), ct);
        return TypedResults.Ok(new AuditPage(page.Select(a => ToDto(a, names)).ToList(), rows.Count > take ? page[^1].Id : null));
    }

    private static async Task<IResult> ExportAudit(
        DateTimeOffset? from, DateTimeOffset? to, string? actor, string? action, Guid? workspaceId,
        ModeDbContext db, IUserDirectory directory, IAuditLog audit, CancellationToken ct)
    {
        var rows = await FilterAudit(db, from, to, actor, action, workspaceId)
            .OrderBy(a => a.Id).Take(MaxCsvRows + 1).ToListAsync(ct);
        if (rows.Count > MaxCsvRows) return Problems.Invalid("from", $"More than {MaxCsvRows} entries match; narrow the dates.");
        var names = await directory.GetManyAsync(rows.Where(a => a.ActorUserId is not null).Select(a => a.ActorUserId!).Distinct(), ct);

        var csv = new StringBuilder("Id,At,ActorUserId,ActorName,Action,TargetType,TargetId,WorkspaceId,IpAddress,Data\r\n");
        foreach (var a in rows)
        {
            var dto = ToDto(a, names);
            csv.AppendJoin(',', new[]
            {
                a.Id.ToString(CultureInfo.InvariantCulture), a.At.ToString("O", CultureInfo.InvariantCulture), a.ActorUserId, dto.ActorName,
                a.Action, a.TargetType, a.TargetId, a.WorkspaceId?.ToString(), a.IpAddress, a.DataJson,
            }.Select(CsvField)).Append("\r\n");
        }

        audit.Record(AuditActions.AuditExported, "audit", null, workspaceId, new { from, to, actor, action, rows = rows.Count });
        await db.SaveChangesAsync(ct);
        return TypedResults.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(),
            "text/csv", $"mode-audit-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
    }

    /// <summary>Seals anything due, then re-computes the whole chain.</summary>
    private static async Task<IResult> VerifyAudit(AuditSealer sealer, ModeDbContext db, IAuditLog audit, CancellationToken ct)
    {
        await sealer.SealAsync(ct);
        var result = await sealer.VerifyAsync(ct);
        audit.Record(AuditActions.AuditVerified, "audit", null, data: result);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(result);
    }

    /// <summary>Quotes every field and neutralizes spreadsheet formulas (CSV injection).</summary>
    internal static string CsvField(string? value)
    {
        value ??= "";
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static AuditEntryDto ToDto(AuditLog a, IReadOnlyDictionary<string, DirectoryUser> names)
    {
        var actorName = a.ActorUserId is { } actor && names.TryGetValue(actor, out var person) ? person.DisplayName : a.ActorUserId;
        JsonElement? data = a.DataJson is null ? null : JsonSerializer.Deserialize<JsonElement>(a.DataJson);
        return new AuditEntryDto(a.Id, a.At, a.ActorUserId, actorName, a.Action, a.TargetType, a.TargetId, a.WorkspaceId, data, a.IpAddress);
    }

    // ---- Legal holds ------------------------------------------------------------------------------

    private static async Task<IResult> ListHolds(bool? active, ModeDbContext db, IUserDirectory directory, CancellationToken ct)
    {
        var query = db.LegalHolds.AsNoTracking();
        if (active == true) query = query.Where(h => h.ReleasedAt == null);
        var holds = await query.OrderByDescending(h => h.CreatedAt).ToListAsync(ct);
        return TypedResults.Ok(await ToDtosAsync(holds, db, directory, ct));
    }

    private static async Task<IResult> PlaceHold(
        CreateHoldRequest body, CurrentUser user, ModeDbContext db, IUserDirectory directory, IAuditLog audit, TimeProvider clock,
        CancellationToken ct)
    {
        if (body.TargetType is not { } type) return Problems.Invalid("targetType", "Required.");
        if (!Input.TryRequired(body.Reason, "reason", 500, out var reason, out var error)) return error;

        string? targetId = null;
        switch (type)
        {
            case HoldTarget.Channel when Guid.TryParse(body.TargetId, out var channelId) && await db.Channels.AnyAsync(c => c.Id == channelId, ct):
                targetId = channelId.ToString();
                break;
            case HoldTarget.Conversation when Guid.TryParse(body.TargetId, out var conversationId)
                && await db.Conversations.AnyAsync(c => c.Id == conversationId, ct):
                targetId = conversationId.ToString();
                break;
            case HoldTarget.User when directory.TryNormalizeId(body.TargetId, out var userId) && await directory.GetByIdAsync(userId, ct) is not null:
                targetId = userId;
                break;
        }
        if (targetId is null) return Problems.Invalid("targetId", "No such channel, chat or person.");
        if (await db.LegalHolds.AnyAsync(h => h.TargetType == type && h.TargetId == targetId && h.ReleasedAt == null, ct))
            return Problems.Conflict("This is already on hold.");

        var hold = new LegalHold
        {
            Id = Guid.NewGuid(), TargetType = type, TargetId = targetId, Reason = reason, CreatedByUserId = user.Id, CreatedAt = clock.GetUtcNow(),
        };
        db.LegalHolds.Add(hold);
        audit.Record(AuditActions.HoldPlaced, type.ToString().ToLowerInvariant(), targetId, data: new { hold.Id, reason });
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/admin/holds/{hold.Id}", (await ToDtosAsync([hold], db, directory, ct))[0]);
    }

    private static async Task<IResult> ReleaseHold(
        Guid holdId, CurrentUser user, ModeDbContext db, IUserDirectory directory, IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var hold = await db.LegalHolds.SingleOrDefaultAsync(h => h.Id == holdId, ct);
        if (hold is null) return Problems.NotFound();
        if (hold.ReleasedAt is null)
        {
            hold.ReleasedAt = clock.GetUtcNow();
            hold.ReleasedByUserId = user.Id;
            audit.Record(AuditActions.HoldReleased, hold.TargetType.ToString().ToLowerInvariant(), hold.TargetId, data: new { hold.Id });
            await db.SaveChangesAsync(ct);
        }
        return TypedResults.Ok((await ToDtosAsync([hold], db, directory, ct))[0]);
    }

    private static async Task<IReadOnlyList<LegalHoldDto>> ToDtosAsync(
        IReadOnlyList<LegalHold> holds, ModeDbContext db, IUserDirectory directory, CancellationToken ct)
    {
        var roomIds = holds.Where(h => h.TargetType != HoldTarget.User).Select(h => Guid.Parse(h.TargetId)).ToList();
        var channels = await db.Channels.Where(c => roomIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => "#" + c.Name, ct);
        var chats = await db.Conversations.Where(c => roomIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name ?? $"Chat with {c.Members.Count} people", ct);
        var people = await directory.GetManyAsync(
            holds.Where(h => h.TargetType == HoldTarget.User).Select(h => h.TargetId).Concat(holds.Select(h => h.CreatedByUserId)).Distinct(), ct);

        return holds.Select(h => new LegalHoldDto(
            h.Id, h.TargetType, h.TargetId,
            h.TargetType switch
            {
                HoldTarget.Channel => channels.GetValueOrDefault(Guid.Parse(h.TargetId)) ?? "Deleted channel",
                HoldTarget.Conversation => chats.GetValueOrDefault(Guid.Parse(h.TargetId)) ?? "Deleted chat",
                _ => people.TryGetValue(h.TargetId, out var p) ? p.DisplayName : h.TargetId,
            },
            h.Reason, WorkspaceEndpoints.ToUserDto(people, h.CreatedByUserId), h.CreatedAt, h.ReleasedAt)).ToList();
    }

    // ---- Data-loss controls -----------------------------------------------------------------------

    private static async Task<IResult> GetDlp(DlpService dlp, CancellationToken ct)
    {
        var settings = await dlp.GetAsync(ct);
        return TypedResults.Ok(new DlpSettingsDto(settings.BlockedExtensions, settings.Patterns, settings.UpdatedByUserId is null ? null : settings.UpdatedAt));
    }

    private static async Task<IResult> UpdateDlp(
        UpdateDlpRequest body, CurrentUser user, ModeDbContext db, IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var extensions = (body.BlockedExtensions ?? []).Select(e => e.Trim().TrimStart('.').ToLowerInvariant()).Where(e => e.Length > 0).Distinct().ToList();
        if (extensions.Any(e => e.Length > 16 || !e.All(char.IsAsciiLetterOrDigit)))
            return Problems.Invalid("blockedExtensions", "Use extensions such as 'zip' (letters and digits).");

        var patterns = body.Patterns ?? [];
        if (patterns.Count > DlpService.MaxPatterns) return Problems.Invalid("patterns", $"At most {DlpService.MaxPatterns} patterns.");
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern.Name) || pattern.Name.Length > 80) return Problems.Invalid("patterns", "Each pattern needs a name of up to 80 characters.");
            if (!DlpService.TryValidate(pattern.Pattern, out var error)) return Problems.Invalid("patterns", $"{pattern.Name}: {error}");
        }

        var settings = await db.DlpSettings.SingleOrDefaultAsync(s => s.Id == DlpSettings.SingletonId, ct);
        if (settings is null) db.DlpSettings.Add(settings = new DlpSettings());
        settings.BlockedExtensions = extensions;
        settings.Patterns = patterns.Select(p => p with { Name = p.Name.Trim() }).ToList();
        settings.UpdatedByUserId = user.Id;
        settings.UpdatedAt = clock.GetUtcNow();
        audit.Record(AuditActions.DlpUpdated, "dlp", null, data: new { blockedExtensions = extensions, patterns = settings.Patterns.Select(p => new { p.Name, p.Action }) });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new DlpSettingsDto(settings.BlockedExtensions, settings.Patterns, settings.UpdatedAt));
    }

    // ---- Webhooks ---------------------------------------------------------------------------------

    private static async Task<IResult> ListWebhooks(ModeDbContext db, CancellationToken ct)
    {
        var hooks = await db.Webhooks.AsNoTracking().OrderByDescending(w => w.CreatedAt).ToListAsync(ct);
        return TypedResults.Ok(await ToDtosAsync(hooks, db, ct));
    }

    /// <summary>The admin must belong to the group chat, so creating a webhook never reveals a chat they can't see.</summary>
    private static async Task<IResult> CreateWebhook(
        CreateWebhookRequest body, CurrentUser user, ModeDbContext db, AccessPolicy policy, IAuditLog audit, TimeProvider clock,
        CancellationToken ct)
    {
        if (body.ConversationId is not { } conversationId) return Problems.Invalid("conversationId", "Required.");
        if (!Input.TryRequired(body.Name, "name", 80, out var name, out var error)) return error;
        if (!(await policy.ForConversationAsync(user, conversationId, ct)).Has(Permission.Manage))
            return Problems.Invalid("conversationId", "Choose a group chat you belong to.");

        var secret = WebhookSecrets.NewSecret();
        var hook = new Webhook
        {
            Id = Guid.NewGuid(), ConversationId = conversationId, Name = name, SecretHash = WebhookSecrets.Hash(secret),
            CreatedByUserId = user.Id, CreatedAt = clock.GetUtcNow(),
        };
        db.Webhooks.Add(hook);
        audit.Record(AuditActions.WebhookCreated, "webhook", hook.Id.ToString(), data: new { name, conversationId });
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/admin/webhooks/{hook.Id}",
            new WebhookSecretDto((await ToDtosAsync([hook], db, ct))[0], secret, WebhookSecrets.Header));
    }

    private static async Task<IResult> RotateWebhook(Guid webhookId, ModeDbContext db, IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var hook = await db.Webhooks.SingleOrDefaultAsync(w => w.Id == webhookId && w.RevokedAt == null, ct);
        if (hook is null) return Problems.NotFound();
        var secret = WebhookSecrets.NewSecret();
        hook.SecretHash = WebhookSecrets.Hash(secret);
        hook.RotatedAt = clock.GetUtcNow();
        audit.Record(AuditActions.WebhookRotated, "webhook", hook.Id.ToString());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new WebhookSecretDto((await ToDtosAsync([hook], db, ct))[0], secret, WebhookSecrets.Header));
    }

    private static async Task<IResult> RevokeWebhook(Guid webhookId, ModeDbContext db, IAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var hook = await db.Webhooks.SingleOrDefaultAsync(w => w.Id == webhookId, ct);
        if (hook is null) return Problems.NotFound();
        if (hook.RevokedAt is null)
        {
            hook.RevokedAt = clock.GetUtcNow();
            audit.Record(AuditActions.WebhookRevoked, "webhook", hook.Id.ToString());
            await db.SaveChangesAsync(ct);
        }
        return TypedResults.NoContent();
    }

    private static async Task<IReadOnlyList<WebhookDto>> ToDtosAsync(IReadOnlyList<Webhook> hooks, ModeDbContext db, CancellationToken ct)
    {
        var ids = hooks.Where(h => h.ConversationId is not null).Select(h => h.ConversationId!.Value).ToList();
        var chats = await db.Conversations.Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name ?? $"Group chat ({c.Members.Count} people)", ct);
        return hooks.Select(h => new WebhookDto(
            h.Id, h.Name, h.ConversationId, h.ConversationId is { } id ? chats.GetValueOrDefault(id) ?? "Deleted chat" : "Channel",
            $"/api/v1/hooks/{h.Id}", h.CreatedAt, h.RotatedAt, h.RevokedAt, h.LastUsedAt)).ToList();
    }

    // ---- Compliance exports -----------------------------------------------------------------------

    private static async Task<IResult> ListExports(ModeDbContext db, IUserDirectory directory, CancellationToken ct)
    {
        var exports = await db.ComplianceExports.AsNoTracking().OrderByDescending(e => e.CreatedAt).Take(100).ToListAsync(ct);
        var people = await directory.GetManyAsync(exports.Select(e => e.RequestedByUserId).Distinct(), ct);
        return TypedResults.Ok(exports.Select(e => ToDto(e, people)));
    }

    private static async Task<IResult> RequestExport(
        ExportFilters body, CurrentUser user, ModeDbContext db, IUserDirectory directory, JobQueue jobs, IAuditLog audit,
        TimeProvider clock, CancellationToken ct)
    {
        string? userId = null;
        if (body.UserId is not null && !directory.TryNormalizeId(body.UserId, out userId))
            return Problems.Invalid("userId", "Not a valid user id.");
        if (userId is null && body.ChannelId is null && body.ConversationId is null && body.From is null)
            return Problems.Invalid("userId", "Choose a person, a channel or chat, or a start date.");
        if (body.From is { } from && body.To is { } to && to <= from) return Problems.Invalid("to", "Must be after the start date.");

        var filters = body with { UserId = userId };
        var export = new ComplianceExport
        {
            Id = Guid.NewGuid(), RequestedByUserId = user.Id, CreatedAt = clock.GetUtcNow(),
            FiltersJson = JsonSerializer.Serialize(filters, Json), Status = ExportStatus.Pending,
        };
        db.ComplianceExports.Add(export);
        jobs.Enqueue(JobTypes.ComplianceExport, new { exportId = export.Id }, maxAttempts: 2);
        audit.Record(AuditActions.ExportRequested, "export", export.Id.ToString(), data: filters);
        await db.SaveChangesAsync(ct);

        var people = await directory.GetManyAsync([user.Id], ct);
        return TypedResults.Accepted($"/api/v1/admin/exports/{export.Id}", ToDto(export, people));
    }

    private static async Task<IResult> DownloadExport(
        Guid exportId, ModeDbContext db, IFileStore store, IAuditLog audit, CancellationToken ct)
    {
        var export = await db.ComplianceExports.SingleOrDefaultAsync(e => e.Id == exportId, ct);
        if (export is null) return Problems.NotFound();
        if (export.Status != ExportStatus.Ready || export.StorageKey is null) return Problems.Conflict("This export is not ready.");
        var content = await store.OpenReadAsync(export.StorageKey, ct);
        if (content is null) return Problems.NotFound();

        audit.Record(AuditActions.ExportDownloaded, "export", export.Id.ToString());
        await db.SaveChangesAsync(ct);
        return TypedResults.File(content, "application/zip", $"mode-export-{export.CreatedAt:yyyyMMdd-HHmm}-{export.Id.ToString()[..8]}.zip");
    }

    private static ExportDto ToDto(ComplianceExport e, IReadOnlyDictionary<string, DirectoryUser> people) => new(
        e.Id, e.CreatedAt, WorkspaceEndpoints.ToUserDto(people, e.RequestedByUserId), JsonSerializer.Deserialize<ExportFilters>(e.FiltersJson, Json)!,
        e.Status, e.CompletedAt, e.SizeBytes, e.MessageCount, e.FileCount, e.ManifestSha256, e.ManifestSignature, e.Error);
}
