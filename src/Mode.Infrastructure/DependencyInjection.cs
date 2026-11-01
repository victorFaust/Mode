using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Mode.Domain.Directory;
using Mode.Infrastructure.Data;
using Mode.Infrastructure.Directory;

namespace Mode.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddModeInfrastructure(this IServiceCollection services)
    {
        services.AddDbContext<ModeDbContext>((sp, options) =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("Mode")
                ?? throw new InvalidOperationException("ConnectionStrings:Mode is not configured.");
            options.UseNpgsql(connectionString);
        });

        services.AddMemoryCache();
        services.AddOptions<UserDirectoryOptions>()
            .BindConfiguration(UserDirectoryOptions.SectionName)
            .ValidateDataAnnotations()
            .Validate(o => o.HasValidIdentifiers(), "UserDirectory table and column names must be plain SQL identifiers.")
            .ValidateOnStart();
        services.AddSingleton<IUserDirectory, SqlServerUserDirectory>();

        return services;
    }

    public static IHealthChecksBuilder AddModeInfrastructureChecks(this IHealthChecksBuilder builder, string[] tags) =>
        builder
            .AddDbContextCheck<ModeDbContext>("database", tags: tags)
            .AddCheck<UserDirectoryHealthCheck>("user-directory", tags: tags);
}
