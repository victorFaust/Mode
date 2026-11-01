using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Mode.Api.Tests.Infrastructure;

namespace Mode.Api.Tests.Endpoints;

/// <summary>A user of one workspace must not reach another workspace's data by guessing ids.</summary>
[Collection(ApiCollection.Name)]
public sealed class CrossWorkspaceTests(ApiFixture api)
{
    [Fact]
    public async Task Guessed_ids_from_another_workspace_return_not_found_and_change_nothing()
    {
        var victimId = api.NewUser();
        using var victim = api.ClientFor(victimId);
        var workspaceId = await ApiFixture.CreateWorkspaceAsync(victim, "Victim");
        var channelId = await ApiFixture.CreateChannelAsync(victim, workspaceId, "public");
        var privateId = await ApiFixture.CreateChannelAsync(victim, workspaceId, "private", "Private");

        var attackerId = api.NewUser();
        using var attacker = api.ClientFor(attackerId);
        await ApiFixture.CreateWorkspaceAsync(attacker, "Attacker");

        var attempts = new List<(string Name, Func<Task<HttpResponseMessage>> Send)>
        {
            ("get workspace", () => attacker.GetAsync($"/api/v1/workspaces/{workspaceId}")),
            ("rename workspace", () => attacker.PatchAsJsonAsync($"/api/v1/workspaces/{workspaceId}", new { name = "pwned" })),
            ("list members", () => attacker.GetAsync($"/api/v1/workspaces/{workspaceId}/members")),
            ("add self as owner", () => attacker.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/members", new { userId = attackerId, role = "Owner" })),
            ("demote owner", () => attacker.PatchAsJsonAsync($"/api/v1/workspaces/{workspaceId}/members/{victimId}", new { role = "Guest" })),
            ("remove owner", () => attacker.DeleteAsync($"/api/v1/workspaces/{workspaceId}/members/{victimId}")),
            ("list channels", () => attacker.GetAsync($"/api/v1/workspaces/{workspaceId}/channels")),
            ("create channel", () => attacker.PostAsJsonAsync($"/api/v1/workspaces/{workspaceId}/channels", new { name = "x" })),
        };
        foreach (var id in new[] { channelId, privateId })
        {
            attempts.Add(($"get channel {id}", () => attacker.GetAsync($"/api/v1/channels/{id}")));
            attempts.Add(($"rename channel {id}", () => attacker.PatchAsJsonAsync($"/api/v1/channels/{id}", new { name = "pwned" })));
            attempts.Add(($"archive channel {id}", () => attacker.PostAsJsonAsync($"/api/v1/channels/{id}/archive", new { })));
            attempts.Add(($"list channel members {id}", () => attacker.GetAsync($"/api/v1/channels/{id}/members")));
            attempts.Add(($"join channel {id}", () => attacker.PostAsJsonAsync($"/api/v1/channels/{id}/members", new { userId = attackerId })));
            attempts.Add(($"remove channel member {id}", () => attacker.DeleteAsync($"/api/v1/channels/{id}/members/{victimId}")));
        }
        attempts.Add(("unknown channel", () => attacker.GetAsync($"/api/v1/channels/{Guid.NewGuid()}")));

        var failures = new List<string>();
        foreach (var (name, send) in attempts)
        {
            var status = (await send()).StatusCode;
            if (status != HttpStatusCode.NotFound) failures.Add($"{name}: {(int)status}");
        }
        Assert.Empty(failures);

        await using var db = api.CreateDbContext();
        Assert.Equal("Victim", (await db.Workspaces.SingleAsync(w => w.Id == workspaceId)).Name);
        Assert.False(await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == workspaceId && m.UserId == attackerId));
        Assert.False(await db.ChannelMembers.AnyAsync(m => m.UserId == attackerId));
        Assert.Equal(3, await db.Channels.CountAsync(c => c.WorkspaceId == workspaceId && c.ArchivedAt == null));
    }
}
