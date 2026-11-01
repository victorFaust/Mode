using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mode.Api.Auth;
using Mode.Api.Authorization;
using Mode.Api.Notifications;
using Mode.Api.Realtime;
using Mode.Domain.Directory;
using Mode.Domain.Entities;
using Mode.Infrastructure.Data;

namespace Mode.Api.Endpoints;

public sealed record ActivityFeedItemDto(
    Guid Id, ActivityType Type, UserDto Actor, Guid? WorkspaceId, Guid? ChannelId, Guid? ConversationId, Guid? MessageId,
    string Title, string Body, DateTimeOffset CreatedAt, bool IsRead);

/// <summary>Newest first; pass <see cref="NextBefore"/> as <c>before</c> for older items.</summary>
public sealed record ActivityPage(IReadOnlyList<ActivityFeedItemDto> Items, int UnreadCount, DateTimeOffset? NextBefore);

public sealed record UnreadCountDto(int UnreadCount);

public sealed record PushKeys(string? P256dh, string? Auth);

public sealed record PushSubscriptionRequest(string? Endpoint, PushKeys? Keys);

public sealed record PushConfigDto(bool Enabled, string? PublicKey);

public static class ActivityEndpoints
{
    public const int MaxPageSize = 100;
    private const int MaxEndpointLength = 1000;
    private const int MaxSubscriptionsPerUser = 20;

    public static RouteGroupBuilder MapActivityEndpoints(this RouteGroupBuilder api)
    {
        var activity = api.MapGroup("/activity").WithTags("Activity");
        activity.MapGet("/", Feed);
        activity.MapGet("/unread-count", UnreadCount);
        activity.MapPost("/{activityId:guid}/read", MarkRead);
        activity.MapPost("/read-all", MarkAllRead);

        api.MapGet("/me/notification-settings", GetSettings).WithTags("Notifications");
        api.MapPut("/me/notification-settings", PutSettings).WithTags("Notifications");

        var push = api.MapGroup("/push").WithTags("Notifications");
        push.MapGet("/public-key", PushConfig);
        push.MapPost("/subscriptions", Subscribe);
        push.MapDelete("/subscriptions", Unsubscribe);
        return api;
    }

    private static async Task<IQueryable<ActivityItem>> VisibleAsync(
        CurrentUser user, ModeDbContext db, AccessPolicy policy, TimeProvider clock, CancellationToken ct)
    {
        var rooms = await policy.ReadableRoomsAsync(user, ct);
        var now = clock.GetUtcNow();
        return db.ActivityItems.Where(a => a.UserId == user.Id && a.ExpiresAt > now).Where(ActivityVisibility.VisibleIn(rooms));
    }

