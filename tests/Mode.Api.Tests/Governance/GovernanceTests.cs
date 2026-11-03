using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mode.Api.Governance;
using Mode.Api.Tests.Infrastructure;
using Npgsql;

namespace Mode.Api.Tests.Governance;

[Collection(ApiCollection.Name)]
public sealed class GovernanceTests(ApiFixture api)
{
    private HttpClient Admin(out string id)
    {
        id = api.NewUser("Admin");
        return api.ClientFor(id, "mode-admin");
    }

    private static async Task<Guid> GroupAsync(HttpClient client, params string[] others) =>
        (await client.PostJsonOkAsync("/api/v1/conversations", new { type = "Group", userIds = others })).GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> SendToChatAsync(HttpClient client, Guid chatId, string body, bool? ack = null) =>
        client.PostAsJsonAsync($"/api/v1/conversations/{chatId}/messages",
            new { id = Guid.NewGuid(), bodyMarkdown = body, acknowledgeDlpWarnings = ack });

    private static async Task<Guid> SendToChannelAsync(HttpClient client, Guid channelId, string body, Guid? parentId = null) =>
        (await client.PostJsonOkAsync($"/api/v1/channels/{channelId}/messages",
            new { id = Guid.NewGuid(), bodyMarkdown = body, parentMessageId = parentId })).GetProperty("id").GetGuid();

    private async Task BackdateAsync(Guid messageId, int days)
    {
        await using var db = api.CreateDbContext();
        var at = DateTimeOffset.UtcNow.AddDays(-days);
        await db.Messages.Where(m => m.Id == messageId).ExecuteUpdateAsync(s => s.SetProperty(m => m.CreatedAt, at));
    }

    private async Task<bool> ExistsAsync(Guid messageId)
    {
        await using var db = api.CreateDbContext();
        return await db.Messages.AnyAsync(m => m.Id == messageId);
    }

