using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Mode.Infrastructure.Directory;

namespace Mode.Api.Tests.Directory;

/// <summary>Runs only when MODE_TEST_HOSTDIR_CONNECTION points at a database seeded by db/host-mock/setup.sql.</summary>
public sealed class HostDirectoryFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "MODE_TEST_HOSTDIR_CONNECTION";

    public HostDirectoryFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)))
            Skip = $"{EnvironmentVariable} is not set.";
    }
}

public sealed class SqlServerUserDirectoryTests
{
    private const string Ada = "0a1b0001-0000-4000-8000-000000000001";
    private const string Bola = "0a1b0001-0000-4000-8000-000000000002";
    private const string InactiveIfeoma = "0a1b0001-0000-4000-8000-000000000009";

    private static SqlServerUserDirectory CreateDirectory() => new(
        Options.Create(new UserDirectoryOptions
        {
            ConnectionString = Environment.GetEnvironmentVariable(HostDirectoryFactAttribute.EnvironmentVariable)!,
        }),
        new MemoryCache(new MemoryCacheOptions()));

    [HostDirectoryFact]
    public async Task GetById_returns_seeded_user()
    {
        var user = await CreateDirectory().GetByIdAsync(Ada);

        Assert.NotNull(user);
        Assert.Equal(Ada, user.Id);
        Assert.Equal("Ada Okafor", user.DisplayName);
        Assert.Equal("ada.okafor@uba.example", user.Email);
        Assert.True(user.IsActive);
    }

    [HostDirectoryFact]
    public async Task GetById_normalizes_guid_casing() =>
        Assert.Equal(Ada, (await CreateDirectory().GetByIdAsync(Ada.ToUpperInvariant()))?.Id);

    [HostDirectoryFact]
    public async Task GetById_returns_null_for_unknown_or_malformed_ids()
    {
        var directory = CreateDirectory();

        Assert.Null(await directory.GetByIdAsync(Guid.NewGuid().ToString()));
        Assert.Null(await directory.GetByIdAsync("not-a-guid' OR 1=1 --"));
    }

    [HostDirectoryFact]
    public async Task GetMany_returns_only_found_users()
    {
        var users = await CreateDirectory().GetManyAsync([Ada, Bola, Guid.NewGuid().ToString(), "garbage"]);

        Assert.Equal([Ada, Bola], users.Keys.Order().ToArray());
    }

    [HostDirectoryFact]
    public async Task IsActive_is_false_for_inactive_user() =>
        Assert.False(await CreateDirectory().IsActiveAsync(InactiveIfeoma));

    [HostDirectoryFact]
    public async Task Search_matches_name_or_email_and_excludes_inactive_users()
    {
        var directory = CreateDirectory();

        Assert.Contains(await directory.SearchAsync("okafor", 10), u => u.Id == Ada);
        Assert.Contains(await directory.SearchAsync("bola.adeyemi@", 10), u => u.Id == Bola);
        Assert.DoesNotContain(await directory.SearchAsync("Ifeoma", 10), u => u.Id == InactiveIfeoma);
    }

    [HostDirectoryFact]
    public async Task Search_treats_wildcards_as_literals() =>
        Assert.Empty(await CreateDirectory().SearchAsync("%", 10));
}
