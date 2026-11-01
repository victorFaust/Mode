using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mode.Api.Tests.Infrastructure;
using Mode.Domain.Entities;

namespace Mode.Api.Tests.Messaging;

[Collection(ApiCollection.Name)]
public sealed class MessagingApiTests(ApiFixture api)
{
    private sealed class Team : IDisposable
    {
        public required HttpClient Owner { get; init; }
        public required HttpClient Member { get; init; }
        public required HttpClient Member2 { get; init; }
        public required string OwnerId { get; init; }
        public required string MemberId { get; init; }
        public required string Member2Id { get; init; }
        public required Guid WorkspaceId { get; init; }
        public required Guid GeneralId { get; init; }

        public void Dispose()
        {
            Owner.Dispose();
            Member.Dispose();
            Member2.Dispose();
        }
    }

    private async Task<Team> CreateTeamAsync()
    {
        var ownerId = api.NewUser("Owner");
        var memberId = api.NewUser("Member");
        var member2Id = api.NewUser("Member Two");
        var owner = api.ClientFor(ownerId);
        var workspaceId = await ApiFixture.CreateWorkspaceAsync(owner);
        await ApiFixture.AddMemberAsync(owner, workspaceId, memberId, "Member");
        await ApiFixture.AddMemberAsync(owner, workspaceId, member2Id, "Member");
        var channels = await owner.GetJsonAsync($"/api/v1/workspaces/{workspaceId}/channels");
        return new Team
        {
            Owner = owner, Member = api.ClientFor(memberId), Member2 = api.ClientFor(member2Id),
            OwnerId = ownerId, MemberId = memberId, Member2Id = member2Id,
            WorkspaceId = workspaceId, GeneralId = channels[0].GetProperty("id").GetGuid(),
        };
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, Guid channelId, string body, Guid? id = null, Guid? parentId = null) =>
        client.PostAsJsonAsync($"/api/v1/channels/{channelId}/messages", new { id = id ?? Guid.NewGuid(), bodyMarkdown = body, parentMessageId = parentId });

