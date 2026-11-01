using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mode.Api.Realtime;
using Mode.Api.Tests.Infrastructure;

namespace Mode.Api.Tests.Notifications;

[Collection(ApiCollection.Name)]
public sealed class UnreadTests(ApiFixture api)
{
    private async Task<(HttpClient Owner, HttpClient Member, string MemberId, Guid WorkspaceId, Guid GeneralId)> CreateTeamAsync()
    {
        var ownerId = api.NewUser("Owner");
        var memberId = api.NewUser("Member");
        var owner = api.ClientFor(ownerId);
        var workspaceId = await ApiFixture.CreateWorkspaceAsync(owner);
        await ApiFixture.AddMemberAsync(owner, workspaceId, memberId, "Member");
        var generalId = (await owner.GetJsonAsync($"/api/v1/workspaces/{workspaceId}/channels"))[0].GetProperty("id").GetGuid();
        return (owner, api.ClientFor(memberId), memberId, workspaceId, generalId);
    }

    private static async Task<long> SendAsync(HttpClient client, Guid channelId, string body)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/channels/{channelId}/messages", new { id = Guid.NewGuid(), bodyMarkdown = body });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sequence").GetInt64();
    }

    private static async Task<JsonElement?> RoomAsync(HttpClient client, Guid roomId) =>
        (await client.GetJsonAsync("/api/v1/me/unread")).EnumerateArray()
            .Cast<JsonElement?>()
            .SingleOrDefault(r => r!.Value.GetProperty("room").GetProperty("id").GetGuid() == roomId);

    [Fact]
    public async Task Unread_counts_follow_the_read_marker()
    {
        var (owner, member, memberId, _, generalId) = await CreateTeamAsync();
        var first = await SendAsync(owner, generalId, "one");
        var second = await SendAsync(owner, generalId, $"two for @{{{memberId}}}");
        var third = await SendAsync(owner, generalId, "three");
        await SendAsync(member, generalId, "my own message does not count");

        var room = (await RoomAsync(member, generalId))!.Value;
        Assert.Equal(3, room.GetProperty("unreadCount").GetInt32());
        Assert.Equal(1, room.GetProperty("mentionCount").GetInt32());
        Assert.Equal(first, room.GetProperty("firstUnreadSequence").GetInt64());
        Assert.Equal(0, room.GetProperty("lastReadSequence").GetInt64());

        await using var otherTab = await RealtimeClient.ConnectAsync(api, memberId);
        var read = await member.PutAsJsonAsync($"/api/v1/channels/{generalId}/read", new { sequence = second });
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var marker = await otherTab.WaitForAsync(RealtimeEvents.ReadMarkerChanged);
        Assert.Equal(second, marker.GetProperty("lastReadSequence").GetInt64());
        await otherTab.WaitForAsync(RealtimeEvents.ActivityRead);

        room = (await RoomAsync(member, generalId))!.Value;
        Assert.Equal(1, room.GetProperty("unreadCount").GetInt32());
        Assert.Equal(0, room.GetProperty("mentionCount").GetInt32());
        Assert.Equal(third, room.GetProperty("firstUnreadSequence").GetInt64());

        await member.PutAsJsonAsync($"/api/v1/channels/{generalId}/read", new { sequence = first });
        Assert.Equal(second, (await RoomAsync(member, generalId))!.Value.GetProperty("lastReadSequence").GetInt64());

        var page = await member.GetJsonAsync($"/api/v1/channels/{generalId}/messages?after={second}");
        Assert.Equal(["three", "my own message does not count"],
            page.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("bodyMarkdown").GetString()));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextAfter").ValueKind);

        await member.PutAsJsonAsync($"/api/v1/channels/{generalId}/read", new { sequence = long.MaxValue });
        Assert.Null(await RoomAsync(member, generalId));
    }

    [Fact]
    public async Task Rooms_the_user_cannot_read_cannot_be_marked_or_muted()
    {
        var (owner, member, _, workspaceId, _) = await CreateTeamAsync();
        var privateId = await ApiFixture.CreateChannelAsync(owner, workspaceId, "closed", "Private");
        await SendAsync(owner, privateId, "hidden");

        Assert.Equal(HttpStatusCode.NotFound, (await member.PutAsJsonAsync($"/api/v1/channels/{privateId}/read", new { sequence = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.PutAsJsonAsync($"/api/v1/channels/{privateId}/mute", new { muted = true })).StatusCode);
        Assert.Null(await RoomAsync(member, privateId));
    }

    [Fact]
    public async Task Muted_rooms_are_flagged_and_chats_have_unread_counts_too()
    {
        var (owner, member, memberId, _, generalId) = await CreateTeamAsync();
        await member.PutAsJsonAsync($"/api/v1/channels/{generalId}/mute", new { muted = true });
        var chatId = (await owner.PostJsonOkAsync("/api/v1/conversations", new { type = "Direct", userId = memberId })).GetProperty("id").GetGuid();
        await owner.PostAsJsonAsync($"/api/v1/conversations/{chatId}/messages", new { id = Guid.NewGuid(), bodyMarkdown = "hi" });

        Assert.True((await RoomAsync(member, generalId))!.Value.GetProperty("isMuted").GetBoolean());
        var chat = (await RoomAsync(member, chatId))!.Value;
        Assert.Equal(1, chat.GetProperty("unreadCount").GetInt32());
        Assert.False(chat.GetProperty("isMuted").GetBoolean());

        await member.PutAsJsonAsync($"/api/v1/channels/{generalId}/mute", new { muted = false });
        Assert.Null(await RoomAsync(member, generalId));
    }
}
