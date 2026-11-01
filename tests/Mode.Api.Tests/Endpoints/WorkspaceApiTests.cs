using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Mode.Api.Auditing;
using Mode.Api.Tests.Infrastructure;
using Mode.Domain.Entities;

namespace Mode.Api.Tests.Endpoints;

[Collection(ApiCollection.Name)]
public sealed class WorkspaceApiTests(ApiFixture api)
{
    [Fact]
    public async Task Creating_a_workspace_makes_the_caller_owner_and_adds_General()
    {
        var ownerId = api.NewUser("Owner");
        using var owner = api.ClientFor(ownerId);

        var workspaceId = await ApiFixture.CreateWorkspaceAsync(owner, "Payments");

        var channels = await owner.GetJsonAsync($"/api/v1/workspaces/{workspaceId}/channels");
        Assert.Equal(["General"], channels.EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        var members = await owner.GetJsonAsync($"/api/v1/workspaces/{workspaceId}/members");
        var only = Assert.Single(members.EnumerateArray());
        Assert.Equal(ownerId, only.GetProperty("user").GetProperty("id").GetString());
        Assert.Equal("Owner", only.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Workspace_name_is_required() =>
        Assert.Equal(HttpStatusCode.BadRequest, (await api.ClientFor(api.NewUser()).PostAsJsonAsync("/api/v1/workspaces", new { name = "  " })).StatusCode);

    [Fact]
    public async Task Host_guests_cannot_create_workspaces() =>
        Assert.Equal(HttpStatusCode.Forbidden, (await api.ClientFor(api.NewUser(), "guest").PostAsJsonAsync("/api/v1/workspaces", new { name = "x" })).StatusCode);

    [Fact]
    public async Task Only_owners_add_members_and_only_active_directory_users_can_be_added()
    {
        using var owner = api.ClientFor(api.NewUser());
        var workspaceId = await ApiFixture.CreateWorkspaceAsync(owner);
        var memberId = api.NewUser();
        await ApiFixture.AddMemberAsync(owner, workspaceId, memberId, "Member");
        using var member = api.ClientFor(memberId);
        var url = $"/api/v1/workspaces/{workspaceId}/members";

        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(url, new { userId = memberId, role = "Member" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(url, new { userId = api.NewUser(active: false), role = "Member" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(url, new { userId = "u-nobody", role = "Member" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync(url, new { userId = api.NewUser(), role = "Member" })).StatusCode);
    }

    [Fact]
    public async Task Guests_cannot_list_members_or_search_the_directory()
    {
        using var owner = api.ClientFor(api.NewUser());
        var workspaceId = await ApiFixture.CreateWorkspaceAsync(owner);
        var guestId = api.NewUser();
        await ApiFixture.AddMemberAsync(owner, workspaceId, guestId, "Guest");
        using var guest = api.ClientFor(guestId);

        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync($"/api/v1/workspaces/{workspaceId}/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync("/api/v1/users/search?q=test")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/users/search?q=test")).StatusCode);
    }

    [Fact]
    public async Task The_last_owner_cannot_be_demoted_or_removed()
    {
        var ownerId = api.NewUser();
        using var owner = api.ClientFor(ownerId);
        var workspaceId = await ApiFixture.CreateWorkspaceAsync(owner);
        var memberUrl = $"/api/v1/workspaces/{workspaceId}/members/{ownerId}";

        Assert.Equal(HttpStatusCode.Conflict, (await owner.PatchAsJsonAsync(memberUrl, new { role = "Member" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync(memberUrl)).StatusCode);

        var secondOwner = api.NewUser();
        await ApiFixture.AddMemberAsync(owner, workspaceId, secondOwner, "Owner");
        Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(memberUrl, new { role = "Member" })).StatusCode);
    }

    [Fact]
    public async Task Two_owners_demoting_each_other_at_once_leave_one_owner()
    {
        var aId = api.NewUser();
        var bId = api.NewUser();
        using var a = api.ClientFor(aId);
        using var b = api.ClientFor(bId);
        var workspaceId = await ApiFixture.CreateWorkspaceAsync(a);
        await ApiFixture.AddMemberAsync(a, workspaceId, bId, "Owner");

        var responses = await Task.WhenAll(
            a.PatchAsJsonAsync($"/api/v1/workspaces/{workspaceId}/members/{bId}", new { role = "Member" }),
            b.PatchAsJsonAsync($"/api/v1/workspaces/{workspaceId}/members/{aId}", new { role = "Member" }));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        await using var db = api.CreateDbContext();
        Assert.Equal(1, await db.WorkspaceMembers.CountAsync(m => m.WorkspaceId == workspaceId && m.Role == WorkspaceRole.Owner));
    }

    [Fact]
    public async Task Removing_a_member_revokes_private_channels_even_after_re_adding()
    {
        using var owner = api.ClientFor(api.NewUser());
        var workspaceId = await ApiFixture.CreateWorkspaceAsync(owner);
        var memberId = api.NewUser();
        await ApiFixture.AddMemberAsync(owner, workspaceId, memberId, "Member");
        var privateId = await ApiFixture.CreateChannelAsync(owner, workspaceId, "secret", "Private");
        await owner.PostJsonOkAsync($"/api/v1/channels/{privateId}/members", new { userId = memberId });
        using var member = api.ClientFor(memberId);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync($"/api/v1/channels/{privateId}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/workspaces/{workspaceId}/members/{memberId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/v1/channels/{privateId}")).StatusCode);

        await ApiFixture.AddMemberAsync(owner, workspaceId, memberId, "Member");
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/v1/channels/{privateId}")).StatusCode);
    }

    [Fact]
    public async Task Members_can_leave_on_their_own()
    {
        using var owner = api.ClientFor(api.NewUser());
        var workspaceId = await ApiFixture.CreateWorkspaceAsync(owner);
        var memberId = api.NewUser();
        await ApiFixture.AddMemberAsync(owner, workspaceId, memberId, "Member");
        using var member = api.ClientFor(memberId);

        Assert.Equal(HttpStatusCode.Forbidden, (await member.DeleteAsync($"/api/v1/workspaces/{workspaceId}/members/{api.NewUser()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await member.DeleteAsync($"/api/v1/workspaces/{workspaceId}/members/{memberId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync($"/api/v1/workspaces/{workspaceId}")).StatusCode);
    }

    [Fact]
    public async Task Membership_and_role_changes_are_audited()
    {
        var ownerId = api.NewUser();
        using var owner = api.ClientFor(ownerId);
        var workspaceId = await ApiFixture.CreateWorkspaceAsync(owner);
        var memberId = api.NewUser();
        await ApiFixture.AddMemberAsync(owner, workspaceId, memberId, "Member");
        await owner.PatchAsJsonAsync($"/api/v1/workspaces/{workspaceId}/members/{memberId}", new { role = "Owner" });
        await owner.DeleteAsync($"/api/v1/workspaces/{workspaceId}/members/{memberId}");

        await using var db = api.CreateDbContext();
        var actions = await db.AuditLogs.Where(a => a.WorkspaceId == workspaceId).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync();
        Assert.Equal(
            [AuditActions.WorkspaceCreated, AuditActions.ChannelCreated, AuditActions.MemberAdded, AuditActions.MemberRoleChanged, AuditActions.MemberRemoved],
            actions);
        Assert.All(await db.AuditLogs.Where(a => a.WorkspaceId == workspaceId).ToListAsync(), a => Assert.Equal(ownerId, a.ActorUserId));
    }

    [Fact]
    public async Task Workspace_list_shows_only_my_workspaces()
    {
        using var a = api.ClientFor(api.NewUser());
        using var b = api.ClientFor(api.NewUser());
        var mine = await ApiFixture.CreateWorkspaceAsync(a, "Mine");
        await ApiFixture.CreateWorkspaceAsync(b, "Theirs");

        var list = await a.GetJsonAsync("/api/v1/workspaces");

        Assert.Equal([mine], list.EnumerateArray().Select(w => w.GetProperty("id").GetGuid()));
    }
}
