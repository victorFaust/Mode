using System.Net;
using System.Text.Json;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mode.Api.Jobs;
using Mode.Api.Presence;
using Mode.Infrastructure.Data;
using StoredSubscription = Mode.Domain.Entities.PushSubscription;

namespace Mode.Api.Notifications;

public sealed class PushOptions
{
    public const string SectionName = "Push";

    /// <summary>Contact for push services, e.g. mailto:it@uba.example.</summary>
    public string Subject { get; set; } = "";

    /// <summary>VAPID key pair (base64url P-256), generated once by scripts/dev-setup.ps1.</summary>
    public string PublicKey { get; set; } = "";
    public string PrivateKey { get; set; } = "";

    /// <summary>
    /// Browser push services the server may call. Endpoints come from browsers, so anything else is refused
    /// (prevents using subscriptions to make the server call internal addresses). A leading dot matches subdomains.
    /// </summary>
    public string[] AllowedHosts { get; set; } = [];

    public static readonly string[] DefaultHosts =
        ["fcm.googleapis.com", "updates.push.services.mozilla.com", ".notify.windows.com", "web.push.apple.com", ".push.apple.com"];

    public bool IsConfigured => PublicKey.Length > 0 && PrivateKey.Length > 0 && Subject.Length > 0;

    public bool IsAllowedEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort) return false;
        var hosts = AllowedHosts.Length > 0 ? AllowedHosts : DefaultHosts;
        return hosts.Any(h => h.StartsWith('.')
            ? uri.Host.EndsWith(h, StringComparison.OrdinalIgnoreCase)
            : uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase));
    }
}

public enum PushResult { Sent, Gone }

public interface IPushSender
{
    Task<PushResult> SendAsync(StoredSubscription subscription, string payloadJson, CancellationToken cancellationToken);
}

/// <summary>Web Push with VAPID (RFC 8292) and aes128gcm payload encryption (RFC 8291).</summary>
public sealed class WebPushSender(HttpClient http, IOptions<PushOptions> options) : IPushSender
{
    private readonly PushServiceClient _client = new(http)
    {
        DefaultAuthentication = new VapidAuthentication(options.Value.PublicKey, options.Value.PrivateKey) { Subject = options.Value.Subject },
        DefaultTimeToLive = 3600,
    };

    public async Task<PushResult> SendAsync(StoredSubscription subscription, string payloadJson, CancellationToken cancellationToken)
    {
        var target = new PushSubscription { Endpoint = subscription.Endpoint };
        target.SetKey(PushEncryptionKeyName.P256DH, subscription.P256dh);
        target.SetKey(PushEncryptionKeyName.Auth, subscription.Auth);
        try
        {
            await _client.RequestPushMessageDeliveryAsync(target, new PushMessage(payloadJson) { Urgency = PushMessageUrgency.High }, cancellationToken);
            return PushResult.Sent;
        }
        catch (PushServiceClientException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return PushResult.Gone;
        }
    }
}

public sealed record PushPayload(string Title, string Body, Guid ActivityId, Guid? ChannelId, Guid? ConversationId, Guid? MessageId);

/// <summary>Sends a push only if the person still hasn't seen the item and has no Mode tab open.</summary>
public sealed class PushSendJob(
    ModeDbContext db, PresenceTracker presence, PreferencesStore preferences, NotificationContent content, IPushSender sender, TimeProvider clock)
    : IJobHandler
{
    public string Type => JobTypes.PushSend;

    public async Task HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var id = payload.Deserialize<ActivityJobPayload>(JsonSerializerOptions.Web)!.ActivityItemId;
        var item = await db.ActivityItems.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct);
        if (item is null || item.ReadAt is not null) return;
        if (presence.IsConnected(item.UserId) || presence.IsDoNotDisturb(item.UserId)) return;
        if (!(await preferences.GetAsync(item.UserId, ct)).PushEnabled) return;

        var subscriptions = await db.PushSubscriptions.Where(s => s.UserId == item.UserId).ToListAsync(ct);
        if (subscriptions.Count == 0) return;

        var text = (await content.BuildAsync([item], ct))[item.Id];
        var json = JsonSerializer.Serialize(
            new PushPayload(text.Title, text.Body, item.Id, item.ChannelId, item.ConversationId, item.MessageId), JsonSerializerOptions.Web);

        var failures = 0;
        foreach (var subscription in subscriptions)
        {
            try
            {
                if (await sender.SendAsync(subscription, json, ct) == PushResult.Gone)
                    db.PushSubscriptions.Remove(subscription);
                else
                    subscription.LastUsedAt = clock.GetUtcNow();
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                failures++;
            }
        }
        await db.SaveChangesAsync(ct);
        if (failures == subscriptions.Count) throw new InvalidOperationException("No push service accepted the message.");
    }
}

/// <summary>Deletes feed items past their 30-day expiry.</summary>
public sealed class ActivityExpiryJob(ModeDbContext db, TimeProvider clock) : IJobHandler
{
    public string Type => JobTypes.ActivityExpire;

    public Task HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.ActivityItems.Where(a => a.ExpiresAt <= now).ExecuteDeleteAsync(ct);
    }
}
