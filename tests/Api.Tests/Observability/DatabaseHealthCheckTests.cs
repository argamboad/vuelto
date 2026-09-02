using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Perezosoft.Api.Observability;
using Perezosoft.Api.Tests.Infrastructure;
using Perezosoft.Infrastructure.Persistence;

namespace Perezosoft.Api.Tests.Observability;

/// <summary>
/// OBS-3 (ADR-008): the readiness check reflects database reachability — Healthy when the DB is up,
/// Unhealthy when it isn't (so an orchestrator stops routing to a node that can't serve).
/// </summary>
[Collection(PostgresCollection.Name)]
public class DatabaseHealthCheckTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task DatabaseReachable_ReportsHealthy()
    {
        await using var db = Fixture.CreateContext();

        var result = await new DatabaseHealthCheck(db).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task DatabaseUnreachable_ReportsUnhealthy()
    {
        // Points at a refused port with a short timeout so the check fails fast.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Port=1;Database=x;Username=x;Password=x;Timeout=1").Options;
        await using var db = new AppDbContext(options, new TestCurrentTenant());

        var result = await new DatabaseHealthCheck(db).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}
