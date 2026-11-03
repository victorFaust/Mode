using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mode.Api.Realtime;
using Mode.Api.Tests.Infrastructure;

namespace Mode.Api.Tests.Security;

/// <summary>Findings from the Prompt 10 review; each test failed before its fix (see docs/SECURITY.md).</summary>
[Collection(ApiCollection.Name)]
public sealed class SecurityTests(ApiFixture api)
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(2);

    private static async Task<Guid> GroupAsync(HttpClient client, params string[] others) =>
        (await client.PostJsonOkAsync("/api/v1/conversations", new { type = "Group", userIds = others })).GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, Guid chatId, string body) =>
        client.PostAsJsonAsync($"/api/v1/conversations/{chatId}/messages", new { id = Guid.NewGuid(), bodyMarkdown = body });

    private static Predicate<JsonElement> Body(string text) =>
        e => e.GetProperty("message").GetProperty("bodyMarkdown").GetString() == text;

    [Fact]
    public async Task Sending_is_rate_limited_per_person()
    {
        var aId = api.NewUser();
        using var a = api.ClientFor(aId);
        using var other = api.ClientFor(api.NewUser());
        var chat = await GroupAsync(a, api.NewUser(), api.NewUser());

        var codes = await Task.WhenAll(Enumerable.Range(0, 61).Select(async i => (await SendAsync(a, chat, $"m{i}")).StatusCode));
        Assert.Equal(60, codes.Count(c => c == HttpStatusCode.Created));
        var limited = await SendAsync(a, chat, "one more");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter is not null);

        // Someone else is not affected.
        var otherChat = await GroupAsync(other, aId, api.NewUser());
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(other, otherChat, "still fine")).StatusCode);
    }

    [Fact]
    public async Task Search_and_people_search_are_rate_limited()
    {
        using var a = api.ClientFor(api.NewUser());
        for (var i = 0; i < 30; i++) Assert.Equal(HttpStatusCode.OK, (await a.GetAsync($"/api/v1/search?q=report{i}")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await a.GetAsync("/api/v1/search?q=report")).StatusCode);

        for (var i = 0; i < 60; i++) Assert.Equal(HttpStatusCode.OK, (await a.GetAsync($"/api/v1/users/search?q=ad{i}")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await a.GetAsync("/api/v1/users/search?q=ad")).StatusCode);
    }

    [Fact]
    public async Task Responses_carry_hardening_headers()
    {
        using var a = api.ClientFor(api.NewUser());
        var response = await a.GetAsync("/api/v1/me");
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Oversized_json_bodies_are_refused()
    {
        using var a = api.ClientFor(api.NewUser());
        var chat = await GroupAsync(a, api.NewUser(), api.NewUser());
        var huge = new StringContent($"{{\"id\":\"{Guid.NewGuid()}\",\"bodyMarkdown\":\"{new string('x', 2 * 1024 * 1024)}\"}}", Encoding.UTF8, "application/json");
        var response = await a.PostAsync($"/api/v1/conversations/{chat}/messages", huge);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Removed_group_chat_member_stops_receiving_events_immediately()
    {
        var aId = api.NewUser();
        var bId = api.NewUser();
        using var a = api.ClientFor(aId);
        var chat = await GroupAsync(a, bId, api.NewUser());
        await using var b = await RealtimeClient.ConnectAsync(api, bId);
        await using var watcher = await RealtimeClient.ConnectAsync(api, aId);
        Assert.True((await b.JoinAsync("Conversation", chat)).GetProperty("granted").GetBoolean());
        await watcher.JoinAsync("Conversation", chat);

        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/api/v1/conversations/{chat}/members/{bId}")).StatusCode);
        await SendAsync(a, chat, "after removal");

        await b.WaitForAsync(RealtimeEvents.RoomAccessRevoked);
        await watcher.WaitForAsync(RealtimeEvents.MessageCreated, Body("after removal"));
        await Task.Delay(QuietPeriod);
        Assert.DoesNotContain(b.Events(RealtimeEvents.MessageCreated), Body("after removal"));
    }

    [Fact]
    public async Task Deactivated_person_loses_realtime_access()
    {
        var aId = api.NewUser();
        var bId = api.NewUser();
        using var a = api.ClientFor(aId);
        var chat = await GroupAsync(a, bId, api.NewUser());
        await using var b = await RealtimeClient.ConnectAsync(api, bId);
        await using var watcher = await RealtimeClient.ConnectAsync(api, aId);
        await b.JoinAsync("Conversation", chat);
        await watcher.JoinAsync("Conversation", chat);

        api.SetActive(bId, false);
        Assert.Equal(1, await api.CheckRealtimeSessionsAsync());
        await SendAsync(a, chat, "after deactivation");

        await watcher.WaitForAsync(RealtimeEvents.MessageCreated, Body("after deactivation"));
        await Task.Delay(QuietPeriod);
        Assert.DoesNotContain(b.Events(RealtimeEvents.MessageCreated), Body("after deactivation"));
    }

    [Fact]
    public async Task Download_names_with_quotes_and_separators_stay_in_the_filename()
    {
        var aId = api.NewUser();
        using var a = api.ClientFor(aId);
        var chat = await GroupAsync(a, api.NewUser(), api.NewUser());
        var file = await a.UploadFileAsync("Conversation", chat, "q\"; filename=evil.html; x=.txt", Encoding.UTF8.GetBytes("hi"));
        await api.RunJobsAsync();

        var url = await a.LinkUrlAsync(file.GetProperty("id").GetGuid());
        using var anonymous = api.Anonymous();
        var download = await anonymous.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var disposition = download.Content.Headers.ContentDisposition!;
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.EndsWith(".txt", disposition.FileNameStar);
        Assert.Single(download.Content.Headers.GetValues("Content-Disposition"));
        Assert.DoesNotContain(disposition.Parameters, p => p.Name == "x");
    }

    [Fact]
    public async Task Audit_log_is_sealed_and_tampering_is_detected()
    {
        using var admin = api.ClientFor(api.NewUser(), "mode-admin");
        await ApiFixture.CreateWorkspaceAsync(admin, "Sealed");

        var first = await admin.PostJsonOkAsync("/api/v1/admin/audit/verify", new { });
        Assert.True(first.GetProperty("intact").GetBoolean());
        Assert.True(first.GetProperty("checked").GetInt64() > 0);

        await using var db = api.CreateDbContext();
        var target = await db.AuditLogs.Where(a => a.Hash != null).OrderByDescending(a => a.Id).FirstAsync();
        // Sealing is the only update allowed, and only once.
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            db.AuditLogs.Where(a => a.Id == target.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Hash, new string('0', 64))));

        // A database superuser can switch the trigger off; the chain still exposes the edit.
        async Task SetActionAsync(string action) => await db.Database.ExecuteSqlInterpolatedAsync($"""
            ALTER TABLE "AuditLogs" DISABLE TRIGGER audit_logs_no_change;
            UPDATE "AuditLogs" SET "Action" = {action} WHERE "Id" = {target.Id};
            ALTER TABLE "AuditLogs" ENABLE TRIGGER audit_logs_no_change;
            """);
        await SetActionAsync("workspace.tampered");
        try
        {
            var broken = await admin.PostJsonOkAsync("/api/v1/admin/audit/verify", new { });
            Assert.False(broken.GetProperty("intact").GetBoolean());
            Assert.Equal(target.Id, broken.GetProperty("brokenAtId").GetInt64());
        }
        finally
        {
            await SetActionAsync(target.Action);
        }
        Assert.True((await admin.PostJsonOkAsync("/api/v1/admin/audit/verify", new { })).GetProperty("intact").GetBoolean());
    }
}

public sealed class SearchTimeoutTests
{
    [Fact]
    public async Task Slow_searches_are_cut_off_but_caller_cancellation_still_propagates()
    {
        var slow = await Mode.Api.Endpoints.SearchEndpoints.WithTimeoutAsync(
            async ct => { await Task.Delay(TimeSpan.FromSeconds(10), ct); return "done"; }, TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.Null(slow);

        var fast = await Mode.Api.Endpoints.SearchEndpoints.WithTimeoutAsync(
            ct => Task.FromResult("done"), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("done", fast);

        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Mode.Api.Endpoints.SearchEndpoints.WithTimeoutAsync(
            async ct => { await Task.Delay(TimeSpan.FromSeconds(10), ct); return "done"; }, TimeSpan.FromSeconds(5), caller.Token));
    }
}
