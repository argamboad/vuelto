using Microsoft.EntityFrameworkCore;
using Npgsql;
using Perezosoft.Api.Tests.Infrastructure;
using Perezosoft.Infrastructure.Persistence;

namespace Perezosoft.Api.Tests.Rls;

/// <summary>
/// ADR-020 keystone: Postgres row-level security is an INDEPENDENT second tenancy wall. These tests
/// connect as the non-privileged runtime role and prove that even queries which escaped the EF
/// query filter (<c>IgnoreQueryFilters()</c>, raw SQL) cannot see or write another tenant's rows —
/// and that the sanctioned escapes (the <see cref="RlsTags.CrossTenant"/> tag, the tenant-less
/// system context) still work. The EF-level wall has its own suite (TenantScopeFilterTests etc.);
/// nothing here relies on it.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RlsBackstopTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly Guid _tenantA = Guid.CreateVersion7();
    private readonly Guid _tenantB = Guid.CreateVersion7();
    private string RuntimeCs => RlsTestSetup.RuntimeConnectionString(fixture.ConnectionString);

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await using (var db = fixture.CreateTestContext())
            await RlsTestSetup.ProvisionAsync(db);

        // One widget per tenant, seeded as the (RLS-exempt) superuser through the normal
        // tenant-stamping path.
        await using (var a = fixture.CreateTestContext(_tenantA))
        {
            a.TestWidgets.Add(new TestWidget { Name = "a-widget", CreatedAt = DateTimeOffset.UtcNow });
            await a.SaveChangesAsync();
        }
        await using (var b = fixture.CreateTestContext(_tenantB))
        {
            b.TestWidgets.Add(new TestWidget { Name = "b-widget", CreatedAt = DateTimeOffset.UtcNow });
            await b.SaveChangesAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A TestAppDbContext connected as the runtime role (subject to RLS).</summary>
    private TestAppDbContext RuntimeContext(Guid? tenantId)
    {
        var options = new DbContextOptionsBuilder<TestAppDbContext>()
            .UseNpgsql(RuntimeCs)
            .Options;
        return new TestAppDbContext(options, new TestCurrentTenant { TenantId = tenantId });
    }

    // ── Raw SQL: the wall exists independently of anything EF does ────────────────────────────

    [Fact]
    public async Task RawSql_WithTenantGuc_SeesOnlyThatTenant()
    {
        await using var conn = new NpgsqlConnection(RuntimeCs);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand($"SET LOCAL {RlsDdl.TenantGuc} = '{_tenantA}'", conn, tx))
            await set.ExecuteNonQueryAsync();

        await using var query = new NpgsqlCommand("""SELECT "Name" FROM "TestWidgets";""", conn, tx);
        await using var reader = await query.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));

        Assert.Equal(["a-widget"], names);
    }

    [Fact]
    public async Task RawSql_WithoutGuc_SeesNothing_FailClosed()
    {
        await using var conn = new NpgsqlConnection(RuntimeCs);
        await conn.OpenAsync();
        await using var query = new NpgsqlCommand("""SELECT count(*) FROM "TestWidgets";""", conn);

        Assert.Equal(0L, await query.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RawSql_EmptyTenantGuc_SeesNothing_FailClosed()
    {
        await using var conn = new NpgsqlConnection(RuntimeCs);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand($"SET LOCAL {RlsDdl.TenantGuc} = ''", conn, tx))
            await set.ExecuteNonQueryAsync();

        await using var query = new NpgsqlCommand("""SELECT count(*) FROM "TestWidgets";""", conn, tx);
        Assert.Equal(0L, await query.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RawSql_BypassGuc_SeesAllTenants()
    {
        await using var conn = new NpgsqlConnection(RuntimeCs);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand($"SET LOCAL {RlsDdl.BypassGuc} = 'on'", conn, tx))
            await set.ExecuteNonQueryAsync();

        await using var query = new NpgsqlCommand("""SELECT count(*) FROM "TestWidgets";""", conn, tx);
        Assert.Equal(2L, await query.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RawSql_ForeignTenantInsert_IsRejected()
    {
        await using var conn = new NpgsqlConnection(RuntimeCs);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand($"SET LOCAL {RlsDdl.TenantGuc} = '{_tenantA}'", conn, tx))
            await set.ExecuteNonQueryAsync();

        await using var insert = new NpgsqlCommand(
            """INSERT INTO "TestWidgets" ("Id", "TenantId", "Name", "CreatedAt") VALUES ($1, $2, 'smuggled', now());""",
            conn, tx);
        insert.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
        insert.Parameters.Add(new NpgsqlParameter { Value = _tenantB });

        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState); // new row violates row-level security policy
    }

    // ── Through the EF stack: the interceptor carries the ambient tenant to the database ──────

    [Fact]
    public async Task Ef_NormalQuery_WithTenant_StillWorks()
    {
        await using var db = RuntimeContext(_tenantA);
        var names = await db.TestWidgets.Select(w => w.Name).ToListAsync();

        Assert.Equal(["a-widget"], names);
    }

    [Fact]
    public async Task Ef_IgnoreQueryFilters_EscapedEfWall_RlsStillScopes()
    {
        // THE keystone: the EF query filter is deliberately bypassed (simulating the bug class the
        // backstop exists for) — the database must still return only the current tenant's rows.
        await using var db = RuntimeContext(_tenantA);
        var names = await db.TestWidgets.IgnoreQueryFilters().Select(w => w.Name).ToListAsync();

        Assert.Equal(["a-widget"], names);
    }

    [Fact]
    public async Task Ef_TaggedCrossTenantQuery_IsSanctioned_SeesAll()
    {
        await using var db = RuntimeContext(_tenantA);
        var count = await db.TestWidgets.IgnoreQueryFilters().TagWith(RlsTags.CrossTenant).CountAsync();

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Ef_SystemContext_NoTenant_IsSanctioned_SeesAll()
    {
        // Tenant-less = the system context (outbox dispatcher, sweeps, jobs): explicitly bypassed.
        await using var db = RuntimeContext(tenantId: null);
        var count = await db.TestWidgets.IgnoreQueryFilters().CountAsync();

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Ef_SaveChanges_WithTenant_InsertsOwnRow()
    {
        await using (var db = RuntimeContext(_tenantA))
        {
            db.TestWidgets.Add(new TestWidget { Name = "a-second", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        await using var check = RuntimeContext(_tenantA);
        Assert.Equal(2, await check.TestWidgets.CountAsync());
    }

    [Fact]
    public async Task Ef_TaggedExecuteUpdate_TagDoesNotRender_StaysBlocked()
    {
        // Pins an EF limitation the design depends on: query tags are NOT rendered into the
        // ExecuteUpdate/ExecuteDelete pipeline, so tags cannot sanction set-based cross-tenant
        // writes — those must EnterTenant instead (see TenantInvitationService.AcceptAsync). If EF
        // ever starts rendering tags here, this fails and the tag mechanism can be extended.
        await using var db = RuntimeContext(_tenantA);
        var touched = await db.TestWidgets.IgnoreQueryFilters().TagWith(RlsTags.CrossTenant)
            .Where(w => w.Name == "b-widget")
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Name, "defaced"));

        Assert.Equal(0, touched);
    }

    [Fact]
    public async Task Ef_EnteredTenant_ExecuteUpdate_IsSanctioned()
    {
        // The invitation-accept mechanism: entering the target tenant makes the set-based write
        // pass the DB policy (the interceptor re-asserts the GUC when the ambient tenant changes).
        await using var db = RuntimeContext(_tenantB);
        var touched = await db.TestWidgets.IgnoreQueryFilters()
            .Where(w => w.Name == "b-widget")
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Name, "b-widget-renamed"));

        Assert.Equal(1, touched);
    }

    [Fact]
    public async Task Ef_ExecuteUpdate_IgnoreQueryFilters_CannotTouchForeignRows()
    {
        // Set-based writes (ExecuteUpdate/Delete) skip the change tracker AND the stamping
        // interceptor — the DB policy is the only wall left. It must hold.
        await using var db = RuntimeContext(_tenantA);
        var touched = await db.TestWidgets.IgnoreQueryFilters()
            .Where(w => w.Name == "b-widget")
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Name, "defaced"));

        Assert.Equal(0, touched);
    }
}
