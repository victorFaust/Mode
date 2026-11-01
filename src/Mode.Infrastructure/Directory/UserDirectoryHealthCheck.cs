using Microsoft.Extensions.Diagnostics.HealthChecks;
using Mode.Domain.Directory;

namespace Mode.Infrastructure.Directory;

internal sealed class UserDirectoryHealthCheck(IUserDirectory directory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (directory is not SqlServerUserDirectory sql)
            return HealthCheckResult.Healthy();

        try
        {
            await using var connection = await sql.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql.ProbeSql;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Host user directory is unreachable or misconfigured.", ex);
        }
    }
}
