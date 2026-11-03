using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Mode.Api.Auth;
using Mode.Api.Authorization;
using Mode.Api.Endpoints;
using Mode.Api.Messaging;
using Mode.Api.Notifications;
using Mode.Api.Realtime;
using Mode.Domain.Entities;
using Mode.Infrastructure.Data;

namespace Mode.Api.Governance;

public static class WebhookSecrets
{
    public const string Header = "X-Mode-Webhook-Key";
    public const string BotPrefix = "bot:";

    public static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public static bool Matches(string? presented, string storedHash) =>
        presented is { Length: > 0 and <= 200 } &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(presented)), Encoding.ASCII.GetBytes(storedHash));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record IncomingWebhookRequest(string? Text, Guid? Id);

public static class IncomingWebhookEndpoints
{
    public const string RateLimitPolicy = "webhooks";

    /// <summary>Anonymous: the secret in <see cref="WebhookSecrets.Header"/> is the credential. Rate-limited per webhook.</summary>
    public static IEndpointRouteBuilder MapIncomingWebhooks(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/hooks/{webhookId:guid}", Post)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy)
            .WithTags("Webhooks")
            .Produces<MessageDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        return app;
    }

    private static async Task<IResult> Post(
        Guid webhookId, IncomingWebhookRequest body, HttpRequest request, ModeDbContext db, AccessPolicy policy, MessageStore store,
        MentionProcessor mentions, NotificationService notifications, RealtimeNotifier notifier, DlpService dlp, TimeProvider clock,
        CancellationToken ct)
    {
        var webhook = await db.Webhooks.SingleOrDefaultAsync(w => w.Id == webhookId && w.RevokedAt == null, ct);
        // One answer for unknown, revoked and wrong-key, so ids can't be probed.
        if (webhook is null || !WebhookSecrets.Matches(request.Headers[WebhookSecrets.Header].ToString(), webhook.SecretHash))
            return TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");

        var room = webhook.ConversationId is { } conversationId
            ? new RoomRef(RoomType.Conversation, conversationId)
            : new RoomRef(RoomType.Channel, webhook.ChannelId!.Value);
        if (room.Type == RoomType.Channel &&
            await db.Channels.AnyAsync(c => c.Id == room.Id && c.ArchivedAt != null, ct))
            return Problems.Forbidden("This channel is archived.");
        if (!MessageContent.TrySanitize(body.Text, out var content, out var error)) return Problems.Invalid("text", error);

        // Bots can't confirm a warning, so only blocking rules apply to them.
        var check = await dlp.CheckAsync(content.Markdown, ct);
        if (check.Blocked.Count > 0) return await Problems.DlpGateAsync(dlp, content.Markdown, true, ct) ?? Problems.Forbidden();

        var bot = new CurrentUser(WebhookSecrets.BotPrefix + webhook.Id, webhook.Name, null, false, false);
        var deps = new MessageEndpoints.MessageDependencies(bot, db, policy, store, mentions, notifications, notifier, clock);
        var draft = new MessageEndpoints.NewMessage(
            body.Id ?? Guid.NewGuid(), room, content.Markdown, content.Mentions, null, [], null, webhook.Name);
        if (await MessageEndpoints.ExistingAsync(draft, deps, ct) is { } existing) return existing;

        webhook.LastUsedAt = clock.GetUtcNow();
        return await MessageEndpoints.InsertAsync(draft, deps, ct);
    }
}
