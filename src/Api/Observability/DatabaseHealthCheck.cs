using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Perezosoft.Infrastructure.Persistence;

namespace Perezosoft.Api.Observability;

/// <summary>
/// Readiness check (OBS-3, ADR-008): reports Healthy only when the database is reachable, so an
/// orchestrator won't route traffic to an instance that can't serve. <b>Status-only</b> — no connection
/// details are surfaced (the endpoint writes just the status string).
/// </summary>
public sealed class DatabaseHealthCheck(AppDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Database unreachable");
        }
        catch
        {
            return HealthCheckResult.Unhealthy("Database unreachable");
        }
    }
}
