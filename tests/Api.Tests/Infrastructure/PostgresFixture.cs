using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Vuelto.Api.Tests.Infrastructure;

/// <summary>Test double for the request's current tenant. Settable so a test can run
/// "as" a given tenant and exercise the global query filter. Also implements
/// <see cref="ITenantContext"/> with the same enter/restore semantics as
/// <c>HttpCurrentTenant</c>, so services that EnterTenant (webhook, invitation accept)
/// can be exercised against the same ambient-tenant instance the context filters by.</summary>
public sealed class TestCurrentTenant : ICurrentTenant, ITenantContext
{
    public Guid? TenantId { get; set; }

    public IDisposable EnterTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Cannot enter the empty tenant.", nameof(tenantId));
        var previous = TenantId;
        TenantId = tenantId;
        return new RestoreScope(this, previous);
    }

    private sealed class RestoreScope(TestCurrentTenant owner, Guid? previous) : IDisposable
    {
        public void Dispose() => owner.TenantId = previous;
    }
}

/// <summary>
/// Spins up a real PostgreSQL instance in a throwaway container for the whole test
/// run. Real Postgres (not the EF in-memory provider) is required because several
/// services branch on <c>Database.IsRelational()</c> and use relational-only
/// constructs — <c>ExecuteUpdateAsync</c>, savepoints — that the in-memory provider
/// silently skips. Requires a running Docker daemon (the dev box already runs one
/// for <c>docker compose</c>).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = CreateContext();
        // Build the schema from the EF model (fast; no migration history needed for tests).
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>
    /// A fresh context bound to the container, acting as <paramref name="tenantId"/>
    /// (null = no current tenant, so tenant-scoped rows are filtered out). The caller
    /// disposes it.
    /// </summary>
    public AppDbContext CreateContext(Guid? tenantId = null) => CreateTestContext(tenantId);

    /// <summary>
    /// A fresh <see cref="TestAppDbContext"/> (real model + the <see cref="TestWidget"/> fixture entity)
    /// bound to the container. Returned typed as the concrete test context so tests can reach
    /// <c>TestWidgets</c>; existing tests use it as an <see cref="AppDbContext"/> transparently.
    /// </summary>
    public TestAppDbContext CreateTestContext(Guid? tenantId = null)
        => CreateTestContext(new TestCurrentTenant { TenantId = tenantId });

    /// <summary>
    /// A fresh context whose global query filter follows the GIVEN ambient-tenant instance — share it
    /// with a <see cref="ServiceHarness"/> so a service's <c>EnterTenant</c> scope drives the context's
    /// filter too, exactly like the one scoped <c>HttpCurrentTenant</c> does in production (needed by
    /// flows that enter another tenant mid-call, e.g. the invitation accept's seat re-check).
    /// </summary>
    public TestAppDbContext CreateTestContext(ICurrentTenant currentTenant)
    {
        var options = new DbContextOptionsBuilder<TestAppDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new TestAppDbContext(options, currentTenant);
    }

    /// <summary>
    /// An <see cref="IDbContextFactory{TContext}"/> over <see cref="CreateContext"/> for services
    /// that write OUT-OF-BAND of the ambient transaction (the webhook delivery recorder): each
    /// created context is fresh — own connection, no ambient tenant — exactly like the scoped
    /// factory the production DI registers.
    /// </summary>
    public IDbContextFactory<AppDbContext> CreateContextFactory() => new FixtureContextFactory(this);

    private sealed class FixtureContextFactory(PostgresFixture fixture) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => fixture.CreateContext();
    }

    /// <summary>
    /// Truncates every table so each test starts from a clean slate. The table list is DERIVED from the
    /// EF model (v2 audit TR-3), so a new entity is reset automatically — no hand-maintained list to
    /// forget. Call at the top of a test (or in the class constructor) when tests share the container.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var db = CreateTestContext();
        var tables = db.Model.GetEntityTypes()
            .Select(e => e.GetTableName())
            .Where(name => name is not null)
            .Distinct()
            .Select(name => $"\"{name}\"");
        // Table names come from the EF model (not user input), so this is not an injection surface;
        // build the statement as a plain string (no interpolated argument) to satisfy the EF1002 analyzer.
        var sql = "TRUNCATE TABLE " + string.Join(", ", tables) + " RESTART IDENTITY CASCADE;";
        await db.Database.ExecuteSqlRawAsync(sql);
    }
}

/// <summary>
/// xUnit collection so the (relatively expensive) container is created once and
/// shared by every relational test class. Decorate such classes with
/// <c>[Collection(PostgresCollection.Name)]</c>.
/// </summary>
[CollectionDefinition(PostgresCollection.Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
