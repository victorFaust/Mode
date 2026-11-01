using System.Net;
using System.Net.Http.Json;
using Mode.Api.Tests.Infrastructure;

namespace Mode.Api.Tests.Endpoints;

[Collection(ApiCollection.Name)]
public sealed class ChannelApiTests(ApiFixture api)
{
    private sealed record Team(HttpClient Owner, HttpClient Member, HttpClient Guest, string MemberId, string GuestId, Guid WorkspaceId) : IDisposable
    {
        public void Dispose()
        {
            Owner.Dispose();
            Member.Dispose();
            Guest.Dispose();
        }
    }

    private async Task<Team> CreateTeamAsync()
    {
        var owner = api.ClientFor(api.NewUser());
        var workspaceId = await ApiFixture.CreateWorkspaceAsync(owner);
        var memberId = api.NewUser();
        var guestId = api.NewUser();
        await ApiFixture.AddMemberAsync(owner, workspaceId, memberId, "Member");
        await ApiFixture.AddMemberAsync(owner, workspaceId, guestId, "Guest");
        return new Team(owner, api.ClientFor(memberId), api.ClientFor(guestId), memberId, guestId, workspaceId);
    }

    private static async Task<string[]> ChannelNamesAsync(HttpClient client, Guid workspaceId) =>
        (await client.GetJsonAsync($"/api/v1/workspaces/{workspaceId}/channels"))
        .EnumerateArray().Select(c => c.GetProperty("name").GetString()!).Order().ToArray();

    [Fact]
    public async Task Channel_names_are_unique_per_workspace_ignoring_case()
    {
        using var team = await CreateTeamAsync();
        await ApiFixture.CreateChannelAsync(team.Member, team.WorkspaceId, "Releases");

        var duplicate = await team.Owner.PostAsJsonAsync($"/api/v1/workspaces/{team.WorkspaceId}/channels", new { name = "  releases " });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task Guests_cannot_create_channels()
    {
        using var team = await CreateTeamAsync();

        var response = await team.Guest.PostAsJsonAsync($"/api/v1/workspaces/{team.WorkspaceId}/channels", new { name = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Private_channels_are_invisible_to_non_members()
    {
        using var team = await CreateTeamAsync();
        var privateId = await ApiFixture.CreateChannelAsync(team.Member, team.WorkspaceId, "hr", "Private");

        Assert.Equal(["General", "hr"], await ChannelNamesAsync(team.Member, team.WorkspaceId));
        Assert.Equal(["General"], await ChannelNamesAsync(team.Owner, team.WorkspaceId));
        Assert.Equal(HttpStatusCode.NotFound, (await team.Guest.GetAsync($"/api/v1/channels/{privateId}")).StatusCode);

        // Owners can manage a private channel they are not in, but cannot read it.
        var asOwner = await team.Owner.GetJsonAsync($"/api/v1/channels/{privateId}");
        Assert.True(asOwner.GetProperty("canManage").GetBoolean());
        Assert.False(asOwner.GetProperty("canPost").GetBoolean());
    }

    [Fact]
    public async Task Guests_see_a_standard_channel_only_after_being_added()
    {
        using var team = await CreateTeamAsync();
        var channelId = await ApiFixture.CreateChannelAsync(team.Owner, team.WorkspaceId, "project");

        Assert.Empty(await ChannelNamesAsync(team.Guest, team.WorkspaceId));
        Assert.Equal(HttpStatusCode.NotFound, (await team.Guest.GetAsync($"/api/v1/channels/{channelId}")).StatusCode);

        await team.Owner.PostJsonOkAsync($"/api/v1/channels/{channelId}/members", new { userId = team.GuestId });

        Assert.Equal(["project"], await ChannelNamesAsync(team.Guest, team.WorkspaceId));
    }

    [Fact]
    public async Task Archiving_makes_a_channel_read_only()
    {
        using var team = await CreateTeamAsync();
        var channelId = await ApiFixture.CreateChannelAsync(team.Owner, team.WorkspaceId, "old");

        var archived = await team.Owner.PostJsonOkAsync($"/api/v1/channels/{channelId}/archive", new { });

        Assert.True(archived.GetProperty("isArchived").GetBoolean());
        Assert.False((await team.Member.GetJsonAsync($"/api/v1/channels/{channelId}")).GetProperty("canPost").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await team.Owner.PatchAsJsonAsync($"/api/v1/channels/{channelId}", new { name = "new" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await team.Owner.PostAsJsonAsync($"/api/v1/channels/{channelId}/members", new { userId = team.GuestId })).StatusCode);
        Assert.DoesNotContain("old", await ChannelNamesAsync(team.Member, team.WorkspaceId));
    }

    [Fact]
    public async Task Members_manage_only_channels_they_created()
    {
        using var team = await CreateTeamAsync();
        var ownersChannel = await ApiFixture.CreateChannelAsync(team.Owner, team.WorkspaceId, "owners");
        var membersChannel = await ApiFixture.CreateChannelAsync(team.Member, team.WorkspaceId, "members");

        Assert.Equal(HttpStatusCode.Forbidden, (await team.Member.PatchAsJsonAsync($"/api/v1/channels/{ownersChannel}", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await team.Member.PatchAsJsonAsync($"/api/v1/channels/{membersChannel}", new { name = "renamed" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await team.Owner.PatchAsJsonAsync($"/api/v1/channels/{membersChannel}", new { description = "set by owner" })).StatusCode);
    }

    [Fact]
    public async Task Channel_members_must_belong_to_the_workspace()
    {
        using var team = await CreateTeamAsync();
        var channelId = await ApiFixture.CreateChannelAsync(team.Owner, team.WorkspaceId, "ops", "Private");

        var response = await team.Owner.PostAsJsonAsync($"/api/v1/channels/{channelId}/members", new { userId = api.NewUser() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Standard_channel_members_include_owners_members_and_added_guests()
    {
        using var team = await CreateTeamAsync();
        var channelId = await ApiFixture.CreateChannelAsync(team.Owner, team.WorkspaceId, "all");

        var before = await team.Member.GetJsonAsync($"/api/v1/channels/{channelId}/members");
        await team.Owner.PostJsonOkAsync($"/api/v1/channels/{channelId}/members", new { userId = team.GuestId });
        var after = await team.Member.GetJsonAsync($"/api/v1/channels/{channelId}/members");

        Assert.Equal(2, before.GetArrayLength());
        Assert.Equal(3, after.GetArrayLength());
    }
}
