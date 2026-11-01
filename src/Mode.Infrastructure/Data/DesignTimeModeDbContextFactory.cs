using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Mode.Infrastructure.Data;

/// <summary>Used by dotnet-ef. Reads the API's user secrets, then environment variables.</summary>
internal sealed class DesignTimeModeDbContextFactory : IDesignTimeDbContextFactory<ModeDbContext>
{
    private const string ApiUserSecretsId = "mode-api-5d0f2c3e-8b1a-4c7e-9f60-3a2b1d4e7c90";

    public ModeDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets(ApiUserSecretsId)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration.GetConnectionString("Mode") ?? "Host=localhost;Database=mode";
        var options = new DbContextOptionsBuilder<ModeDbContext>().UseNpgsql(connectionString).Options;
        return new ModeDbContext(options);
    }
}
