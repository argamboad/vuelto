using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Perezosoft.Api.Tests.Infrastructure;
using Perezosoft.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Perezosoft.Api.Tests;

/// <summary>
/// Guards against migration/snapshot drift, which the rest of the suite cannot see because the
/// shared fixture builds its schema with <c>EnsureCreated</c> (CONF-18 / B6-2). This applies the
/// REAL migrations to a throwaway database — catching a broken migration — and asserts the model is
/// fully captured by them (no pending model changes), so a future entity edit without a migration
/// fails the build instead of silently drifting.
/// </summary>
public sealed class MigrationsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;
        return new AppDbContext(options, new TestCurrentTenant());
    }

    [Fact]
    public async Task Migrations_ApplyCleanly_AndModelHasNoPendingChanges()
    {
        await using var db = CreateContext();

        await db.Database.MigrateAsync(); // every migration runs against a real DB — throws if broken

        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); // all applied
        Assert.False(db.Database.HasPendingModelChanges());          // model == last snapshot (no drift)
    }

    [Fact]
    public async Task Migrations_Down_RevertCleanly_ToEmptySchema()
    {
        // v2 audit B8: exercise every Down — a broken rollback (e.g. dropping a renamed column) is
        // otherwise invisible until a production rollback. Migrate up, then all the way back to empty.
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        await db.GetService<IMigrator>().MigrateAsync(Migration.InitialDatabase); // runs every Down; throws if broken

        Assert.Empty(await db.Database.GetAppliedMigrationsAsync()); // schema fully reverted
    }
}
