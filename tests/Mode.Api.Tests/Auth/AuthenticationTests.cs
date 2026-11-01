using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Mode.Api.Auditing;
using Mode.Api.Tests.Infrastructure;

namespace Mode.Api.Tests.Auth;

[Collection(ApiCollection.Name)]
public sealed class AuthenticationTests(ApiFixture api)
{
    private async Task<HttpStatusCode> MeStatusAsync(string? token)
    {
        using var client = token is null ? api.Anonymous() : api.WithToken(token);
        return (await client.GetAsync("/api/v1/me")).StatusCode;
    }

    [Fact]
    public async Task Request_without_token_is_rejected() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(null));

    [Fact]
    public async Task Valid_token_identifies_the_user()
    {
        var userId = api.NewUser("Ada Okafor");
        using var client = api.ClientFor(userId);

        var me = await client.GetJsonAsync("/api/v1/me");

        Assert.Equal(userId, me.GetProperty("user").GetProperty("id").GetString());
        Assert.Equal("Ada Okafor", me.GetProperty("user").GetProperty("displayName").GetString());
    }

    public static TheoryData<string> RejectedTokens => new()
    {
        "expired", "wrong-issuer", "wrong-audience", "unsigned", "hmac-with-public-key", "foreign-key", "no-expiry",
    };

    [Theory]
    [MemberData(nameof(RejectedTokens))]
    public async Task Invalid_tokens_are_rejected(string kind)
    {
        var userId = api.NewUser();
        var token = kind switch
        {
            "expired" => TestAuth.CreateToken(userId, lifetime: TimeSpan.FromMinutes(-1)),
            "wrong-issuer" => TestAuth.CreateToken(userId, issuer: "https://evil.test"),
            "wrong-audience" => TestAuth.CreateToken(userId, audience: "another-app"),
            "unsigned" => TestAuth.CreateToken(userId, unsigned: true),
            // Algorithm confusion: an HMAC token keyed with the public RSA key.
            "hmac-with-public-key" => TestAuth.CreateToken(userId, credentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestAuth.PublicKeyPem)), SecurityAlgorithms.HmacSha256)),
            "foreign-key" => TestAuth.CreateToken(userId, credentials: new SigningCredentials(
                new RsaSecurityKey(RSA.Create(2048)), SecurityAlgorithms.RsaSha256)),
            "no-expiry" => TestAuth.CreateToken(userId, withExpiry: false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(token));
    }

    [Fact]
    public async Task Unknown_user_is_rejected() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(TestAuth.CreateToken("u-not-in-directory")));

    [Fact]
    public async Task Inactive_user_is_rejected_even_with_a_valid_token()
    {
        var userId = api.NewUser();
        var token = TestAuth.CreateToken(userId);
        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(token));

        api.SetActive(userId, false);

        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(token));
    }

    [Theory]
    [InlineData("mode-admin", true, false)]
    [InlineData("GUEST", false, true)]
    [InlineData("unrelated-role", false, false)]
    public async Task Host_roles_map_to_mode_roles(string hostRole, bool isAdmin, bool isGuest)
    {
        using var client = api.ClientFor(api.NewUser(), hostRole);

        var me = await client.GetJsonAsync("/api/v1/me");

        Assert.Equal(isAdmin, me.GetProperty("isAdmin").GetBoolean());
        Assert.Equal(isGuest, me.GetProperty("isGuest").GetBoolean());
    }

    [Fact]
    public async Task Query_string_token_is_accepted_only_on_the_hub_path()
    {
        var token = TestAuth.CreateToken(api.NewUser());
        using var client = api.Anonymous();

        var rest = await client.GetAsync($"/api/v1/me?access_token={token}");
        var negotiate = await client.PostAsync($"/hubs/mode/negotiate?negotiateVersion=1&access_token={token}", null);
        var negotiateWithoutToken = await client.PostAsync("/hubs/mode/negotiate?negotiateVersion=1", null);

        Assert.Equal(HttpStatusCode.Unauthorized, rest.StatusCode);
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, negotiateWithoutToken.StatusCode);
    }

    [Fact]
    public async Task First_use_of_a_token_is_audited_once_as_a_sign_in()
    {
        var userId = api.NewUser();
        using var client = api.ClientFor(userId);

        await client.GetJsonAsync("/api/v1/me");
        await client.GetJsonAsync("/api/v1/me");

        await using var db = api.CreateDbContext();
        var signIns = await db.AuditLogs.CountAsync(a => a.Action == AuditActions.SignIn && a.ActorUserId == userId);
        Assert.Equal(1, signIns);
    }

    [Fact]
    public async Task Realtime_connection_is_closed_when_the_token_expires()
    {
        var userId = api.NewUser();
        var token = TestAuth.CreateToken(userId, lifetime: TimeSpan.FromSeconds(4));
        await using var connection = CreateHubConnection(token);
        var closed = new TaskCompletionSource();
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        await connection.StartAsync();
        Assert.Equal(HubConnectionState.Connected, connection.State);

        var finished = await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(closed.Task, finished);

        await using var db = api.CreateDbContext();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == AuditActions.RealtimeConnected && a.ActorUserId == userId));
    }

    [Fact]
    public async Task Realtime_connection_with_expired_token_is_refused()
    {
        var token = TestAuth.CreateToken(api.NewUser(), lifetime: TimeSpan.FromMinutes(-1));
        await using var connection = CreateHubConnection(token);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    private HubConnection CreateHubConnection(string token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(api.Factory.Server.BaseAddress, "hubs/mode"), options =>
            {
                options.HttpMessageHandlerFactory = _ => api.Factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
}
