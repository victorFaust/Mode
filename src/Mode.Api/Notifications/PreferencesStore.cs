using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mode.Domain.Entities;
using Mode.Infrastructure.Data;

namespace Mode.Api.Notifications;

public enum DeliveryMode { BannerAndFeed, FeedOnly, Off }

public enum DigestFrequency { Off, Hourly, Daily }

public enum NotificationCategory { Mentions, Chats, Channels }

public sealed record NotificationSettings
{
    public DeliveryMode Mentions { get; init; } = DeliveryMode.BannerAndFeed;
    public DeliveryMode Chats { get; init; } = DeliveryMode.BannerAndFeed;
    public DeliveryMode Channels { get; init; } = DeliveryMode.FeedOnly;
    public bool PushEnabled { get; init; } = true;
    public DigestFrequency EmailDigest { get; init; } = DigestFrequency.Off;

    public DeliveryMode For(NotificationCategory category) => category switch
    {
        NotificationCategory.Mentions => Mentions,
        NotificationCategory.Chats => Chats,
        _ => Channels,
    };
}

/// <summary>Reads and writes the "notifications" object inside UserSettings.PreferencesJson, leaving other keys untouched.</summary>
public sealed class PreferencesStore(ModeDbContext db, TimeProvider clock)
{
    private const string Key = "notifications";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<NotificationSettings> GetAsync(string userId, CancellationToken ct) =>
        (await GetManyAsync([userId], ct))[userId];

    /// <summary>Every requested user gets an entry; missing settings fall back to the defaults.</summary>
    public async Task<IReadOnlyDictionary<string, NotificationSettings>> GetManyAsync(IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        var stored = await db.UserSettings.AsNoTracking()
            .Where(s => userIds.Contains(s.UserId))
            .ToDictionaryAsync(s => s.UserId, s => s.PreferencesJson, ct);
        return userIds.Distinct().ToDictionary(id => id, id => Parse(stored.GetValueOrDefault(id)));
    }

    public async Task<IReadOnlyList<(string UserId, NotificationSettings Settings)>> WithDigestAsync(CancellationToken ct)
    {
        var rows = await db.UserSettings
            .FromSql($"""SELECT * FROM "UserSettings" WHERE "PreferencesJson" -> 'notifications' ->> 'emailDigest' IN ('Hourly', 'Daily')""")
            .AsNoTracking()
            .Select(s => new { s.UserId, s.PreferencesJson })
            .ToListAsync(ct);
        return rows.Select(r => (r.UserId, Parse(r.PreferencesJson)))
            .Where(r => r.Item2.EmailDigest != DigestFrequency.Off)
            .ToList();
    }

    public async Task SaveAsync(string userId, NotificationSettings settings, CancellationToken ct)
    {
        var row = await db.UserSettings.SingleOrDefaultAsync(s => s.UserId == userId, ct);
        if (row is null)
        {
            row = new UserSettings { UserId = userId };
            db.UserSettings.Add(row);
        }

        var root = (JsonNode.Parse(row.PreferencesJson) as JsonObject) ?? [];
        root[Key] = JsonSerializer.SerializeToNode(settings, Json);
        row.PreferencesJson = root.ToJsonString();
        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    private static NotificationSettings Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new NotificationSettings();
        try
        {
            return JsonNode.Parse(json)?[Key]?.Deserialize<NotificationSettings>(Json) ?? new NotificationSettings();
        }
        catch (JsonException)
        {
            return new NotificationSettings();
        }
    }
}
