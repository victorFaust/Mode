using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mode.Api.Files;
using Mode.Api.Jobs;
using Mode.Api.Tests.Infrastructure;
using Mode.Domain.Entities;
using Mode.Domain.Files;

namespace Mode.Api.Tests.Files;

[Collection(ApiCollection.Name)]
public sealed class FileApiTests(ApiFixture api)
{
    private sealed record Team(
        HttpClient Owner, HttpClient Member, HttpClient Member2, string OwnerId, string MemberId, string Member2Id, Guid WorkspaceId, Guid GeneralId);

    private async Task<Team> CreateTeamAsync()
    {
        var ownerId = api.NewUser("Owner");
        var memberId = api.NewUser("Member");
        var member2Id = api.NewUser("Member Two");
        var owner = api.ClientFor(ownerId);
        var workspaceId = await ApiFixture.CreateWorkspaceAsync(owner);
        await ApiFixture.AddMemberAsync(owner, workspaceId, memberId, "Member");
        await ApiFixture.AddMemberAsync(owner, workspaceId, member2Id, "Member");
        var generalId = (await owner.GetJsonAsync($"/api/v1/workspaces/{workspaceId}/channels"))[0].GetProperty("id").GetGuid();
        return new Team(owner, api.ClientFor(memberId), api.ClientFor(member2Id), ownerId, memberId, member2Id, workspaceId, generalId);
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private Task<HttpResponseMessage> StartAsync(HttpClient client, Guid channelId, string name, long size) =>
        client.PostAsJsonAsync("/api/v1/files/uploads", new { room = new { type = "Channel", id = channelId }, name, size });

    [Fact]
    public async Task Upload_is_scanned_before_it_can_be_downloaded()
    {
        var team = await CreateTeamAsync();
        var file = await team.Member.UploadFileAsync("Channel", team.GeneralId, "notes.txt", Utf8("meeting notes"));
        var fileId = file.GetProperty("id").GetGuid();

        Assert.Equal("Pending", file.GetProperty("current").GetProperty("scanStatus").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await team.Member2.LinkAsync(fileId)).StatusCode);

        await api.RunJobsAsync();

        var scanned = await team.Member2.GetJsonAsync($"/api/v1/files/{fileId}");
        Assert.Equal("Clean", scanned.GetProperty("current").GetProperty("scanStatus").GetString());
        Assert.Equal("Ready", scanned.GetProperty("current").GetProperty("previewStatus").GetString());

        var url = await team.Member2.LinkUrlAsync(fileId);
        using var anonymous = api.Anonymous();
        var download = await anonymous.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("meeting notes", await download.Content.ReadAsStringAsync());
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("notes.txt", download.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("sandbox", download.Headers.GetValues("Content-Security-Policy").Single());

        await using var db = api.CreateDbContext();
        var version = await db.FileVersions.SingleAsync(v => v.FileAssetId == fileId);
        Assert.Equal("meeting notes", version.ExtractedText);
        Assert.Equal(64, version.Sha256!.Length);
    }

    [Fact]
    public async Task Images_get_a_thumbnail_served_inline()
    {
        var team = await CreateTeamAsync();
        var fileId = (await team.Member.UploadFileAsync("Channel", team.GeneralId, "pic.png", TestFiles.TinyPng)).GetProperty("id").GetGuid();
        await api.RunJobsAsync();

        var thumbnail = await api.Anonymous().GetAsync(await team.Member.LinkUrlAsync(fileId, "variant=thumbnail"));

        Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
        Assert.Equal("image/png", thumbnail.Content.Headers.ContentType?.MediaType);
        Assert.Null(thumbnail.Content.Headers.ContentDisposition);
        Assert.Equal(HttpStatusCode.NotFound, (await team.Member.LinkAsync(fileId, "variant=preview")).StatusCode);
    }

    [Fact]
    public async Task Same_name_in_the_same_room_adds_a_version()
    {
        var team = await CreateTeamAsync();
        var first = await team.Member.UploadFileAsync("Channel", team.GeneralId, "report.txt", Utf8("version one"));
        var second = await team.Owner.UploadFileAsync("Channel", team.GeneralId, "Report.txt", Utf8("version two"));
        await api.RunJobsAsync();

        var fileId = first.GetProperty("id").GetGuid();
        Assert.Equal(fileId, second.GetProperty("id").GetGuid());
        Assert.Equal(2, second.GetProperty("currentVersionNo").GetInt32());
        var versions = await team.Member2.GetJsonAsync($"/api/v1/files/{fileId}/versions");
        Assert.Equal([2, 1], versions.EnumerateArray().Select(v => v.GetProperty("versionNo").GetInt32()));

        using var anonymous = api.Anonymous();
        Assert.Equal("version one", await anonymous.GetStringAsync(await team.Member2.LinkUrlAsync(fileId, "version=1")));
        Assert.Equal("version two", await anonymous.GetStringAsync(await team.Member2.LinkUrlAsync(fileId)));
        Assert.Single((await team.Member.GetJsonAsync($"/api/v1/channels/{team.GeneralId}/files")).EnumerateArray());
    }

    [Fact]
    public async Task Only_people_who_can_read_the_room_can_reach_its_files()
    {
        var team = await CreateTeamAsync();
        var privateId = await ApiFixture.CreateChannelAsync(team.Owner, team.WorkspaceId, "board", "Private");
        await team.Owner.PostJsonOkAsync($"/api/v1/channels/{privateId}/members", new { userId = team.MemberId });
        var fileId = (await team.Member.UploadFileAsync("Channel", privateId, "minutes.txt", Utf8("secret"))).GetProperty("id").GetGuid();
        await api.RunJobsAsync();
        using var outsider = api.ClientFor(api.NewUser());

        foreach (var client in new[] { team.Member2, outsider })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/files/{fileId}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/files/{fileId}/versions")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.LinkAsync(fileId)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/channels/{privateId}/files")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/v1/files/{fileId}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await StartAsync(client, privateId, "x.txt", 1)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, (await team.Owner.LinkAsync(fileId)).StatusCode);
    }

    [Fact]
    public async Task Chat_files_are_only_for_participants()
    {
        var aId = api.NewUser();
        var bId = api.NewUser();
        using var a = api.ClientFor(aId);
        using var outsider = api.ClientFor(api.NewUser());
        var chat = await a.PostJsonOkAsync("/api/v1/conversations", new { type = "Direct", userId = bId });
        var chatId = chat.GetProperty("id").GetGuid();

        var fileId = (await a.UploadFileAsync("Conversation", chatId, "photo.png", TestFiles.TinyPng)).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await api.ClientFor(bId).GetAsync($"/api/v1/files/{fileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/v1/files/{fileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/v1/conversations/{chatId}/files")).StatusCode);
    }

    [Fact]
    public async Task Size_type_and_name_limits_are_enforced()
    {
        var team = await CreateTeamAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(team.Member, team.GeneralId, "big.pdf", 2 * 1024 * 1024)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(team.Member, team.GeneralId, "tool.exe", 10)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(team.Member, team.GeneralId, "page.html", 10)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(team.Member, team.GeneralId, "CON.txt", 10)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await StartAsync(team.Member, team.GeneralId, "zero.txt", 0)).StatusCode);

        var tooMuch = await team.Member.PostJsonOkAsync("/api/v1/files/uploads", new { room = new { type = "Channel", id = team.GeneralId }, name = "a.txt", size = 5 });
        var overflow = await team.Member.PutAsync(tooMuch.GetProperty("uploadUrl").GetString(), new ByteArrayContent(new byte[50]));
        Assert.Equal(HttpStatusCode.BadRequest, overflow.StatusCode);

        var tooLittle = await team.Member.PostJsonOkAsync("/api/v1/files/uploads", new { room = new { type = "Channel", id = team.GeneralId }, name = "b.txt", size = 50 });
        var underflow = await team.Member.PutAsync(tooLittle.GetProperty("uploadUrl").GetString(), new ByteArrayContent(new byte[5]));
        Assert.Equal(HttpStatusCode.BadRequest, underflow.StatusCode);

        var empty = await team.Member.PostJsonOkAsync("/api/v1/files/uploads", new { room = new { type = "Channel", id = team.GeneralId }, name = "c.txt", size = 5 });
        var completeEarly = await team.Member.PostAsJsonAsync($"/api/v1/files/uploads/{empty.GetProperty("uploadId").GetGuid()}/complete", new { });
        Assert.Equal(HttpStatusCode.BadRequest, completeEarly.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await team.Member2.PutAsync(empty.GetProperty("uploadUrl").GetString(), new ByteArrayContent(new byte[5]))).StatusCode);
    }

    [Fact]
    public async Task Expired_upload_sessions_are_refused()
    {
        var team = await CreateTeamAsync();
        var start = await team.Member.PostJsonOkAsync("/api/v1/files/uploads", new { room = new { type = "Channel", id = team.GeneralId }, name = "late.txt", size = 4 });
        await using (var db = api.CreateDbContext())
            await db.UploadSessions.Where(u => u.Id == start.GetProperty("uploadId").GetGuid())
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));

        var put = await team.Member.PutAsync(start.GetProperty("uploadUrl").GetString(), new ByteArrayContent(Utf8("late")));

        Assert.Equal(HttpStatusCode.Gone, put.StatusCode);
    }

    [Theory]
    [InlineData("photo.png", "just some text pretending to be an image", "Blocked")]
    [InlineData("readme.txt", TestFiles.Eicar, "Infected")]
    public async Task Disguised_or_infected_files_are_blocked_and_removed(string name, string content, string expectedStatus)
    {
        var team = await CreateTeamAsync();
        var fileId = (await team.Member.UploadFileAsync("Channel", team.GeneralId, name, Utf8(content))).GetProperty("id").GetGuid();

        await api.RunJobsAsync();

        var file = await team.Member.GetJsonAsync($"/api/v1/files/{fileId}");
        Assert.Equal(expectedStatus, file.GetProperty("current").GetProperty("scanStatus").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await team.Member.LinkAsync(fileId)).StatusCode);

        await using var db = api.CreateDbContext();
        var key = await db.FileVersions.Where(v => v.FileAssetId == fileId).Select(v => v.ObjectKey).SingleAsync();
        Assert.Null(await api.Factory.Services.GetRequiredService<IFileStore>().GetSizeAsync(key, default));
    }

    [Fact]
    public async Task Expired_tampered_and_misused_links_are_refused()
    {
        var team = await CreateTeamAsync();
        var fileId = (await team.Member.UploadFileAsync("Channel", team.GeneralId, "doc.txt", Utf8("hello"))).GetProperty("id").GetGuid();
        await api.RunJobsAsync();
        await using var db = api.CreateDbContext();
        var versionId = await db.FileVersions.Where(v => v.FileAssetId == fileId).Select(v => v.Id).SingleAsync();
        var signer = api.Factory.Services.GetRequiredService<FileLinkSigner>();
        using var anonymous = api.Anonymous();

        var expired = signer.Create(new FileLink(LinkPurpose.Download, versionId, FileVariant.Original, DateTimeOffset.UtcNow.AddSeconds(-5)));
        var processorToken = signer.Create(LinkPurpose.ProcessorRead, versionId, FileVariant.Original, TimeSpan.FromMinutes(5));
        var valid = (await team.Member.LinkUrlAsync(fileId)).Split('/')[^1];
        var tampered = valid[..^2] + (valid[^2] == 'A' ? "B" : "A") + valid[^1];

        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync($"/api/v1/files/content/{expired}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync($"/api/v1/files/content/{tampered}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync($"/api/v1/files/content/{processorToken}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync($"/internal/files/{valid}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/v1/files/content/{valid}")).StatusCode);
    }

    [Fact]
    public async Task Processor_can_only_store_png_previews()
    {
        var team = await CreateTeamAsync();
        var fileId = (await team.Member.UploadFileAsync("Channel", team.GeneralId, "x.txt", Utf8("x"))).GetProperty("id").GetGuid();
        await using var db = api.CreateDbContext();
        var version = await db.FileVersions.SingleAsync(v => v.FileAssetId == fileId);
        await db.FileVersions.Where(v => v.Id == version.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.ScanStatus, ScanStatus.Clean));
        var write = api.Factory.Services.GetRequiredService<FileLinkSigner>()
            .Create(LinkPurpose.ProcessorWrite, version.Id, FileVariant.Thumbnail, TimeSpan.FromMinutes(5));

        var response = await api.Anonymous().PutAsync($"/internal/files/{write}", new ByteArrayContent(Utf8("<html>not an image</html>")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Attachments_must_come_from_the_same_room_and_survive_message_deletion()
    {
        var team = await CreateTeamAsync();
        var otherChannel = await ApiFixture.CreateChannelAsync(team.Owner, team.WorkspaceId, "other");
        var fileId = (await team.Member.UploadFileAsync("Channel", team.GeneralId, "plan.txt", Utf8("plan"))).GetProperty("id").GetGuid();

        var wrongRoom = await team.Member.PostAsJsonAsync($"/api/v1/channels/{otherChannel}/messages",
            new { id = Guid.NewGuid(), bodyMarkdown = "see file", fileIds = new[] { fileId } });
        var message = await team.Member.PostJsonOkAsync($"/api/v1/channels/{team.GeneralId}/messages",
            new { id = Guid.NewGuid(), bodyMarkdown = "see file", fileIds = new[] { fileId } });

        Assert.Equal(HttpStatusCode.BadRequest, wrongRoom.StatusCode);
        var attached = Assert.Single(message.GetProperty("files").EnumerateArray());
        Assert.Equal("plan.txt", attached.GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await team.Member.DeleteAsync($"/api/v1/messages/{message.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await team.Member2.GetAsync($"/api/v1/files/{fileId}")).StatusCode);
        Assert.Single((await team.Member2.GetJsonAsync($"/api/v1/channels/{team.GeneralId}/files")).EnumerateArray());
    }

    [Fact]
    public async Task Deleted_files_can_be_restored_then_are_purged_after_the_recovery_window()
    {
        var team = await CreateTeamAsync();
        var fileId = (await team.Member.UploadFileAsync("Channel", team.GeneralId, "old.txt", Utf8("old"))).GetProperty("id").GetGuid();
        await api.RunJobsAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await team.Member2.DeleteAsync($"/api/v1/files/{fileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await team.Member.DeleteAsync($"/api/v1/files/{fileId}")).StatusCode);
        Assert.Empty((await team.Member.GetJsonAsync($"/api/v1/channels/{team.GeneralId}/files")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await team.Member2.GetAsync($"/api/v1/files/{fileId}")).StatusCode);
        Assert.Single((await team.Member.GetJsonAsync($"/api/v1/channels/{team.GeneralId}/files?deleted=true")).EnumerateArray());
        Assert.Empty((await team.Member2.GetJsonAsync($"/api/v1/channels/{team.GeneralId}/files?deleted=true")).EnumerateArray());

        Assert.Equal(HttpStatusCode.NotFound, (await team.Member2.PostAsync($"/api/v1/files/{fileId}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await team.Member.PostAsync($"/api/v1/files/{fileId}/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await team.Member2.GetAsync($"/api/v1/files/{fileId}")).StatusCode);

        await team.Owner.DeleteAsync($"/api/v1/files/{fileId}");
        await using var db = api.CreateDbContext();
        var key = await db.FileVersions.Where(v => v.FileAssetId == fileId).Select(v => v.ObjectKey).SingleAsync();
        await db.FileAssets.Where(f => f.Id == fileId).ExecuteUpdateAsync(s => s.SetProperty(f => f.DeletedAt, DateTimeOffset.UtcNow.AddDays(-31)));
        await using (var scope = api.Factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<JobQueue>().Enqueue(JobTypes.FilesPurge, new { });
            await scope.ServiceProvider.GetRequiredService<Mode.Infrastructure.Data.ModeDbContext>().SaveChangesAsync();
        }
        await api.RunJobsAsync();

        Assert.False(await db.FileAssets.AnyAsync(f => f.Id == fileId));
        Assert.Null(await api.Factory.Services.GetRequiredService<IFileStore>().GetSizeAsync(key, default));
    }

    [Fact]
    public async Task File_updates_reach_realtime_listeners()
    {
        var team = await CreateTeamAsync();
        await using var listener = await RealtimeClient.ConnectAsync(api, team.Member2Id);
        await listener.JoinAsync("Channel", team.GeneralId);

        await team.Member.UploadFileAsync("Channel", team.GeneralId, "live.txt", Utf8("live"));
        await api.RunJobsAsync();

        await listener.WaitForAsync(Mode.Api.Realtime.RealtimeEvents.FileUpdated,
            e => e.GetProperty("file").GetProperty("current").GetProperty("scanStatus").GetString() == "Clean");
    }
}
