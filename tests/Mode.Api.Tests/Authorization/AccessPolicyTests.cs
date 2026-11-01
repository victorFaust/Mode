using Mode.Api.Auth;
using Mode.Api.Authorization;
using Mode.Api.Tests.Infrastructure;
using Mode.Domain.Entities;
using Mode.Infrastructure.Data;

namespace Mode.Api.Tests.Authorization;

/// <summary>The authorization matrix: every role against every action on standard, private and archived channels.</summary>
[Collection(ApiCollection.Name)]
public sealed class AccessPolicyTests(ApiFixture api)
{
    private sealed class World
    {
        public required Dictionary<string, CurrentUser> Users { get; init; }
        public required Dictionary<string, Guid> Channels { get; init; }
        public required Guid WorkspaceId { get; init; }
    }

    private static CurrentUser Person(string id, bool hostGuest = false) => new(id, id, null, false, hostGuest);

    private async Task<World> SeedAsync(ModeDbContext db)
    {
        var users = new[] { "owner", "member", "member2", "guest", "outsider" }
            .ToDictionary(name => name, name => Person(api.NewUser(name)));
        var now = DateTimeOffset.UtcNow;
        string Id(string name) => users[name].Id;

        var workspace = new Workspace { Id = Guid.NewGuid(), Name = "Matrix", CreatedByUserId = Id("owner"), CreatedAt = now };
        foreach (var (name, role) in new[] { ("owner", WorkspaceRole.Owner), ("member", WorkspaceRole.Member), ("member2", WorkspaceRole.Member), ("guest", WorkspaceRole.Guest) })
            workspace.Members.Add(new WorkspaceMember { UserId = Id(name), Role = role, AddedByUserId = Id("owner"), AddedAt = now });

        Channel NewChannel(string name, ChannelType type, string creator, bool archived, params string[] members)
        {
            var channel = new Channel
            {
                Id = Guid.NewGuid(), Name = name, NormalizedName = name, Type = type, CreatedByUserId = Id(creator), CreatedAt = now,
                ArchivedAt = archived ? now : null,
            };
            foreach (var member in members)
                channel.Members.Add(new ChannelMember { UserId = Id(member), AddedByUserId = Id(creator), AddedAt = now });
            workspace.Channels.Add(channel);
            return channel;
        }

        var channels = new Dictionary<string, Guid>
        {
            ["standard"] = NewChannel("standard", ChannelType.Standard, "owner", false).Id,
            ["standard-with-guest"] = NewChannel("standard-with-guest", ChannelType.Standard, "member", false, "guest").Id,
            ["private"] = NewChannel("private", ChannelType.Private, "owner", false, "owner", "member").Id,
            ["private-by-member"] = NewChannel("private-by-member", ChannelType.Private, "member", false, "member").Id,
            ["archived"] = NewChannel("archived", ChannelType.Standard, "owner", true).Id,
        };

        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        return new World { Users = users, Channels = channels, WorkspaceId = workspace.Id };
    }

    private static string Describe(Permission p)
    {
        var text = (p.Has(Permission.Read) ? "R" : "") + (p.Has(Permission.Post) ? "P" : "") + (p.Has(Permission.Manage) ? "M" : "");
        return text.Length == 0 ? "-" : text;
    }

    [Fact]
    public async Task Channel_permission_matrix()
    {
        await using var db = api.CreateDbContext();
        var world = await SeedAsync(db);
        var policy = new AccessPolicy(db);

        // R = read, P = post, M = manage (rename, archive, membership).
        var expected = new Dictionary<string, Dictionary<string, string>>
        {
            ["standard"] = new() { ["owner"] = "RPM", ["member"] = "RP", ["member2"] = "RP", ["guest"] = "-", ["outsider"] = "-" },
            ["standard-with-guest"] = new() { ["owner"] = "RPM", ["member"] = "RPM", ["member2"] = "RP", ["guest"] = "RP", ["outsider"] = "-" },
            ["private"] = new() { ["owner"] = "RPM", ["member"] = "RP", ["member2"] = "-", ["guest"] = "-", ["outsider"] = "-" },
            ["private-by-member"] = new() { ["owner"] = "M", ["member"] = "RPM", ["member2"] = "-", ["guest"] = "-", ["outsider"] = "-" },
            ["archived"] = new() { ["owner"] = "RM", ["member"] = "R", ["member2"] = "R", ["guest"] = "-", ["outsider"] = "-" },
        };

        var failures = new List<string>();
        foreach (var (channel, row) in expected)
        foreach (var (actor, want) in row)
        {
            var got = Describe(await policy.ForChannelAsync(world.Users[actor], world.Channels[channel], default));
            if (got != want) failures.Add($"{actor} on {channel}: expected {want}, got {got}");
        }

        Assert.Empty(failures);
    }

    [Fact]
    public async Task Workspace_permissions_by_role()
    {
        await using var db = api.CreateDbContext();
        var world = await SeedAsync(db);
        var policy = new AccessPolicy(db);

        async Task<Permission> For(string actor) => await policy.ForWorkspaceAsync(world.Users[actor], world.WorkspaceId, default);

        Assert.Equal(Permission.Read | Permission.Manage | Permission.CreateChannel | Permission.ViewMembers, await For("owner"));
        Assert.Equal(Permission.Read | Permission.CreateChannel | Permission.ViewMembers, await For("member"));
        Assert.Equal(Permission.Read, await For("guest"));
        Assert.Equal(Permission.None, await For("outsider"));
    }

