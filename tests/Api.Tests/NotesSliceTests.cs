using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Perezosoft.Api.Features.Notes;
using Perezosoft.Api.Tests.Infrastructure;
using Perezosoft.Core.Entities;
using Perezosoft.Infrastructure.Repositories;

namespace Perezosoft.Api.Tests;

/// <summary>
/// Reference-slice tests for the sample Notes feature: it's tenant-scoped purely through
/// the platform (generic repository + global query filter — no per-query Where(TenantId)),
/// and its <see cref="NotesDataContributor"/> participates in tenant dissolve. This is the
/// behavior every real feature slice inherits for free.
/// </summary>
[Collection(PostgresCollection.Name)]
public class NotesSliceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static NotesHandler Handler(Perezosoft.Infrastructure.Persistence.AppDbContext db, Guid tenantId, TimeProvider? clock = null) =>
        new(new EfRepository<Note>(db), new TestCurrentTenant { TenantId = tenantId }, clock ?? TimeProvider.System);

    [Fact]
    public async Task Notes_AreVisibleOnlyToTheirTenant()
    {
        var tenantA = Guid.CreateVersion7();
        var tenantB = Guid.CreateVersion7();

        await using (var db = Fixture.CreateContext(tenantA))
            Assert.NotNull(await Handler(db, tenantA).CreateAsync(new CreateNoteRequest("A's note", "hi"), default));

        await using (var db = Fixture.CreateContext(tenantB))
            Assert.Empty(await Handler(db, tenantB).ListAsync(default));   // B sees nothing

        await using (var db = Fixture.CreateContext(tenantA))
            Assert.Single(await Handler(db, tenantA).ListAsync(default));  // A sees its own
    }

    [Fact]
    public async Task Create_StampsCreatedAt_FromInjectedClock()
    {
        var tenant = Guid.CreateVersion7();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 22, 9, 30, 0, TimeSpan.Zero));

        await using var db = Fixture.CreateContext(tenant);
        var created = await Handler(db, tenant, clock).CreateAsync(new CreateNoteRequest("clocked", null), default);

        Assert.NotNull(created);
        var stored = await db.Notes.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(clock.GetUtcNow(), stored.CreatedAt);
        Assert.Equal(clock.GetUtcNow(), stored.UpdatedAt);
    }

    [Fact]
    public async Task Create_BlankTitle_IsRejected()
    {
        var tenant = Guid.CreateVersion7();

        await using var db = Fixture.CreateContext(tenant);
        Assert.Null(await Handler(db, tenant).CreateAsync(new CreateNoteRequest("  ", null), default));
    }

    [Fact]
    public async Task Contributor_ReportsAndWipesTenantData()
    {
        var tenant = Guid.CreateVersion7();

        await using (var db = Fixture.CreateContext(tenant))
            await Handler(db, tenant).CreateAsync(new CreateNoteRequest("keep", null), default);

        // The contributor's Wipe runs inside the dissolve's EnterTenant(target) scope (RLS-2/T6), so the
        // filter scopes the set-based delete to the target — the context is that tenant here. HasData reads
        // cross-tenant (QueryAllTenants) so it sees the row regardless.
        await using (var db = Fixture.CreateContext(tenant))
        {
            var contributor = new NotesDataContributor(new EfRepository<Note>(db));
            Assert.True(await contributor.HasDataAsync(tenant));
            await contributor.WipeAsync(tenant);
            Assert.False(await contributor.HasDataAsync(tenant));
        }
    }
}
