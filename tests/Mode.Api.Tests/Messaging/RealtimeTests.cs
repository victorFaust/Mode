using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mode.Api.Realtime;
using Mode.Api.Tests.Infrastructure;

namespace Mode.Api.Tests.Messaging;

[Collection(ApiCollection.Name)]
public sealed class RealtimeTests(ApiFixture api)
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(2);

    private async Task<(HttpClient Owner, string OwnerId, string MemberId, Guid WorkspaceId, Guid GeneralId)> CreateTeamAsync()
    {
        var ownerId = api.NewUser("Owner");
        var memberId = api.NewUser("Member");
        var owner = api.ClientFor(ownerId);
        var workspaceId = await ApiFixture.CreateWorkspaceAsync(owner);
        await ApiFixture.AddMemberAsync(owner, workspaceId, memberId, "Member");
        var general = (await owner.GetJsonAsync($"/api/v1/workspaces/{workspaceId}/channels"))[0].GetProperty("id").GetGuid();
        return (owner, ownerId, memberId, workspaceId, general);
    }

    private static async Task<JsonElement> SendAsync(HttpClient client, Guid channelId, string body)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/channels/{channelId}/messages", new { id = Guid.NewGuid(), bodyMarkdown = body });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Predicate<JsonElement> Body(string text) =>
        e => e.GetProperty("message").GetProperty("bodyMarkdown").GetString() == text;

    [Fact]
    public async Task Joined_members_receive_new_messages_and_non_members_are_refused()
    {
        var (owner, _, memberId, workspaceId, generalId) = await CreateTeamAsync();
        var privateId = await ApiFixture.CreateChannelAsync(owner, workspaceId, "owners-only", "Private");
        await using var member = await RealtimeClient.ConnectAsync(api, memberId);

        var general = await member.JoinAsync("Channel", generalId);
        var denied = await member.JoinAsync("Channel", privateId);
        var sent = await SendAsync(owner, generalId, "hello realtime");
        await SendAsync(owner, privateId, "not for you");

        Assert.True(general.GetProperty("granted").GetBoolean());
        Assert.False(denied.GetProperty("granted").GetBoolean());
        var received = await member.WaitForAsync(RealtimeEvents.MessageCreated, Body("hello realtime"));
        Assert.Equal(sent.GetProperty("sequence").GetInt64(), received.GetProperty("sequence").GetInt64());
        await Task.Delay(QuietPeriod);
        Assert.DoesNotContain(member.Events(RealtimeEvents.MessageCreated), Body("not for you"));
    }

    [Fact]
    public async Task Reconnecting_replays_everything_missed_since_the_last_sequence()
    {
        var (owner, _, memberId, _, generalId) = await CreateTeamAsync();
        using var memberHttp = api.ClientFor(memberId);
        long lastSeen;
        Guid editedId;

        await using (var first = await RealtimeClient.ConnectAsync(api, memberId))
        {
            await first.JoinAsync("Channel", generalId);
            var seen = await SendAsync(owner, generalId, "seen live");
            editedId = seen.GetProperty("id").GetGuid();
            lastSeen = (await first.WaitForAsync(RealtimeEvents.MessageCreated, Body("seen live"))).GetProperty("sequence").GetInt64();
            await first.StopAsync();
        }

        var missed = await SendAsync(owner, generalId, "sent while offline");
        var deleted = await SendAsync(owner, generalId, "deleted while offline");
        await owner.PatchAsJsonAsync($"/api/v1/messages/{editedId}", new { bodyMarkdown = "edited while offline" });
        await memberHttp.PutAsync($"/api/v1/messages/{missed.GetProperty("id").GetGuid()}/reactions/ok", null);
        await owner.DeleteAsync($"/api/v1/messages/{deleted.GetProperty("id").GetGuid()}");

        await using var second = await RealtimeClient.ConnectAsync(api, memberId);
        var join = await second.JoinAsync("Channel", generalId, lastSeen);

        Assert.False(join.GetProperty("resyncRequired").GetBoolean());
        var replay = join.GetProperty("missed").EnumerateArray().ToList();
        Assert.Equal(3, replay.Count);
        var bySequence = replay.Select(m => m.GetProperty("updatedSequence").GetInt64()).ToList();
        Assert.Equal(bySequence.Order(), bySequence);
        Assert.Equal(join.GetProperty("currentSequence").GetInt64(), bySequence[^1]);

        var edited = replay.Single(m => m.GetProperty("id").GetGuid() == editedId);
        Assert.Equal("edited while offline", edited.GetProperty("bodyMarkdown").GetString());
        var reacted = replay.Single(m => m.GetProperty("id").GetGuid() == missed.GetProperty("id").GetGuid());
        Assert.Equal(1, reacted.GetProperty("reactions")[0].GetProperty("count").GetInt32());
        Assert.True(replay.Single(m => m.GetProperty("id").GetGuid() == deleted.GetProperty("id").GetGuid()).GetProperty("isDeleted").GetBoolean());
    }

    [Fact]
    public async Task Removed_channel_member_stops_receiving_events_immediately()
    {
        var (owner, ownerId, memberId, workspaceId, _) = await CreateTeamAsync();
        var privateId = await ApiFixture.CreateChannelAsync(owner, workspaceId, "inner", "Private");
        await owner.PostJsonOkAsync($"/api/v1/channels/{privateId}/members", new { userId = memberId });
        await using var member = await RealtimeClient.ConnectAsync(api, memberId);
        await using var watcher = await RealtimeClient.ConnectAsync(api, ownerId);
        Assert.True((await member.JoinAsync("Channel", privateId)).GetProperty("granted").GetBoolean());
        await watcher.JoinAsync("Channel", privateId);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/channels/{privateId}/members/{memberId}")).StatusCode);
        await SendAsync(owner, privateId, "after removal");

        var revoked = await member.WaitForAsync(RealtimeEvents.RoomAccessRevoked);
        Assert.Equal(privateId, revoked.GetProperty("room").GetProperty("id").GetGuid());
        await watcher.WaitForAsync(RealtimeEvents.MessageCreated, Body("after removal"));
        await Task.Delay(QuietPeriod);
        Assert.DoesNotContain(member.Events(RealtimeEvents.MessageCreated), Body("after removal"));
    }

    [Fact]
    public async Task Removed_workspace_member_stops_receiving_events_immediately()
    {
        var (owner, ownerId, memberId, workspaceId, generalId) = await CreateTeamAsync();
        await using var member = await RealtimeClient.ConnectAsync(api, memberId);
        await using var watcher = await RealtimeClient.ConnectAsync(api, ownerId);
        await member.JoinAsync("Channel", generalId);
        await watcher.JoinAsync("Channel", generalId);

        await owner.DeleteAsync($"/api/v1/workspaces/{workspaceId}/members/{memberId}");
        await SendAsync(owner, generalId, "members only now");

        await member.WaitForAsync(RealtimeEvents.RoomAccessRevoked);
        await watcher.WaitForAsync(RealtimeEvents.MessageCreated, Body("members only now"));
        await Task.Delay(QuietPeriod);
        Assert.DoesNotContain(member.Events(RealtimeEvents.MessageCreated), Body("members only now"));
    }

    [Fact]
    public async Task Typing_reaches_others_once_per_window()
    {
        var (_, ownerId, memberId, _, generalId) = await CreateTeamAsync();
        await using var typist = await RealtimeClient.ConnectAsync(api, memberId);
        await using var watcher = await RealtimeClient.ConnectAsync(api, ownerId);
        await typist.JoinAsync("Channel", generalId);
        await watcher.JoinAsync("Channel", generalId);

        await typist.TypingAsync("Channel", generalId);
        await typist.TypingAsync("Channel", generalId);

        var typing = await watcher.WaitForAsync(RealtimeEvents.Typing);
        Assert.Equal(memberId, typing.GetProperty("userId").GetString());
        await Task.Delay(QuietPeriod);
        Assert.Single(watcher.Events(RealtimeEvents.Typing));
        Assert.Empty(typist.Events(RealtimeEvents.Typing));
    }

    [Fact]
    public async Task Mentioned_people_get_an_activity_event()
    {
        var (owner, _, memberId, _, generalId) = await CreateTeamAsync();
        await using var member = await RealtimeClient.ConnectAsync(api, memberId);

        var message = await SendAsync(owner, generalId, $"@{{{memberId}}} can you check?");

        var activity = await member.WaitForAsync(RealtimeEvents.ActivityCreated);
        Assert.Equal("Mention", activity.GetProperty("item").GetProperty("type").GetString());
        Assert.Equal(message.GetProperty("id").GetGuid(), activity.GetProperty("item").GetProperty("messageId").GetGuid());
    }
}
