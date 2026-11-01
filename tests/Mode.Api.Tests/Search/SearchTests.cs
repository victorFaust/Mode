using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Mode.Api.Endpoints;
using Mode.Api.Tests.Infrastructure;

namespace Mode.Api.Tests.Search;

[Collection(ApiCollection.Name)]
public sealed class SearchTests(ApiFixture api)
{
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

    private static async Task<Guid> SendAsync(HttpClient client, Guid channelId, string body)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/channels/{channelId}/messages", new { id = Guid.NewGuid(), bodyMarkdown = body });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<List<JsonElement>> HitsAsync(HttpClient client, string kind, string query) =>
        (await client.GetJsonAsync($"/api/v1/search?{query}")).GetProperty(kind).EnumerateArray().ToList();

    [Fact]
    public async Task Messages_match_word_forms_and_come_back_with_highlights()
    {
        var team = await CreateTeamAsync();
        var id = await SendAsync(team.Owner, team.GeneralId, "The quarterly **budgets** were approved yesterday");

        var hit = Assert.Single(await HitsAsync(team.Member, "messages", "q=budget"));

        Assert.Equal(id, hit.GetProperty("messageId").GetGuid());
        Assert.Equal("General", hit.GetProperty("roomName").GetString());
        Assert.Equal(team.OwnerId, hit.GetProperty("author").GetProperty("id").GetString());
        var snippet = hit.GetProperty("snippet");
        var text = snippet.GetProperty("text").GetString()!;
        var range = Assert.Single(snippet.GetProperty("highlights").EnumerateArray());
        Assert.Equal("budgets", text.Substring(range.GetProperty("start").GetInt32(), range.GetProperty("length").GetInt32()));
        Assert.Empty(await HitsAsync(team.Member, "messages", "q=budget -approved"));
        Assert.Equal(HttpStatusCode.BadRequest, (await team.Member.GetAsync("/api/v1/search?q=a")).StatusCode);
    }

    [Fact]
    public async Task Results_never_include_rooms_the_user_cannot_read_or_deleted_messages()
    {
        var team = await CreateTeamAsync();
        var privateId = await ApiFixture.CreateChannelAsync(team.Owner, team.WorkspaceId, "board", "Private");
        var id = await SendAsync(team.Owner, privateId, "secret pineapple plan");
        using var outsider = api.ClientFor(api.NewUser());

        Assert.Empty(await HitsAsync(team.Member, "messages", "q=pineapple"));
        Assert.Empty(await HitsAsync(outsider, "messages", "q=pineapple"));
        Assert.Empty(await HitsAsync(team.Member, "messages", $"q=pineapple&channelId={privateId}"));
        Assert.Single(await HitsAsync(team.Owner, "messages", "q=pineapple"));

        await team.Owner.PostJsonOkAsync($"/api/v1/channels/{privateId}/members", new { userId = team.MemberId });
        Assert.Single(await HitsAsync(team.Member, "messages", "q=pineapple"));
        await team.Owner.DeleteAsync($"/api/v1/channels/{privateId}/members/{team.MemberId}");
        Assert.Empty(await HitsAsync(team.Member, "messages", "q=pineapple"));

        await team.Owner.DeleteAsync($"/api/v1/messages/{id}");
        Assert.Empty(await HitsAsync(team.Owner, "messages", "q=pineapple"));
    }

    [Fact]
    public async Task Files_match_names_and_contents_but_only_when_clean()
    {
        var team = await CreateTeamAsync();
        await team.Member.UploadFileAsync("Channel", team.GeneralId, "roadmap.txt", Encoding.UTF8.GetBytes("migration of the payments platform"));
        await team.Member.UploadFileAsync("Channel", team.GeneralId, "payments-virus.txt", Encoding.UTF8.GetBytes(TestFiles.Eicar));
        var deleted = await team.Member.UploadFileAsync("Channel", team.GeneralId, "payments-old.txt", Encoding.UTF8.GetBytes("payments"));

        Assert.Empty(await HitsAsync(team.Owner, "files", "q=payments"));
        await api.RunJobsAsync();
        await team.Member.DeleteAsync($"/api/v1/files/{deleted.GetProperty("id").GetGuid()}");

        var byContent = Assert.Single(await HitsAsync(team.Owner, "files", "q=payment&scope=Files"));
        Assert.Equal("roadmap.txt", byContent.GetProperty("name").GetString());
        Assert.Contains("payments", byContent.GetProperty("snippet").GetProperty("text").GetString());
        var byName = Assert.Single(await HitsAsync(team.Owner, "files", "q=roadmap"));
        Assert.Equal(team.MemberId, byName.GetProperty("uploadedBy").GetProperty("id").GetString());
        Assert.Empty(await HitsAsync(team.Owner, "messages", "q=roadmap&scope=Files"));
    }

    [Fact]
    public async Task Guests_do_not_get_people_results()
    {
        var team = await CreateTeamAsync();
        var name = "Findable " + Guid.NewGuid().ToString("N")[..8];
        var colleagueId = api.NewUser(name);
        await ApiFixture.AddMemberAsync(team.Owner, team.WorkspaceId, colleagueId, "Member");
        var guestId = api.NewUser("Guest");
        await ApiFixture.AddMemberAsync(team.Owner, team.WorkspaceId, guestId, "Guest");
        using var guest = api.ClientFor(guestId);

        var found = Assert.Single(await HitsAsync(team.Member, "people", $"q={Uri.EscapeDataString(name)}&scope=People"));
        Assert.Equal(colleagueId, found.GetProperty("id").GetString());
        Assert.Empty(await HitsAsync(guest, "people", $"q={Uri.EscapeDataString(name)}&scope=People"));
    }

    [Fact]
    public async Task Filters_narrow_by_room_author_and_date()
    {
        var team = await CreateTeamAsync();
        var randomId = await ApiFixture.CreateChannelAsync(team.Owner, team.WorkspaceId, "random");
        await SendAsync(team.Owner, team.GeneralId, "kiwi in general");
        await SendAsync(team.Member, randomId, "kiwi in random");

        Assert.Equal(2, (await HitsAsync(team.Member, "messages", "q=kiwi")).Count);
        var inRandom = Assert.Single(await HitsAsync(team.Member, "messages", $"q=kiwi&channelId={randomId}"));
        Assert.Equal("random", inRandom.GetProperty("roomName").GetString());
        var byOwner = Assert.Single(await HitsAsync(team.Member, "messages", $"q=kiwi&authorId={team.OwnerId}"));
        Assert.Equal(team.OwnerId, byOwner.GetProperty("author").GetProperty("id").GetString());
        var tomorrow = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
        Assert.Empty(await HitsAsync(team.Member, "messages", $"q=kiwi&from={tomorrow}"));
        Assert.Equal(2, (await HitsAsync(team.Member, "messages", $"q=kiwi&to={tomorrow}")).Count);
    }

    [Fact]
    public void Headlines_become_plain_text_with_offsets()
    {
        var parsed = SearchEndpoints.ParseHeadline("the [[[budget]]] and [[[budgets]]] <b>");

        Assert.Equal("the budget and budgets <b>", parsed.Text);
        Assert.Equal([new TextRange(4, 6), new TextRange(15, 7)], parsed.Highlights);
    }
}