    private async Task RunRetentionAsync()
    {
        using var scope = api.Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RetentionService>().ApplyAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Admin_endpoints_are_forbidden_to_everyone_else()
    {
        using var user = api.ClientFor(api.NewUser());
        foreach (var url in new[] { "/api/v1/admin/workspaces", "/api/v1/admin/audit", "/api/v1/admin/holds", "/api/v1/admin/dlp", "/api/v1/admin/webhooks", "/api/v1/admin/exports" })
            Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync(url)).StatusCode);
        using var admin = Admin(out _);
        var allowed = await admin.GetAsync("/api/v1/admin/workspaces");
        Assert.True(allowed.IsSuccessStatusCode, $"{(int)allowed.StatusCode}: {await allowed.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Retention_deletes_old_channel_messages_but_holds_keep_them()
    {
        using var admin = Admin(out _);
        var ownerId = api.NewUser("Owner");
        var heldId = api.NewUser("Held");
        using var owner = api.ClientFor(ownerId);
        using var held = api.ClientFor(heldId);
        var workspace = await ApiFixture.CreateWorkspaceAsync(owner);
        await ApiFixture.AddMemberAsync(owner, workspace, heldId, "Member");
        var open = await ApiFixture.CreateChannelAsync(owner, workspace, "open");
        var frozen = await ApiFixture.CreateChannelAsync(owner, workspace, "frozen");

        var old = await SendToChannelAsync(owner, open, "old");
        var oldReply = await SendToChannelAsync(owner, open, "old reply", old);
        var oldByHeld = await SendToChannelAsync(held, open, "old by held person");
        var recent = await SendToChannelAsync(owner, open, "recent");
        var oldInHeldChannel = await SendToChannelAsync(owner, frozen, "old in held channel");
        foreach (var id in new[] { old, oldReply, oldByHeld, oldInHeldChannel }) await BackdateAsync(id, 40);

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/v1/admin/workspaces/{workspace}/retention", new { keepDays = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/v1/admin/workspaces/{workspace}/retention", new { keepDays = 0 })).StatusCode);
        await admin.PostJsonOkAsync("/api/v1/admin/holds", new { targetType = "Channel", targetId = frozen.ToString(), reason = "Case 1" });
        var userHold = await admin.PostJsonOkAsync("/api/v1/admin/holds", new { targetType = "User", targetId = heldId, reason = "Case 2" });

        await RunRetentionAsync();

        Assert.False(await ExistsAsync(old));
        Assert.False(await ExistsAsync(oldReply));
        Assert.True(await ExistsAsync(oldByHeld));
        Assert.True(await ExistsAsync(recent));
        Assert.True(await ExistsAsync(oldInHeldChannel));

        var release = await admin.PostAsync($"/api/v1/admin/holds/{userHold.GetProperty("id").GetGuid()}/release", null);
        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        await RunRetentionAsync();
        Assert.False(await ExistsAsync(oldByHeld));
    }

    [Fact]
    public async Task Audit_log_rows_cannot_be_changed_or_removed()
    {
        await using var db = api.CreateDbContext();
        await Assert.ThrowsAsync<PostgresException>(() => db.AuditLogs.ExecuteUpdateAsync(s => s.SetProperty(a => a.Action, "tampered")));
        await Assert.ThrowsAsync<PostgresException>(() => db.AuditLogs.ExecuteDeleteAsync());
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("TRUNCATE \"AuditLogs\""));
    }

    [Fact]
    public async Task Audit_csv_escapes_formulas_and_is_itself_audited()
    {
        Assert.Equal("\"'=SUM(A1)\"", AdminEndpoints.CsvField("=SUM(A1)"));
        Assert.Equal("\"say \"\"hi\"\"\"", AdminEndpoints.CsvField("say \"hi\""));

        using var admin = Admin(out var adminId);
        using var sneaky = api.ClientFor(api.NewUser("=HYPERLINK(\"x\")"));
        var workspace = await ApiFixture.CreateWorkspaceAsync(sneaky);
        var csv = await admin.GetStringAsync($"/api/v1/admin/audit/export?workspaceId={workspace}");
        Assert.StartsWith("\uFEFFId,At,", csv);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", csv);

        var page = await admin.GetJsonAsync($"/api/v1/admin/audit?actor={adminId}&action=audit.");
        Assert.Contains(page.GetProperty("items").EnumerateArray(), e => e.GetProperty("action").GetString() == "audit.exported");
    }

    [Fact]
    public async Task Dlp_warns_then_accepts_confirmation_blocks_and_stops_uploads()
    {
        using var admin = Admin(out var adminId);
        var token = "proj" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/admin/dlp",
                new { blockedExtensions = Array.Empty<string>(), patterns = new[] { new { name = "Bad", pattern = "(", action = "Block" } } })).StatusCode);
            var saved = await admin.PutAsJsonAsync("/api/v1/admin/dlp", new
            {
                blockedExtensions = new[] { ".TXT" },
                patterns = new[]
                {
                    new { name = "Card", pattern = @"\b4\d{15}\b", action = "Block" },
                    new { name = "Codename", pattern = token, action = "Warn" },
                },
            });
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

            var chat = await GroupAsync(admin, api.NewUser(), api.NewUser());
            var warned = await SendToChatAsync(admin, chat, $"about {token}");
            Assert.Equal(HttpStatusCode.Conflict, warned.StatusCode);
            Assert.Contains("dlp_warning", await warned.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Created, (await SendToChatAsync(admin, chat, $"about {token}", ack: true)).StatusCode);

            var blocked = await SendToChatAsync(admin, chat, "card 4111111111111111", ack: true);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
            Assert.Contains("dlp_blocked", await blocked.Content.ReadAsStringAsync());

            var upload = await admin.PostAsJsonAsync("/api/v1/files/uploads", new { room = new { type = "Conversation", id = chat }, name = "notes.txt", size = 5 });
            Assert.Equal(HttpStatusCode.BadRequest, upload.StatusCode);
            Assert.Contains(".txt", await upload.Content.ReadAsStringAsync());
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/v1/admin/dlp", new { blockedExtensions = Array.Empty<string>(), patterns = Array.Empty<object>() });
        }
    }

    [Fact]
    public async Task Webhooks_post_as_a_labelled_bot_and_reject_bad_keys()
    {
        using var admin = Admin(out _);
        var chat = await GroupAsync(admin, api.NewUser(), api.NewUser());
        using var outsider = Admin(out _);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await outsider.PostAsJsonAsync("/api/v1/admin/webhooks", new { conversationId = chat, name = "Ops" })).StatusCode);

        var created = await admin.PostJsonOkAsync("/api/v1/admin/webhooks", new { conversationId = chat, name = "Build bot" });
        var hookId = created.GetProperty("webhook").GetProperty("id").GetGuid();
        var secret = created.GetProperty("secret").GetString()!;
        using var anon = api.Anonymous();

        Task<HttpResponseMessage> Post(string? key, string text = "Build passed")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/hooks/{hookId}") { Content = JsonContent.Create(new { text }) };
            if (key is not null) request.Headers.Add(WebhookSecrets.Header, key);
            return anon.SendAsync(request);
        }

        var ok = await Post(secret);
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var message = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(message.GetProperty("isBot").GetBoolean());
        Assert.Equal("Build bot", message.GetProperty("author").GetProperty("displayName").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post("wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(null)).StatusCode);

        var rotated = await admin.PostJsonOkAsync($"/api/v1/admin/webhooks/{hookId}/rotate", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(secret)).StatusCode);
        var newSecret = rotated.GetProperty("secret").GetString()!;

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/admin/webhooks/{hookId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(newSecret)).StatusCode);
    }

    [Fact]
    public async Task Webhooks_are_rate_limited_per_hook()
    {
        using var admin = Admin(out _);
        var chat = await GroupAsync(admin, api.NewUser(), api.NewUser());
        var created = await admin.PostJsonOkAsync("/api/v1/admin/webhooks", new { conversationId = chat, name = "Chatty" });
        var hookId = created.GetProperty("webhook").GetProperty("id").GetGuid();
        using var anon = api.Anonymous();
        anon.DefaultRequestHeaders.Add(WebhookSecrets.Header, created.GetProperty("secret").GetString());

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i <= ApiFactory.WebhookPerMinute; i++)
            codes.Add((await anon.PostAsJsonAsync($"/api/v1/hooks/{hookId}", new { text = $"n{i}" })).StatusCode);
        Assert.All(codes.Take(ApiFactory.WebhookPerMinute), c => Assert.Equal(HttpStatusCode.Created, c));
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[^1]);
    }

    [Fact]
    public async Task Compliance_export_is_signed_and_keeps_edit_history()
    {
        using var admin = Admin(out _);
        var chat = await GroupAsync(admin, api.NewUser(), api.NewUser());
        var sent = await (await SendToChatAsync(admin, chat, "first draft")).Content.ReadFromJsonAsync<JsonElement>();
        await admin.PatchAsJsonAsync($"/api/v1/messages/{sent.GetProperty("id").GetGuid()}", new { bodyMarkdown = "final text" });

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/admin/exports", new { })).StatusCode);
        var requested = await admin.PostAsJsonAsync("/api/v1/admin/exports", new { conversationId = chat });
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var exportId = (await requested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.GetAsync($"/api/v1/admin/exports/{exportId}/download")).StatusCode);

        await api.RunJobsAsync();
        var list = await admin.GetJsonAsync("/api/v1/admin/exports");
        var export = list.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == exportId);
        Assert.Equal("Ready", export.GetProperty("status").GetString());

        var zipBytes = await admin.GetByteArrayAsync($"/api/v1/admin/exports/{exportId}/download");
        using var zip = new ZipArchive(new MemoryStream(zipBytes));
        string Read(string name)
        {
            using var reader = new StreamReader(zip.GetEntry(name)!.Open());
            return reader.ReadToEnd();
        }

        var manifest = Read("manifest.json");
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Convert.FromBase64String(ApiFactory.ComplianceSigningKey), Encoding.UTF8.GetBytes(manifest)));
        Assert.Equal(expected, Read("manifest.sig").Trim());
        var messages = Read("messages.jsonl");
        Assert.Contains("final text", messages);
        Assert.Contains("first draft", messages);
    }
}
