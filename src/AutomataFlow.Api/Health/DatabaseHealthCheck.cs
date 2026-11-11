using AutomataFlow.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AutomataFlow.Api.Health;

public sealed class DatabaseHealthCheck(AutomataFlowDbContext dbContext, ILogger<DatabaseHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var canConnect = await dbContext.Database.CanConnectAsync(cancellationToken);
            return canConnect
                ? HealthCheckResult.Healthy("PostgreSQL is reachable.")
                : HealthCheckResult.Unhealthy("PostgreSQL is not reachable.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Do not put the exception in the result: it may contain connection details.
            logger.LogWarning(ex, "Database readiness check failed");
            return HealthCheckResult.Unhealthy("PostgreSQL check failed.");
        }
    }
}
