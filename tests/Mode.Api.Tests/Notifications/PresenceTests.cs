using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mode.Api.Realtime;
using Mode.Api.Tests.Infrastructure;

namespace Mode.Api.Tests.Notifications;

[Collection(ApiCollection.Name)]
public sealed class PresenceTests(ApiFixture api)
{
    private async Task<(string OwnerId, string MemberId, string StrangerId)> CreateTeamAsync()
    {
        var ownerId = api.NewUser("Owner");
        var memberId = api.NewUser("Member");
        using var owner = api.ClientFor(ownerId);
        var workspaceId = await ApiFixture.CreateWorkspaceAsync(owner);
        await ApiFixture.AddMemberAsync(owner, workspaceId, memberId, "Member");
        return (ownerId, memberId, api.NewUser("Stranger"));
    }

    private static Predicate<JsonElement> Status(string userId, string status) =>
        e => e.GetProperty("userId").GetString() == userId && e.GetProperty("status").GetString() == status;

    [Fact]
    public async Task Colleagues_see_each_others_presence_and_strangers_do_not()
    {
        var (ownerId, memberId, strangerId) = await CreateTeamAsync();
        await using var member = await RealtimeClient.ConnectAsync(api, memberId);
        await using var stranger = await RealtimeClient.ConnectAsync(api, strangerId);
        await using var owner = await RealtimeClient.ConnectAsync(api, ownerId);

        var subscribed = (await owner.SubscribePresenceAsync(memberId, strangerId, ownerId)).EnumerateArray().ToList();

        Assert.Equal([memberId, ownerId], subscribed.Select(p => p.GetProperty("userId").GetString()));
        // Away after 2s in tests, so either online status is fine here.
        Assert.All(subscribed, p => Assert.Contains(p.GetProperty("status").GetString(), new[] { "Available", "Away" }));
        using var http = api.ClientFor(ownerId);
        var lookup = await http.GetJsonAsync($"/api/v1/users/presence?ids={memberId},{strangerId}");
        Assert.Equal([memberId], lookup.EnumerateArray().Select(p => p.GetProperty("userId").GetString()));
        using var strangerHttp = api.ClientFor(strangerId);
        Assert.Empty((await strangerHttp.GetJsonAsync($"/api/v1/users/presence?ids={memberId}")).EnumerateArray());
    }

    [Fact]
    public async Task Inactivity_turns_away_and_activity_brings_people_back()
    {
        var (ownerId, memberId, _) = await CreateTeamAsync();
        await using var member = await RealtimeClient.ConnectAsync(api, memberId);
        await using var owner = await RealtimeClient.ConnectAsync(api, ownerId);
        await owner.SubscribePresenceAsync(memberId);

        await owner.WaitForAsync(RealtimeEvents.PresenceChanged, Status(memberId, "Away"),
            TimeSpan.FromSeconds(ApiFactory.AwayAfterSeconds + 10));
        await member.HeartbeatAsync(active: true);

        await owner.WaitForAsync(RealtimeEvents.PresenceChanged, Status(memberId, "Available"));
    }

    [Fact]
    public async Task Chosen_status_survives_reconnects_and_disconnect_shows_offline()
    {
        var (ownerId, memberId, _) = await CreateTeamAsync();
        using var memberHttp = api.ClientFor(memberId);
        await using var owner = await RealtimeClient.ConnectAsync(api, ownerId);
        await owner.SubscribePresenceAsync(memberId);

        await using (var member = await RealtimeClient.ConnectAsync(api, memberId))
        {
            var set = await memberHttp.PutAsJsonAsync("/api/v1/me/presence", new { status = "Busy", message = "In a meeting" });
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            var busy = await owner.WaitForAsync(RealtimeEvents.PresenceChanged, Status(memberId, "Busy"));
            Assert.Equal("In a meeting", busy.GetProperty("message").GetString());
        }

        await owner.WaitForAsync(RealtimeEvents.PresenceChanged, Status(memberId, "Offline"));
        await using var again = await RealtimeClient.ConnectAsync(api, memberId);
        using var ownerHttp = api.ClientFor(ownerId);
        var lookup = await ownerHttp.GetJsonAsync($"/api/v1/users/presence?ids={memberId}");
        Assert.Equal("Busy", lookup[0].GetProperty("status").GetString());
        Assert.Equal("In a meeting", lookup[0].GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("Offline")]
    [InlineData("Sleeping")]
    public async Task Only_real_statuses_can_be_chosen(string status)
    {
        using var client = api.ClientFor(api.NewUser());
        var response = await client.PutAsJsonAsync("/api/v1/me/presence", new { status });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
