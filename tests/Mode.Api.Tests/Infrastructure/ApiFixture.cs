using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mode.Api.Files.Processing;
using Mode.Api.Files.Scanning;
using Mode.Api.Jobs;
using Mode.Api.Notifications;
using Mode.Domain.Directory;
using Mode.Infrastructure.Data;
using Testcontainers.PostgreSql;

namespace Mode.Api.Tests.Infrastructure;

/// <summary>One PostgreSQL container and one API host shared by every test in the "api" collection.</summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly FakeUserDirectory _directory = new();
    private readonly string _storagePath = Path.Combine(Path.GetTempPath(), "mode-tests-" + Guid.NewGuid().ToString("N"));

    internal ApiFactory Factory { get; private set; } = null!;
    internal FakePreviewProcessor Processor { get; } = new();
    internal FakePushSender Push { get; } = new();
    internal FakeEmailSender Email { get; } = new();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using (var db = CreateDbContext())
            await db.Database.MigrateAsync();

        Factory = new ApiFactory(_postgres.GetConnectionString(), _directory, _storagePath, Processor, Push, Email);
        Factory.CreateClient().Dispose();
        Processor.ClientFactory = () => Factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
        if (System.IO.Directory.Exists(_storagePath)) System.IO.Directory.Delete(_storagePath, recursive: true);
    }

    /// <summary>The background worker is off in tests; this runs every due job now.</summary>
    public Task<int> RunJobsAsync() => Factory.Services.GetRequiredService<JobRunner>().RunAllDueAsync();

    /// <summary>Runs the periodic check that disconnects people deactivated in the host directory.</summary>
    public Task<int> CheckRealtimeSessionsAsync() =>
        Factory.Services.GetRequiredService<Mode.Api.Realtime.RealtimeSessionCheck>().RunAsync(CancellationToken.None);

    public ModeDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<ModeDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options);

    /// <summary>Adds a person to the fake host directory; every test uses fresh users.</summary>
    public string NewUser(string displayName = "Test User", bool active = true)
    {
        var id = "u-" + Guid.NewGuid().ToString("N");
        _directory.Add(id, displayName, active);
        return id;
    }

    public void SetActive(string userId, bool active) => _directory.SetActive(userId, active);

    public HttpClient Anonymous() => Factory.CreateClient();

    public HttpClient ClientFor(string userId, params string[] hostRoles) => WithToken(TestAuth.CreateToken(userId, hostRoles));

    public HttpClient WithToken(string token)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static async Task<Guid> CreateWorkspaceAsync(HttpClient owner, string name = "Team")
    {
        var created = await owner.PostJsonOkAsync("/api/v1/workspaces", new { name });
        return created.GetProperty("id").GetGuid();
    }

    public static Task AddMemberAsync(HttpClient owner, Guid workspaceId, string userId, string role) =>
        owner.PostJsonOkAsync($"/api/v1/workspaces/{workspaceId}/members", new { userId, role });

    public static async Task<Guid> CreateChannelAsync(HttpClient client, Guid workspaceId, string name, string type = "Standard")
    {
        var created = await client.PostJsonOkAsync($"/api/v1/workspaces/{workspaceId}/channels", new { name, type });
        return created.GetProperty("id").GetGuid();
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}

internal sealed class ApiFactory(
    string connectionString, IUserDirectory directory, string storagePath, FakePreviewProcessor processor, FakePushSender push, FakeEmailSender email)
    : WebApplicationFactory<Program>
{
    public static readonly string LinkSigningKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    public static readonly string ComplianceSigningKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    public const int WebhookPerMinute = 5;

    /// <summary>Presence turns away after this many seconds without activity in tests.</summary>
    public const int AwayAfterSeconds = 2;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Mode", connectionString);
        builder.UseSetting("UserDirectory:ConnectionString", "Server=unused;Database=unused");
        builder.UseSetting("Auth:Issuer", TestAuth.Issuer);
        builder.UseSetting("Auth:Audience", TestAuth.Audience);
        builder.UseSetting("Auth:PublicKeyPem", TestAuth.PublicKeyPem);
        builder.UseSetting("Auth:ClockSkewSeconds", "0");
        builder.UseSetting("Files:StoragePath", storagePath);
        builder.UseSetting("Files:LinkSigningKey", LinkSigningKey);
        builder.UseSetting("Files:MaxSizeMb", "1");
        builder.UseSetting("Files:ProcessorCallbackBaseUrl", "http://localhost");
        builder.UseSetting("Jobs:Enabled", "false");
        builder.UseSetting("Compliance:SigningKey", ComplianceSigningKey);
        builder.UseSetting("Webhooks:PerMinute", WebhookPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Presence:AwayAfterSeconds", AwayAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Presence:SweepSeconds", "1");
        // The fake sender never uses the keys; they only need to be present for push to count as configured.
        builder.UseSetting("Push:Subject", "mailto:tests@test.example");
        builder.UseSetting("Push:PublicKey", "test-public-key");
        builder.UseSetting("Push:PrivateKey", "test-private-key");
        builder.UseSetting("Email:Enabled", "true");
        builder.UseSetting("Email:FromAddress", "mode@test.example");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPushSender>();
            services.AddSingleton<IPushSender>(push);
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(email);
            services.RemoveAll<IUserDirectory>();
            services.AddSingleton(directory);
            services.RemoveAll<IVirusScanner>();
            services.AddSingleton<IVirusScanner, FakeVirusScanner>();
            services.RemoveAll<IPreviewProcessor>();
            services.AddSingleton<IPreviewProcessor>(processor);
            services.AddScoped<IJobHandler, CountingJobHandler>();
            services.AddScoped<IJobHandler, FailingJobHandler>();
        });
    }
}

internal static class HttpTestExtensions
{
    public static async Task<JsonElement> PostJsonOkAsync(this HttpClient client, string url, object body)
    {
        var response = await client.PostAsJsonAsync(url, body);
        Assert.True(response.IsSuccessStatusCode, $"POST {url} -> {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<JsonElement> GetJsonAsync(this HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.IsSuccessStatusCode, $"GET {url} -> {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
