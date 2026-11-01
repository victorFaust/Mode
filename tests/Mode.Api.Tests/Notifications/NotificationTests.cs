using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Mode.Api.Jobs;
using Mode.Api.Realtime;
using Mode.Api.Tests.Infrastructure;
using Mode.Domain.Entities;

namespace Mode.Api.Tests.Notifications;

[Collection(ApiCollection.Name)]
public sealed class NotificationTests(ApiFixture api)
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(2);

    private sealed record Team(HttpClient Owner, string OwnerId, HttpClient Member, string MemberId, Guid WorkspaceId, Guid GeneralId);

    private async Task<Team> CreateTeamAsync()
    {
        var ownerId = api.NewUser("Owner");
        var memberId = api.NewUser("Member");
        var owner = api.ClientFor(ownerId);
        var workspaceId = await ApiFixture.CreateWorkspaceAsync(owner);
        await ApiFixture.AddMemberAsync(owner, workspaceId, memberId, "Member");
        var generalId = (await owner.GetJsonAsync($"/api/v1/workspaces/{workspaceId}/channels"))[0].GetProperty("id").GetGuid();
        return new Team(owner, ownerId, api.ClientFor(memberId), memberId, workspaceId, generalId);
    }

    private static async Task<JsonElement> SendAsync(HttpClient client, string room, Guid roomId, string body, Guid? parentId = null)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/{room}/{roomId}/messages",
            new { id = Guid.NewGuid(), bodyMarkdown = body, parentMessageId = parentId });
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<JsonElement> SendToChannelAsync(HttpClient client, Guid channelId, string body, Guid? parentId = null) =>
        SendAsync(client, "channels", channelId, body, parentId);

    private static async Task<int> UnreadAsync(HttpClient client) =>
        (await client.GetJsonAsync("/api/v1/activity/unread-count")).GetProperty("unreadCount").GetInt32();

    /// <summary>Message-related items only; every member also has an "added you to Team" item.</summary>
    private async Task<List<ActivityItem>> ItemsForAsync(string userId)
    {
        await using var db = api.CreateDbContext();
        return await db.ActivityItems.Where(a => a.UserId == userId && a.MessageId != null).OrderBy(a => a.CreatedAt).ToListAsync();
    }

    private static Task<HttpResponseMessage> PutSettingsAsync(HttpClient client, string mentions = "BannerAndFeed", string chats = "BannerAndFeed",
        string channels = "FeedOnly", bool pushEnabled = true, string emailDigest = "Off") =>
        client.PutAsJsonAsync("/api/v1/me/notification-settings", new { mentions, chats, channels, pushEnabled, emailDigest });

    private static async Task SubscribeAsync(HttpClient client, string endpoint) =>
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/push/subscriptions", PushBody(endpoint))).StatusCode);

    private static object PushBody(string endpoint) => new
    {
        endpoint,
        keys = new
        {
            p256dh = Base64UrlTextEncoder.Encode([4, .. new byte[64]]),
            auth = Base64UrlTextEncoder.Encode(new byte[16]),
        },
    };

    private async Task RunJobAsync(string type)
    {
        await using (var db = api.CreateDbContext())
        {
            var now = DateTimeOffset.UtcNow;
            db.Jobs.Add(new Job { Type = type, PayloadJson = "{}", Status = JobStatus.Pending, MaxAttempts = 1, RunAfter = now, CreatedAt = now });
            await db.SaveChangesAsync();
        }
        await api.RunJobsAsync();
    }

    [Fact]
    public async Task Badge_counts_follow_new_and_read_activity()
    {
        var team = await CreateTeamAsync();
        await team.Member.PostJsonOkAsync("/api/v1/activity/read-all", new { });
        await using var realtime = await RealtimeClient.ConnectAsync(api, team.MemberId);

        await SendToChannelAsync(team.Owner, team.GeneralId, $"@{{{team.MemberId}}} please review");
        var created = await realtime.WaitForAsync(RealtimeEvents.ActivityCreated);
        Assert.Equal(1, created.GetProperty("unreadCount").GetInt32());

        var question = await SendToChannelAsync(team.Member, team.GeneralId, "question?");
        await SendToChannelAsync(team.Owner, team.GeneralId, "answer", question.GetProperty("id").GetGuid());
        Assert.Equal(2, await UnreadAsync(team.Member));

        var feed = await team.Member.GetJsonAsync("/api/v1/activity?unreadOnly=true");
        var items = feed.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["Reply", "Mention"], items.Select(i => i.GetProperty("type").GetString()));
        Assert.Equal("Owner mentioned you in #General", items[1].GetProperty("title").GetString());
        Assert.Equal("@Member please review", items[1].GetProperty("body").GetString());
        Assert.Equal(team.OwnerId, items[0].GetProperty("actor").GetProperty("id").GetString());
        Assert.Single((await team.Member.GetJsonAsync("/api/v1/activity?type=Mention")).GetProperty("items").EnumerateArray());

        var afterOne = await team.Member.PostJsonOkAsync($"/api/v1/activity/{items[0].GetProperty("id").GetGuid()}/read", new { });
        Assert.Equal(1, afterOne.GetProperty("unreadCount").GetInt32());
        Assert.Single((await team.Member.GetJsonAsync("/api/v1/activity?unreadOnly=true")).GetProperty("items").EnumerateArray());
        await team.Member.PostJsonOkAsync("/api/v1/activity/read-all", new { });
        Assert.Equal(0, await UnreadAsync(team.Member));
        Assert.Equal(HttpStatusCode.NotFound,
            (await team.Owner.PostAsync($"/api/v1/activity/{items[0].GetProperty("id").GetGuid()}/read", null)).StatusCode);
    }

    [Fact]
    public async Task Muted_channel_only_notifies_direct_mentions()
    {
        var team = await CreateTeamAsync();
        Assert.Equal(HttpStatusCode.OK, (await team.Member.PutAsJsonAsync($"/api/v1/channels/{team.GeneralId}/mute", new { muted = true })).StatusCode);

        await SendToChannelAsync(team.Owner, team.GeneralId, "@channel all hands at 3");
        await SendToChannelAsync(team.Owner, team.GeneralId, $"@{{{team.MemberId}}} you specifically");

        var item = Assert.Single(await ItemsForAsync(team.MemberId));
        Assert.Equal(ActivityType.Mention, item.Type);
    }

    [Fact]
    public async Task Do_not_disturb_keeps_the_feed_but_holds_back_banners_and_push()
    {
        var team = await CreateTeamAsync();
        await SubscribeAsync(team.Member, $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}");
        await team.Member.PostJsonOkAsync("/api/v1/activity/read-all", new { });
        Assert.Equal(HttpStatusCode.OK, (await team.Member.PutAsJsonAsync("/api/v1/me/presence", new { status = "DoNotDisturb" })).StatusCode);

        await using (var realtime = await RealtimeClient.ConnectAsync(api, team.MemberId))
        {
            await SendToChannelAsync(team.Owner, team.GeneralId, $"@{{{team.MemberId}}} while busy");
            await realtime.WaitForAsync(RealtimeEvents.ActivityCreated);
            await Task.Delay(QuietPeriod);
            Assert.Empty(realtime.Events(RealtimeEvents.Notification));
        }

        // Offline but still on DND: no push either.
        await SendToChannelAsync(team.Owner, team.GeneralId, $"@{{{team.MemberId}}} while away from the desk");
        await api.RunJobsAsync();
        Assert.Empty(api.Push.PayloadsFor(team.MemberId));
        Assert.Equal(2, await UnreadAsync(team.Member));

        await team.Member.PutAsJsonAsync("/api/v1/me/presence", new { status = "Available" });
        await using var again = await RealtimeClient.ConnectAsync(api, team.MemberId);
        await SendToChannelAsync(team.Owner, team.GeneralId, $"@{{{team.MemberId}}} back now?");
        var banner = await again.WaitForAsync(RealtimeEvents.Notification);
        Assert.Equal("Owner mentioned you in #General", banner.GetProperty("title").GetString());
        Assert.Equal("@Member back now?", banner.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Delivery_settings_decide_between_banner_feed_and_nothing()
    {
        var team = await CreateTeamAsync();
        Assert.Equal(HttpStatusCode.OK, (await PutSettingsAsync(team.Member, mentions: "Off", chats: "FeedOnly")).StatusCode);
        var saved = await team.Member.GetJsonAsync("/api/v1/me/notification-settings");
        Assert.Equal("Off", saved.GetProperty("mentions").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await team.Member.PutAsJsonAsync("/api/v1/me/notification-settings", new { mentions = "Loud" })).StatusCode);

        await using var realtime = await RealtimeClient.ConnectAsync(api, team.MemberId);
        await SendToChannelAsync(team.Owner, team.GeneralId, $"@{{{team.MemberId}}} muted by settings");
        var chatId = (await team.Owner.PostJsonOkAsync("/api/v1/conversations", new { type = "Direct", userId = team.MemberId })).GetProperty("id").GetGuid();
        await SendAsync(team.Owner, "conversations", chatId, "feed only please");

        var item = await realtime.WaitForAsync(RealtimeEvents.ActivityCreated);
        Assert.Equal("ChatMessage", item.GetProperty("item").GetProperty("type").GetString());
        await Task.Delay(QuietPeriod);
        Assert.Empty(realtime.Events(RealtimeEvents.Notification));
        Assert.Equal([ActivityType.ChatMessage], (await ItemsForAsync(team.MemberId)).Select(i => i.Type));

        await PutSettingsAsync(team.Member, mentions: "Off", chats: "BannerAndFeed");
        await SendAsync(team.Owner, "conversations", chatId, "now with a banner");
        var banner = await realtime.WaitForAsync(RealtimeEvents.Notification);
        Assert.Equal("Owner", banner.GetProperty("title").GetString());
        Assert.Equal("now with a banner", banner.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Push_reaches_offline_users_and_dead_subscriptions_are_removed()
    {
        var team = await CreateTeamAsync();
        var live = $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}";
        var dead = $"https://updates.push.services.mozilla.com/wpush/v2/{Guid.NewGuid():N}";
        api.Push.Gone[dead] = true;
        await SubscribeAsync(team.Member, live);
        await SubscribeAsync(team.Member, dead);

        await SendToChannelAsync(team.Owner, team.GeneralId, $"@{{{team.MemberId}}} ping");
        await api.RunJobsAsync();

        var payload = JsonDocument.Parse(Assert.Single(api.Push.PayloadsFor(team.MemberId))).RootElement;
        Assert.Equal("Owner mentioned you in #General", payload.GetProperty("title").GetString());
        await using var db = api.CreateDbContext();
        Assert.Equal([live], await db.PushSubscriptions.Where(s => s.UserId == team.MemberId).Select(s => s.Endpoint).ToListAsync());

        var config = await team.Member.GetJsonAsync("/api/v1/push/public-key");
        Assert.True(config.GetProperty("enabled").GetBoolean());
        Assert.Equal("test-public-key", config.GetProperty("publicKey").GetString());
    }

    [Theory]
    [InlineData("https://intranet.local/hook")]
    [InlineData("http://fcm.googleapis.com/fcm/send/x")]
    [InlineData("https://fcm.googleapis.com:8443/fcm/send/x")]
    [InlineData("https://fcm.googleapis.com.evil.example/x")]
    [InlineData("https://127.0.0.1/x")]
    public async Task Push_subscriptions_only_accept_known_push_services(string endpoint)
    {
        using var client = api.ClientFor(api.NewUser());
        var response = await client.PostAsJsonAsync("/api/v1/push/subscriptions", PushBody(endpoint));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Activity_expires_after_thirty_days()
    {
        var team = await CreateTeamAsync();
        await SendToChannelAsync(team.Owner, team.GeneralId, $"@{{{team.MemberId}}} old news");
        var item = Assert.Single(await ItemsForAsync(team.MemberId));
        Assert.InRange(item.ExpiresAt - item.CreatedAt, TimeSpan.FromDays(30), TimeSpan.FromDays(30));

        await using (var db = api.CreateDbContext())
            await db.ActivityItems.Where(a => a.Id == item.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));

        Assert.Equal(1, await UnreadAsync(team.Member)); // only "Owner added you to Team" is left
        Assert.DoesNotContain((await team.Member.GetJsonAsync("/api/v1/activity")).GetProperty("items").EnumerateArray(),
            i => i.GetProperty("messageId").ValueKind != JsonValueKind.Null);
        await RunJobAsync(JobTypes.ActivityExpire);
        Assert.Empty(await ItemsForAsync(team.MemberId));
    }

    [Fact]
    public async Task Feed_drops_items_from_rooms_the_user_can_no_longer_read()
    {
        var team = await CreateTeamAsync();
        var privateId = await ApiFixture.CreateChannelAsync(team.Owner, team.WorkspaceId, "inner", "Private");
        await team.Owner.PostJsonOkAsync($"/api/v1/channels/{privateId}/members", new { userId = team.MemberId });
        await team.Member.PostJsonOkAsync("/api/v1/activity/read-all", new { });
        await SendToChannelAsync(team.Owner, privateId, $"@{{{team.MemberId}}} confidential");
        Assert.Equal(1, await UnreadAsync(team.Member));

        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.DeleteAsync($"/api/v1/channels/{privateId}/members/{team.MemberId}")).StatusCode);

        Assert.Equal(0, await UnreadAsync(team.Member));
        var feed = await team.Member.GetJsonAsync("/api/v1/activity");
        Assert.DoesNotContain(feed.GetProperty("items").EnumerateArray(), i => i.GetProperty("channelId").ValueKind != JsonValueKind.Null &&
            i.GetProperty("channelId").GetGuid() == privateId);
    }

    [Fact]
    public async Task Reactions_chats_and_membership_changes_notify()
    {
        var team = await CreateTeamAsync();
        var newcomerId = api.NewUser("Newcomer");
        using var newcomer = api.ClientFor(newcomerId);
        await ApiFixture.AddMemberAsync(team.Owner, team.WorkspaceId, newcomerId, "Member");
        var privateId = await ApiFixture.CreateChannelAsync(team.Owner, team.WorkspaceId, "projects", "Private");
        await team.Owner.PostJsonOkAsync($"/api/v1/channels/{privateId}/members", new { userId = newcomerId });

        var titles = (await newcomer.GetJsonAsync("/api/v1/activity")).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("title").GetString()).ToList();
        Assert.Equal(["Owner added you to #projects", "Owner added you to Team"], titles);

        var message = await SendToChannelAsync(team.Owner, team.GeneralId, "ship it");
        var messageId = message.GetProperty("id").GetGuid();
        await team.Member.PutAsync($"/api/v1/messages/{messageId}/reactions/ok", null);
        await newcomer.PutAsync($"/api/v1/messages/{messageId}/reactions/ok", null);
        await team.Member.DeleteAsync($"/api/v1/messages/{messageId}/reactions/ok");

        var reaction = Assert.Single(await ItemsForAsync(team.OwnerId), i => i.Type == ActivityType.Reaction);
        Assert.Equal(team.MemberId, reaction.ActorUserId);

        var groupId = (await team.Owner.PostJsonOkAsync("/api/v1/conversations",
            new { type = "Group", userIds = new[] { team.MemberId, newcomerId }, name = "Launch" })).GetProperty("id").GetGuid();
        await SendAsync(team.Owner, "conversations", groupId, $"hello both, especially @{{{newcomerId}}}");

        Assert.Equal(ActivityType.ChatMessage, (await ItemsForAsync(team.MemberId)).Single(i => i.ConversationId == groupId).Type);
        Assert.Equal(ActivityType.Mention, (await ItemsForAsync(newcomerId)).Single(i => i.ConversationId == groupId).Type);
        Assert.DoesNotContain(await ItemsForAsync(team.OwnerId), i => i.ConversationId == groupId);
        var chatTitle = (await team.Member.GetJsonAsync("/api/v1/activity")).GetProperty("items")[0].GetProperty("title").GetString();
        Assert.Equal("Owner in Launch", chatTitle);
    }

    [Fact]
    public async Task Email_digest_sends_unread_activity_once()
    {
        var team = await CreateTeamAsync();
        Assert.Equal(HttpStatusCode.OK, (await PutSettingsAsync(team.Member, emailDigest: "Hourly")).StatusCode);
        await SendToChannelAsync(team.Owner, team.GeneralId, $"@{{{team.MemberId}}} see <b>this</b>");
        await SendToChannelAsync(team.Owner, team.GeneralId, $"@{{{team.MemberId}}} too recent to email");

        await using (var db = api.CreateDbContext())
        {
            var first = (await ItemsForAsync(team.MemberId))[0];
            await db.ActivityItems.Where(a => a.Id == first.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.CreatedAt, DateTimeOffset.UtcNow.AddMinutes(-20)));
        }

        await RunJobAsync(JobTypes.EmailDigest);
        await RunJobAsync(JobTypes.EmailDigest);

        var email = Assert.Single(api.Email.Sent, e => e.To == $"{team.MemberId}@test.example");
        Assert.Equal("Owner mentioned you in #General", email.Subject);
        Assert.Contains("@Member see", email.Text);
        Assert.DoesNotContain("<b>this", email.Html);
        Assert.DoesNotContain("too recent", email.Text);
        Assert.Single(await ItemsForAsync(team.MemberId), i => i.EmailedAt is not null);
    }
}
