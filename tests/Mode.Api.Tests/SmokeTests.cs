using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Mode.Api.Tests;

public sealed class SmokeTests(SmokeTests.Factory factory) : IClassFixture<SmokeTests.Factory>
{
    [Fact]
    public async Task Liveness_endpoint_returns_healthy()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // Never contacted by the liveness probe; present so startup validation passes.
            builder.UseSetting("ConnectionStrings:Mode", "Host=unused;Database=unused");
            builder.UseSetting("UserDirectory:ConnectionString", "Server=unused;Database=unused");
            builder.UseSetting("Auth:Issuer", Infrastructure.TestAuth.Issuer);
            builder.UseSetting("Auth:Audience", Infrastructure.TestAuth.Audience);
            builder.UseSetting("Auth:PublicKeyPem", Infrastructure.TestAuth.PublicKeyPem);
            builder.UseSetting("Files:StoragePath", Path.Combine(Path.GetTempPath(), "mode-smoke"));
            builder.UseSetting("Files:LinkSigningKey", Infrastructure.ApiFactory.LinkSigningKey);
            builder.UseSetting("Jobs:Enabled", "false");
        }
    }
}