    private static async Task<IResult> Feed(
        string? type, bool? unreadOnly, DateTimeOffset? before, int? limit, CurrentUser user, ModeDbContext db, AccessPolicy policy,
        NotificationContent content, IUserDirectory directory, TimeProvider clock, CancellationToken ct)
    {
        var types = new List<ActivityType>();
        foreach (var part in (type ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<ActivityType>(part, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                return Problems.Invalid("type", $"Unknown activity type '{part}'.");
            types.Add(parsed);
        }

        var visible = await VisibleAsync(user, db, policy, clock, ct);
        var query = visible;
        if (types.Count > 0) query = query.Where(a => types.Contains(a.Type));
        if (unreadOnly == true) query = query.Where(a => a.ReadAt == null);
        if (before is { } cutoff) query = query.Where(a => a.CreatedAt < cutoff);

        var take = Math.Clamp(limit ?? 30, 1, MaxPageSize);
        var items = await query.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id).Take(take + 1).AsNoTracking().ToListAsync(ct);
        var hasMore = items.Count > take;
        items = items.Take(take).ToList();

        var texts = await content.BuildAsync(items, ct);
        var actors = await directory.GetManyAsync(items.Select(i => i.ActorUserId).Distinct(), ct);
        var unread = await visible.CountAsync(a => a.ReadAt == null, ct);
        return TypedResults.Ok(new ActivityPage(
            items.Select(i => new ActivityFeedItemDto(
                i.Id, i.Type, WorkspaceEndpoints.ToUserDto(actors, i.ActorUserId), i.WorkspaceId, i.ChannelId, i.ConversationId, i.MessageId,
                texts[i.Id].Title, texts[i.Id].Body, i.CreatedAt, i.ReadAt is not null)).ToList(),
            unread,
            hasMore ? items[^1].CreatedAt : null));
    }

    private static async Task<IResult> UnreadCount(CurrentUser user, ModeDbContext db, AccessPolicy policy, TimeProvider clock, CancellationToken ct)
    {
        var visible = await VisibleAsync(user, db, policy, clock, ct);
        return TypedResults.Ok(new UnreadCountDto(await visible.CountAsync(a => a.ReadAt == null, ct)));
    }

    private static async Task<IResult> MarkRead(
        Guid activityId, CurrentUser user, ModeDbContext db, AccessPolicy policy, RealtimeNotifier notifier, TimeProvider clock, CancellationToken ct)
    {
        var visible = await VisibleAsync(user, db, policy, clock, ct);
        var item = await db.ActivityItems.SingleOrDefaultAsync(a => a.Id == activityId && a.UserId == user.Id, ct);
        if (item is null || !await visible.AnyAsync(a => a.Id == activityId, ct)) return Problems.NotFound();

        if (item.ReadAt is null)
        {
            item.ReadAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        var unread = await visible.CountAsync(a => a.ReadAt == null, ct);
        await notifier.ActivityReadAsync(user.Id, [activityId], unread);
        return TypedResults.Ok(new UnreadCountDto(unread));
    }

    private static async Task<IResult> MarkAllRead(
        CurrentUser user, ModeDbContext db, AccessPolicy policy, RealtimeNotifier notifier, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        await db.ActivityItems.Where(a => a.UserId == user.Id && a.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(a => a.ReadAt, now), ct);
        await notifier.ActivityReadAsync(user.Id, [], 0);
        return TypedResults.Ok(new UnreadCountDto(0));
    }

    private static async Task<IResult> GetSettings(CurrentUser user, PreferencesStore preferences, CancellationToken ct) =>
        TypedResults.Ok(await preferences.GetAsync(user.Id, ct));

    private static async Task<IResult> PutSettings(NotificationSettings body, CurrentUser user, PreferencesStore preferences, CancellationToken ct)
    {
        if (!Enum.IsDefined(body.Mentions) || !Enum.IsDefined(body.Chats) || !Enum.IsDefined(body.Channels) || !Enum.IsDefined(body.EmailDigest))
            return Problems.Invalid("settings", "Unknown setting value.");
        await preferences.SaveAsync(user.Id, body, ct);
        return TypedResults.Ok(body);
    }

    private static IResult PushConfig(IOptions<PushOptions> options) =>
        TypedResults.Ok(new PushConfigDto(options.Value.IsConfigured, options.Value.IsConfigured ? options.Value.PublicKey : null));

    private static async Task<IResult> Subscribe(
        PushSubscriptionRequest body, CurrentUser user, ModeDbContext db, IOptions<PushOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var push = options.Value;
        if (!push.IsConfigured) return Problems.Conflict("Push notifications are not configured on this server.");
        if (body.Endpoint is not { Length: > 0 and <= MaxEndpointLength } endpoint || !push.IsAllowedEndpoint(endpoint))
            return Problems.Invalid("endpoint", "Not a supported browser push service.");
        if (!IsKey(body.Keys?.P256dh, 65) || !IsKey(body.Keys?.Auth, 16))
            return Problems.Invalid("keys", "Missing or malformed p256dh/auth keys.");

        // The same browser re-subscribing (or a shared machine switching users) replaces the old row.
        var existing = await db.PushSubscriptions.SingleOrDefaultAsync(s => s.Endpoint == endpoint, ct);
        if (existing is not null) db.PushSubscriptions.Remove(existing);
        else if (await db.PushSubscriptions.CountAsync(s => s.UserId == user.Id, ct) >= MaxSubscriptionsPerUser)
            return Problems.Conflict($"At most {MaxSubscriptionsPerUser} devices can receive push notifications.");

        db.PushSubscriptions.Add(new PushSubscription
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Endpoint = endpoint,
            P256dh = body.Keys!.P256dh!,
            Auth = body.Keys.Auth!,
            CreatedAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> Unsubscribe(string? endpoint, CurrentUser user, ModeDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(endpoint)) return Problems.Invalid("endpoint", "Required.");
        await db.PushSubscriptions.Where(s => s.UserId == user.Id && s.Endpoint == endpoint).ExecuteDeleteAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>Base64url without padding, decoding to exactly <paramref name="length"/> bytes.</summary>
    private static bool IsKey(string? value, int length)
    {
        if (value is null || value.Length > 128) return false;
        try
        {
            return Microsoft.AspNetCore.WebUtilities.Base64UrlTextEncoder.Decode(value).Length == length;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