    [Fact]
    public async Task Channel_membership_without_workspace_membership_grants_nothing()
    {
        await using var db = api.CreateDbContext();
        var world = await SeedAsync(db);
        var outsider = world.Users["outsider"];
        db.ChannelMembers.Add(new ChannelMember
        {
            ChannelId = world.Channels["private"], UserId = outsider.Id, AddedByUserId = outsider.Id, AddedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.Equal(Permission.None, await new AccessPolicy(db).ForChannelAsync(outsider, world.Channels["private"], default));
    }

    [Fact]
    public async Task Host_flagged_guest_is_treated_as_guest_whatever_the_stored_role()
    {
        await using var db = api.CreateDbContext();
        var world = await SeedAsync(db);
        var policy = new AccessPolicy(db);
        var memberAsHostGuest = Person(world.Users["member"].Id, hostGuest: true);

        Assert.Equal(Permission.None, await policy.ForChannelAsync(memberAsHostGuest, world.Channels["standard"], default));
        Assert.Equal(Permission.Read, await policy.ForWorkspaceAsync(memberAsHostGuest, world.WorkspaceId, default));
        Assert.False(policy.CanCreateWorkspace(memberAsHostGuest));
        Assert.False(await policy.CanUseDirectoryAsync(memberAsHostGuest, default));
    }

    [Fact]
    public async Task Directory_is_closed_to_guests_only()
    {
        await using var db = api.CreateDbContext();
        var world = await SeedAsync(db);
        var policy = new AccessPolicy(db);

        Assert.True(await policy.CanUseDirectoryAsync(world.Users["member"], default));
        Assert.True(await policy.CanUseDirectoryAsync(world.Users["outsider"], default));
        Assert.False(await policy.CanUseDirectoryAsync(world.Users["guest"], default));
    }

    [Fact]
    public async Task Conversation_access_requires_membership()
    {
        await using var db = api.CreateDbContext();
        var a = Person(api.NewUser());
        var b = Person(api.NewUser());
        var outsider = Person(api.NewUser());
        var now = DateTimeOffset.UtcNow;
        var direct = new Conversation
        {
            Id = Guid.NewGuid(), Type = ConversationType.Direct, DirectKey = string.Join('|', new[] { a.Id, b.Id }.Order(StringComparer.Ordinal)),
            CreatedByUserId = a.Id, CreatedAt = now,
        };
        var group = new Conversation { Id = Guid.NewGuid(), Type = ConversationType.Group, Name = "Group", CreatedByUserId = a.Id, CreatedAt = now };
        foreach (var conversation in new[] { direct, group })
        foreach (var user in new[] { a, b })
            conversation.Members.Add(new ConversationMember { UserId = user.Id, AddedAt = now });
        db.Conversations.AddRange(direct, group);
        await db.SaveChangesAsync();
        var policy = new AccessPolicy(db);

        Assert.Equal(Permission.Read | Permission.Post | Permission.ViewMembers, await policy.ForConversationAsync(b, direct.Id, default));
        Assert.True((await policy.ForConversationAsync(b, group.Id, default)).Has(Permission.Manage));
        Assert.Equal(Permission.None, await policy.ForConversationAsync(outsider, direct.Id, default));
        Assert.Equal(Permission.None, await policy.ForConversationAsync(a, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Message_and_file_access_follow_the_room()
    {
        await using var db = api.CreateDbContext();
        var world = await SeedAsync(db);
        var now = DateTimeOffset.UtcNow;
        var channelId = world.Channels["private"];
        var author = world.Users["member"];
        var message = new Message
        {
            Id = Guid.NewGuid(), ChannelId = channelId, Sequence = 1, AuthorUserId = author.Id, BodyMarkdown = "hello", CreatedAt = now,
        };
        var deleted = new Message
        {
            Id = Guid.NewGuid(), ChannelId = channelId, Sequence = 2, AuthorUserId = author.Id, BodyMarkdown = "", CreatedAt = now, DeletedAt = now,
        };
        var file = new FileAsset
        {
            Id = Guid.NewGuid(), ChannelId = channelId, Name = "a.txt", NormalizedName = "a.txt", CurrentVersionNo = 1,
            CreatedByUserId = author.Id, CreatedAt = now,
        };
        db.Messages.AddRange(message, deleted);
        db.FileAssets.Add(file);
        await db.SaveChangesAsync();
        var policy = new AccessPolicy(db);

        Assert.Equal(Permission.Read | Permission.Post | Permission.Edit | Permission.Delete, await policy.ForMessageAsync(author, message.Id, default));
        Assert.Equal(Permission.Read | Permission.Post | Permission.Delete, await policy.ForMessageAsync(world.Users["owner"], message.Id, default));
        Assert.Equal(Permission.None, await policy.ForMessageAsync(world.Users["member2"], message.Id, default));
        Assert.Equal(Permission.Read | Permission.Post, await policy.ForMessageAsync(author, deleted.Id, default));

        Assert.Equal(Permission.Read | Permission.Delete, await policy.ForFileAsync(author, file.Id, default));
        Assert.Equal(Permission.Read | Permission.Delete, await policy.ForFileAsync(world.Users["owner"], file.Id, default));
        Assert.Equal(Permission.None, await policy.ForFileAsync(world.Users["member2"], file.Id, default));
        Assert.Equal(Permission.None, await policy.ForFileAsync(world.Users["guest"], file.Id, default));
    }
}
