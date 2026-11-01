using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mode.Api.Tests.Infrastructure;

namespace Mode.Api.Tests.Messaging;

[Collection(ApiCollection.Name)]
public sealed class ConversationApiTests(ApiFixture api)
{
    private static Task<HttpResponseMessage> StartDirectAsync(HttpClient client, string otherId) =>
        client.PostAsJsonAsync("/api/v1/conversations", new { type = "Direct", userId = otherId });

    [Fact]
    public async Task A_direct_chat_is_unique_per_pair()
    {
        var aId = api.NewUser();
        var bId = api.NewUser();
        using var a = api.ClientFor(aId);
        using var b = api.ClientFor(bId);

        var first = await StartDirectAsync(a, bId);
        var again = await StartDirectAsync(a, bId);
        var reverse = await StartDirectAsync(b, aId.ToUpperInvariant());

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reverse.StatusCode);
        var ids = new List<Guid>();
        foreach (var r in new[] { first, again, reverse })
            ids.Add((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.Single(ids.Distinct());
    }

    [Fact]
    public async Task Chats_need_valid_other_people()
    {
        var meId = api.NewUser();
        using var me = api.ClientFor(meId);

        Assert.Equal(HttpStatusCode.BadRequest, (await StartDirectAsync(me, meId)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await StartDirectAsync(me, "u-nobody")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await StartDirectAsync(me, api.NewUser(active: false))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await me.PostAsJsonAsync("/api/v1/conversations", new { type = "Group", userIds = new[] { api.NewUser() } })).StatusCode);
    }

    [Fact]
    public async Task Group_chats_can_be_renamed_by_members_and_outsiders_see_nothing()
    {
        var aId = api.NewUser();
        var bId = api.NewUser();
        using var a = api.ClientFor(aId);
        using var b = api.ClientFor(bId);
        using var outsider = api.ClientFor(api.NewUser());
        var created = await a.PostJsonOkAsync("/api/v1/conversations", new { type = "Group", userIds = new[] { bId, api.NewUser() }, name = "Launch" });
        var id = created.GetProperty("id").GetGuid();

        var renamed = await b.PatchAsJsonAsync($"/api/v1/conversations/{id}", new { name = "Launch v2" });
        await b.PostJsonOkAsync($"/api/v1/conversations/{id}/messages", new { id = Guid.NewGuid(), bodyMarkdown = "hi all" });

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(3, created.GetProperty("members").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/v1/conversations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/v1/conversations/{id}/messages")).StatusCode);
        var page = await a.GetJsonAsync($"/api/v1/conversations/{id}/messages");
        Assert.Equal("hi all", page.GetProperty("items")[0].GetProperty("bodyMarkdown").GetString());
        var mine = await a.GetJsonAsync("/api/v1/conversations");
        Assert.Contains(mine.EnumerateArray(), c => c.GetProperty("id").GetGuid() == id && c.GetProperty("name").GetString() == "Launch v2");
    }

    [Fact]
    public async Task Direct_chats_cannot_be_renamed_or_left()
    {
        var aId = api.NewUser();
        var bId = api.NewUser();
        using var a = api.ClientFor(aId);
        var id = (await (await StartDirectAsync(a, bId)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await a.PatchAsJsonAsync($"/api/v1/conversations/{id}", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.DeleteAsync($"/api/v1/conversations/{id}/members/{aId}")).StatusCode);
    }

    [Fact]
    public async Task Leaving_a_group_removes_access_and_only_the_creator_removes_others()
    {
        var aId = api.NewUser();
        var bId = api.NewUser();
        var cId = api.NewUser();
        using var a = api.ClientFor(aId);
        using var b = api.ClientFor(bId);
        var id = (await a.PostJsonOkAsync("/api/v1/conversations", new { type = "Group", userIds = new[] { bId, cId } })).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await b.DeleteAsync($"/api/v1/conversations/{id}/members/{cId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await b.DeleteAsync($"/api/v1/conversations/{id}/members/{bId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/conversations/{id}/messages")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/api/v1/conversations/{id}/members/{cId}")).StatusCode);
    }

    [Fact]
    public async Task Guests_can_only_chat_with_people_in_their_workspaces()
    {
        var ownerId = api.NewUser();
        var guestId = api.NewUser();
        using var owner = api.ClientFor(ownerId);
        var workspaceId = await ApiFixture.CreateWorkspaceAsync(owner);
        await ApiFixture.AddMemberAsync(owner, workspaceId, guestId, "Member");
        using var hostGuest = api.ClientFor(guestId, "guest");

        Assert.Equal(HttpStatusCode.Forbidden, (await StartDirectAsync(hostGuest, api.NewUser())).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await StartDirectAsync(hostGuest, ownerId)).StatusCode);
    }
}
