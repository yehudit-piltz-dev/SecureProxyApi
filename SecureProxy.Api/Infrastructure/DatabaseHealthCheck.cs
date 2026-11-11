using Microsoft.Extensions.Diagnostics.HealthChecks;
using SecureProxy.Api.Data;

namespace SecureProxy.Api.Infrastructure;

/// <summary>
/// Readiness check: the proxy cannot serve requests without SQL Server (configuration and the mandatory
/// audit record both live there), so an instance that cannot reach the database reports itself not ready.
/// </summary>
public sealed class DatabaseHealthCheck(ProxyDbContext db) : IHealthCheck
{
    public const string ReadyTag = "ready";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // The result carries no exception or connection details: health endpoints are often reachable
        // by load balancers and monitoring, and must not reveal server names.
        return await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Database unavailable.");
    }
}
