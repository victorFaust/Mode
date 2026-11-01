using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mode.Api.Endpoints;
using Mode.Api.Tests.Infrastructure;

namespace Mode.Api.Tests.Messaging;

/// <summary>Pinned messages, forwarding, edit history and per-person chat list settings.</summary>
[Collection(ApiCollection.Name)]
public sealed class ChatFeatureTests(ApiFixture api)
{
    private static async Task<Guid> ChatAsync(HttpClient client, params string[] others)
    {
        var body = others.Length == 1
            ? (object)new { type = "Direct", userId = others[0] }
            : new { type = "Group", userIds = others };
        var response = await client.PostAsJsonAsync("/api/v1/conversations", body);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> SendAsync(HttpClient client, Guid chatId, string body, Guid? parentId = null, Guid[]? fileIds = null) =>
        await client.PostJsonOkAsync($"/api/v1/conversations/{chatId}/messages",
            new { id = Guid.NewGuid(), bodyMarkdown = body, parentMessageId = parentId, fileIds });

    private static Guid Id(JsonElement element) => element.GetProperty("id").GetGuid();

    [Fact]
    public async Task Chat_members_pin_and_unpin_top_level_messages_and_outsiders_see_nothing()
    {
        var aId = api.NewUser("Ada");
        var bId = api.NewUser("Bola");
        using var a = api.ClientFor(aId);
        using var b = api.ClientFor(bId);
        using var outsider = api.ClientFor(api.NewUser());
        var chat = await ChatAsync(a, bId);
        var message = Id(await SendAsync(a, chat, "Agenda for Monday"));
        var reply = Id(await SendAsync(b, chat, "thanks", message));

        var pinned = await b.PutAsync($"/api/v1/messages/{message}/pin", null);
        var pins = await a.GetJsonAsync($"/api/v1/conversations/{chat}/pins");

        Assert.Equal(HttpStatusCode.OK, pinned.StatusCode);
        Assert.True((await pinned.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isPinned").GetBoolean());
        var pin = Assert.Single(pins.EnumerateArray());
        Assert.Equal(message, Id(pin.GetProperty("message")));
        Assert.Equal(bId, pin.GetProperty("pinnedBy").GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PutAsync($"/api/v1/messages/{reply}/pin", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PutAsync($"/api/v1/messages/{message}/pin", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/v1/conversations/{chat}/pins")).StatusCode);

        var unpinned = await a.DeleteAsync($"/api/v1/messages/{message}/pin");
        Assert.False((await unpinned.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isPinned").GetBoolean());
        Assert.Empty((await a.GetJsonAsync($"/api/v1/conversations/{chat}/pins")).EnumerateArray());
    }

    [Fact]
    public async Task Deleting_a_message_unpins_it_and_a_chat_holds_at_most_twenty_pins()
    {
        var bId = api.NewUser();
        using var a = api.ClientFor(api.NewUser());
        var chat = await ChatAsync(a, bId);
        var ids = new List<Guid>();
        for (var i = 0; i <= MessageEndpoints.MaxPinsPerRoom; i++) ids.Add(Id(await SendAsync(a, chat, $"note {i}")));
        foreach (var id in ids.Take(MessageEndpoints.MaxPinsPerRoom))
            Assert.Equal(HttpStatusCode.OK, (await a.PutAsync($"/api/v1/messages/{id}/pin", null)).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await a.PutAsync($"/api/v1/messages/{ids[^1]}/pin", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/api/v1/messages/{ids[0]}")).StatusCode);
        Assert.Equal(MessageEndpoints.MaxPinsPerRoom - 1, (await a.GetJsonAsync($"/api/v1/conversations/{chat}/pins")).GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await a.PutAsync($"/api/v1/messages/{ids[0]}/pin", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.PutAsync($"/api/v1/messages/{ids[^1]}/pin", null)).StatusCode);
    }

    [Fact]
    public async Task Forwarding_copies_text_as_a_card_without_widening_access()
    {
        var adaId = api.NewUser("Ada");
        var bolaId = api.NewUser("Bola");
        var chidiId = api.NewUser("Chidi");
        using var ada = api.ClientFor(adaId);
        using var bola = api.ClientFor(bolaId);
        using var chidi = api.ClientFor(chidiId);
        var source = await ChatAsync(ada, bolaId);
        var target = await ChatAsync(ada, chidiId);
        var file = await bola.UploadFileAsync("Conversation", source, "q3-notes.txt", "numbers"u8.ToArray());
        var original = await SendAsync(bola, source, $"Figures for @{{{adaId}}} are in: zanzibar", fileIds: [Id(file)]);

        var forwarded = await ada.PostJsonOkAsync($"/api/v1/messages/{Id(original)}/forward",
            new { id = Guid.NewGuid(), to = new { type = "Conversation", id = target }, bodyMarkdown = "FYI" });

        Assert.Equal("FYI", forwarded.GetProperty("bodyMarkdown").GetString());
        var card = forwarded.GetProperty("forwarded");
        Assert.Equal(Id(original), card.GetProperty("messageId").GetGuid());
        Assert.Equal(bolaId, card.GetProperty("author").GetProperty("id").GetString());
        var copy = card.GetProperty("bodyMarkdown").GetString()!;
        Assert.Contains("@Ada are in: zanzibar", copy);
        Assert.DoesNotContain("@{", copy);
        Assert.Contains("Files not forwarded: q3\\-notes\\.txt", copy);
        Assert.Empty(forwarded.GetProperty("files").EnumerateArray());

        // Chidi sees the card but not the original, its file or its chat.
        var seen = (await chidi.GetJsonAsync($"/api/v1/conversations/{target}/messages")).GetProperty("items")[0];
        Assert.Equal(Id(forwarded), Id(seen));
        Assert.Equal(HttpStatusCode.NotFound, (await chidi.GetAsync($"/api/v1/messages/{Id(original)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await chidi.LinkAsync(Id(file))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await chidi.PostAsJsonAsync($"/api/v1/messages/{Id(original)}/forward",
            new { id = Guid.NewGuid(), to = new { type = "Conversation", id = target } })).StatusCode);
        Assert.Contains((await chidi.GetJsonAsync("/api/v1/search?q=zanzibar")).GetProperty("messages").EnumerateArray(),
            hit => hit.GetProperty("messageId").GetGuid() == Id(forwarded));

        // The copy carries no mentions, so nobody is notified again.
        await using var db = api.CreateDbContext();
        Assert.False(await db.Mentions.AnyAsync(m => m.MessageId == Id(forwarded)));
    }

    [Fact]
    public async Task Forwarding_needs_a_target_you_can_post_in_and_the_copy_cannot_be_edited()
    {
        var bId = api.NewUser();
        var cId = api.NewUser();
        using var a = api.ClientFor(api.NewUser());
        using var c = api.ClientFor(cId);
        var mine = await ChatAsync(a, bId);
        var notMine = await ChatAsync(c, bId);
        var message = Id(await SendAsync(a, mine, "original text"));

        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync($"/api/v1/messages/{message}/forward",
            new { id = Guid.NewGuid(), to = new { type = "Conversation", id = notMine } })).StatusCode);

        var forwarded = await a.PostJsonOkAsync($"/api/v1/messages/{message}/forward",
            new { id = Guid.NewGuid(), to = new { type = "Conversation", id = mine } });
        Assert.Equal("", forwarded.GetProperty("bodyMarkdown").GetString());

        // Forwarding a forward without a note passes on the first card, not a card inside a card.
        var again = await a.PostJsonOkAsync($"/api/v1/messages/{Id(forwarded)}/forward",
            new { id = Guid.NewGuid(), to = new { type = "Conversation", id = mine } });
        Assert.Equal(message, again.GetProperty("forwarded").GetProperty("messageId").GetGuid());
        Assert.Equal("original text", again.GetProperty("forwarded").GetProperty("bodyMarkdown").GetString());

        var edited = await a.PatchAsJsonAsync($"/api/v1/messages/{Id(forwarded)}", new { bodyMarkdown = "my note" });
        var after = await edited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("my note", after.GetProperty("bodyMarkdown").GetString());
        Assert.Equal("original text", after.GetProperty("forwarded").GetProperty("bodyMarkdown").GetString());
    }

    [Fact]
    public async Task Only_the_author_sees_earlier_versions_and_deleted_text_is_never_listed()
    {
        var bId = api.NewUser();
        using var a = api.ClientFor(api.NewUser());
        using var b = api.ClientFor(bId);
        var chat = await ChatAsync(a, bId);
        var id = Id(await SendAsync(a, chat, "first"));
        await a.PatchAsJsonAsync($"/api/v1/messages/{id}", new { bodyMarkdown = "second" });
        await a.DeleteAsync($"/api/v1/messages/{id}");
        await a.PostAsync($"/api/v1/messages/{id}/restore", null);
        await a.PatchAsJsonAsync($"/api/v1/messages/{id}", new { bodyMarkdown = "third" });

        var history = (await a.GetJsonAsync($"/api/v1/messages/{id}/history")).EnumerateArray()
            .Select(v => v.GetProperty("bodyMarkdown").GetString()).ToList();

        Assert.Equal(["first", "second", "third"], history);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.GetAsync($"/api/v1/messages/{id}/history")).StatusCode);
        await a.DeleteAsync($"/api/v1/messages/{id}");
        Assert.Equal(HttpStatusCode.Forbidden, (await a.GetAsync($"/api/v1/messages/{id}/history")).StatusCode);
    }

    [Fact]
    public async Task Pinning_and_hiding_a_chat_is_personal_and_a_new_message_unhides_it()
    {
        var bId = api.NewUser();
        using var a = api.ClientFor(api.NewUser());
        using var b = api.ClientFor(bId);
        using var outsider = api.ClientFor(api.NewUser());
        var chat = await ChatAsync(a, bId);

        var pinned = await a.PutAsJsonAsync($"/api/v1/conversations/{chat}/settings", new { isPinned = true });
        var hidden = await (await a.PutAsJsonAsync($"/api/v1/conversations/{chat}/settings", new { isHidden = true }))
            .Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, pinned.StatusCode);
        Assert.True(hidden.GetProperty("isPinned").GetBoolean());
        Assert.True(hidden.GetProperty("isHidden").GetBoolean());
        var theirs = (await b.GetJsonAsync("/api/v1/conversations")).EnumerateArray().Single(c => Id(c) == chat);
        Assert.False(theirs.GetProperty("isPinned").GetBoolean());
        Assert.False(theirs.GetProperty("isHidden").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound,
            (await outsider.PutAsJsonAsync($"/api/v1/conversations/{chat}/settings", new { isHidden = true })).StatusCode);

        await SendAsync(b, chat, "are you there?");

        var mine = await a.GetJsonAsync($"/api/v1/conversations/{chat}");
        Assert.False(mine.GetProperty("isHidden").GetBoolean());
        Assert.True(mine.GetProperty("isPinned").GetBoolean());
    }
}