    private static async Task<JsonElement> SendOkAsync(HttpClient client, Guid channelId, string body, Guid? parentId = null)
    {
        var response = await SendAsync(client, channelId, body, parentId: parentId);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Concurrent_sends_get_unique_gap_free_sequences()
    {
        using var team = await CreateTeamAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            SendAsync(i % 2 == 0 ? team.Owner : team.Member, team.GeneralId, $"message {i}")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var sequences = new List<long>();
        foreach (var response in responses)
            sequences.Add((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sequence").GetInt64());
        Assert.Equal(Enumerable.Range(1, 50).Select(i => (long)i), sequences.Order());
    }

    [Fact]
    public async Task Retrying_a_send_with_the_same_id_does_not_duplicate()
    {
        using var team = await CreateTeamAsync();
        var id = Guid.NewGuid();

        var first = await SendAsync(team.Member, team.GeneralId, "hello", id);
        var retry = await SendAsync(team.Member, team.GeneralId, "hello", id);
        var differentBody = await SendAsync(team.Member, team.GeneralId, "something else", id);
        var otherUser = await SendAsync(team.Member2, team.GeneralId, "hello", id);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(
            (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sequence").GetInt64(),
            (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sequence").GetInt64());
        Assert.Equal(HttpStatusCode.Conflict, differentBody.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, otherUser.StatusCode);
        await using var db = api.CreateDbContext();
        Assert.Equal(1, await db.Messages.CountAsync(m => m.ChannelId == team.GeneralId));
    }

    [Fact]
    public async Task Simultaneous_retries_create_one_message()
    {
        using var team = await CreateTeamAsync();
        var id = Guid.NewGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => SendAsync(team.Member, team.GeneralId, "once", id)));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK));
        await using var db = api.CreateDbContext();
        Assert.Equal(1, await db.Messages.CountAsync(m => m.ChannelId == team.GeneralId));
        Assert.Equal(1, await db.Channels.Where(c => c.Id == team.GeneralId).Select(c => c.LastSequence).SingleAsync());
    }

    [Fact]
    public async Task Messages_are_paged_newest_first()
    {
        using var team = await CreateTeamAsync();
        for (var i = 1; i <= 5; i++) await SendOkAsync(team.Member, team.GeneralId, $"m{i}");

        var first = await team.Member.GetJsonAsync($"/api/v1/channels/{team.GeneralId}/messages?limit=2");
        var before = first.GetProperty("nextBefore").GetInt64();
        var second = await team.Member.GetJsonAsync($"/api/v1/channels/{team.GeneralId}/messages?limit=2&before={before}");
        var third = await team.Member.GetJsonAsync($"/api/v1/channels/{team.GeneralId}/messages?limit=2&before={second.GetProperty("nextBefore").GetInt64()}");

        static string[] Bodies(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(m => m.GetProperty("bodyMarkdown").GetString()!).ToArray();
        Assert.Equal(["m5", "m4"], Bodies(first));
        Assert.Equal(["m3", "m2"], Bodies(second));
        Assert.Equal(["m1"], Bodies(third));
        Assert.Equal(JsonValueKind.Null, third.GetProperty("nextBefore").ValueKind);
    }

    [Fact]
    public async Task Replies_are_one_level_deep_and_update_the_parent()
    {
        using var team = await CreateTeamAsync();
        var parent = await SendOkAsync(team.Owner, team.GeneralId, "question");
        var parentId = parent.GetProperty("id").GetGuid();

        var reply = await SendOkAsync(team.Member, team.GeneralId, "answer", parentId);
        var nested = await SendAsync(team.Member2, team.GeneralId, "nested", parentId: reply.GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.BadRequest, nested.StatusCode);
        var updatedParent = await team.Owner.GetJsonAsync($"/api/v1/messages/{parentId}");
        Assert.Equal(1, updatedParent.GetProperty("replyCount").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, updatedParent.GetProperty("lastReplyAt").ValueKind);
        Assert.Equal(reply.GetProperty("sequence").GetInt64(), updatedParent.GetProperty("updatedSequence").GetInt64());

        var replies = await team.Owner.GetJsonAsync($"/api/v1/messages/{parentId}/replies");
        Assert.Equal(["answer"], replies.EnumerateArray().Select(m => m.GetProperty("bodyMarkdown").GetString()));
        var topLevel = await team.Owner.GetJsonAsync($"/api/v1/channels/{team.GeneralId}/messages");
        Assert.Equal(1, topLevel.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Edits_keep_history_and_only_the_author_can_edit()
    {
        using var team = await CreateTeamAsync();
        var message = await SendOkAsync(team.Member, team.GeneralId, "first draft");
        var id = message.GetProperty("id").GetGuid();

        var byOwner = await team.Owner.PatchAsJsonAsync($"/api/v1/messages/{id}", new { bodyMarkdown = "hijack" });
        var byAuthor = await team.Member.PatchAsJsonAsync($"/api/v1/messages/{id}", new { bodyMarkdown = "final" });

        Assert.Equal(HttpStatusCode.Forbidden, byOwner.StatusCode);
        var edited = await byAuthor.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("final", edited.GetProperty("bodyMarkdown").GetString());
        Assert.NotEqual(JsonValueKind.Null, edited.GetProperty("editedAt").ValueKind);
        Assert.True(edited.GetProperty("updatedSequence").GetInt64() > edited.GetProperty("sequence").GetInt64());

        await using var db = api.CreateDbContext();
        var history = await db.MessageEdits.Where(e => e.MessageId == id).ToListAsync();
        Assert.Equal("first draft", Assert.Single(history).PreviousBodyMarkdown);
    }

    [Fact]
    public async Task Delete_leaves_a_tombstone_and_keeps_the_original_for_compliance()
    {
        using var team = await CreateTeamAsync();
        var ownersMessage = await SendOkAsync(team.Owner, team.GeneralId, "owner text");
        var membersMessage = await SendOkAsync(team.Member, team.GeneralId, "secret @{" + team.Member2Id + "}");
        var membersId = membersMessage.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await team.Member2.DeleteAsync($"/api/v1/messages/{ownersMessage.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Owner.DeleteAsync($"/api/v1/messages/{membersId}")).StatusCode);

        var tombstone = await team.Member2.GetJsonAsync($"/api/v1/messages/{membersId}");
        Assert.True(tombstone.GetProperty("isDeleted").GetBoolean());
        Assert.Equal("", tombstone.GetProperty("bodyMarkdown").GetString());
        Assert.Equal(0, tombstone.GetProperty("mentions").GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await team.Member.PatchAsJsonAsync($"/api/v1/messages/{membersId}", new { bodyMarkdown = "x" })).StatusCode);

        await using var db = api.CreateDbContext();
        var original = await db.MessageEdits.SingleAsync(e => e.MessageId == membersId && e.Kind == MessageChangeKind.Delete);
        Assert.Equal("secret @{" + team.Member2Id + "}", original.PreviousBodyMarkdown);
        Assert.Equal(team.OwnerId, original.ChangedByUserId);
    }

    [Fact]
    public async Task Reactions_are_idempotent_and_show_who_reacted()
    {
        using var team = await CreateTeamAsync();
        var id = (await SendOkAsync(team.Owner, team.GeneralId, "react to me")).GetProperty("id").GetGuid();
        var url = $"/api/v1/messages/{id}/reactions/{Uri.EscapeDataString("👍")}";

        await team.Member.PutAsync(url, null);
        await team.Member.PutAsync(url, null);
        var afterSecondUser = await (await team.Member2.PutAsync(url, null)).Content.ReadFromJsonAsync<JsonElement>();
        var afterRemove = await (await team.Member.DeleteAsync(url)).Content.ReadFromJsonAsync<JsonElement>();

        var both = Assert.Single(afterSecondUser.EnumerateArray());
        Assert.Equal("👍", both.GetProperty("emoji").GetString());
        Assert.Equal(2, both.GetProperty("count").GetInt32());
        Assert.Equal([team.MemberId, team.Member2Id], both.GetProperty("userIds").EnumerateArray().Select(u => u.GetString()));
        Assert.Equal([team.Member2Id], Assert.Single(afterRemove.EnumerateArray()).GetProperty("userIds").EnumerateArray().Select(u => u.GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await team.Member.PutAsync($"/api/v1/messages/{id}/reactions/{Uri.EscapeDataString("<b>")}", null)).StatusCode);
    }

    [Fact]
    public async Task Unsafe_markdown_is_rejected_and_html_is_stored_escaped()
    {
        using var team = await CreateTeamAsync();

        var badLink = await SendAsync(team.Member, team.GeneralId, "[click](javascript:alert(1))");
        var html = await SendOkAsync(team.Member, team.GeneralId, "hi <script>alert(1)</script>");

        Assert.Equal(HttpStatusCode.BadRequest, badLink.StatusCode);
        Assert.Equal("hi &lt;script>alert(1)&lt;/script>", html.GetProperty("bodyMarkdown").GetString());
    }

    [Fact]
    public async Task Mentions_notify_only_people_who_can_read_the_room()
    {
        using var team = await CreateTeamAsync();
        var privateId = await ApiFixture.CreateChannelAsync(team.Owner, team.WorkspaceId, "leads", "Private");
        await team.Owner.PostJsonOkAsync($"/api/v1/channels/{privateId}/members", new { userId = team.MemberId });

        var direct = await SendOkAsync(team.Owner, privateId, $"@{{{team.MemberId}}} and @{{{team.Member2Id}}} please look");
        var everyone = await SendOkAsync(team.Member, privateId, "@channel heads up");

        await using var db = api.CreateDbContext();
        var directItems = await db.ActivityItems.Where(a => a.MessageId == direct.GetProperty("id").GetGuid()).ToListAsync();
        var everyoneItems = await db.ActivityItems.Where(a => a.MessageId == everyone.GetProperty("id").GetGuid()).ToListAsync();

        var onlyMember = Assert.Single(directItems);
        Assert.Equal((team.MemberId, ActivityType.Mention), (onlyMember.UserId, onlyMember.Type));
        Assert.True(onlyMember.ExpiresAt > DateTimeOffset.UtcNow.AddDays(29));
        var toOwner = Assert.Single(everyoneItems);
        Assert.Equal((team.OwnerId, ActivityType.ChannelMention), (toOwner.UserId, toOwner.Type));
        Assert.Equal(2, direct.GetProperty("mentions").GetArrayLength());
    }

    [Fact]
    public async Task Editing_in_a_new_mention_notifies_only_the_new_person()
    {
        using var team = await CreateTeamAsync();
        var message = await SendOkAsync(team.Owner, team.GeneralId, $"hi @{{{team.MemberId}}}");
        var id = message.GetProperty("id").GetGuid();

        var edit = await team.Owner.PatchAsJsonAsync($"/api/v1/messages/{id}", new { bodyMarkdown = $"hi @{{{team.MemberId}}} and @{{{team.Member2Id}}}" });

        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(2, (await edit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mentions").GetArrayLength());
        await using var db = api.CreateDbContext();
        var recipients = await db.ActivityItems.Where(a => a.MessageId == id).Select(a => a.UserId).ToListAsync();
        Assert.Equal(new[] { team.MemberId, team.Member2Id }.Order(), recipients.Order());
    }

    [Fact]
    public async Task Archived_channels_accept_no_messages_or_reactions()
    {
        using var team = await CreateTeamAsync();
        var channelId = await ApiFixture.CreateChannelAsync(team.Owner, team.WorkspaceId, "done");
        var id = (await SendOkAsync(team.Member, channelId, "before archive")).GetProperty("id").GetGuid();
        await team.Owner.PostJsonOkAsync($"/api/v1/channels/{channelId}/archive", new { });

        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(team.Member, channelId, "after")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await team.Member.PutAsync($"/api/v1/messages/{id}/reactions/ok", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await team.Member.GetAsync($"/api/v1/channels/{channelId}/messages")).StatusCode);
    }

    [Fact]
    public async Task People_who_cannot_read_the_room_get_not_found()
    {
        using var team = await CreateTeamAsync();
        var privateId = await ApiFixture.CreateChannelAsync(team.Owner, team.WorkspaceId, "hidden", "Private");
        var id = (await SendOkAsync(team.Owner, privateId, "classified")).GetProperty("id").GetGuid();
        using var outsider = api.ClientFor(api.NewUser());

        foreach (var client in new[] { team.Member, outsider })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/channels/{privateId}/messages")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(client, privateId, "let me in")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/messages/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/messages/{id}/replies")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync($"/api/v1/messages/{id}/reactions/ok", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/messages/{id}")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/v1/channels/{team.GeneralId}/messages")).StatusCode);
    }
}
