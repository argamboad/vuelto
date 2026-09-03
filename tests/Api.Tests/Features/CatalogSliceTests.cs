using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Api.Features.Catalog;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Features;

/// <summary>
/// CATALOG-1/2 on real Postgres, through the category handler (the bank handler differs only in seed
/// data and error prefix — covered where it matters): first-read seeding in the caller's locale,
/// idempotent seeding, the 409 reactivation offer, rename/reactivate/deactivate, uniform 404 for
/// foreign ids, tenant isolation for reads and writes, and the contributors.
/// </summary>
[Collection(PostgresCollection.Name)]
public class CatalogSliceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private static CategoryCatalogHandler Categories(AppDbContext db, Guid tenantId) =>
        new(new EfRepository<Category>(db), new TestCurrentTenant { TenantId = tenantId }, new FakeTimeProvider(T0));

    private static BankCatalogHandler Banks(AppDbContext db, Guid tenantId) =>
        new(new EfRepository<Bank>(db), new TestCurrentTenant { TenantId = tenantId }, new FakeTimeProvider(T0));

    [Fact]
    public async Task FirstList_SeedsTheDefaults_InTheCallersLocale_AndOnlyOnce()
    {
        var tenant = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);

        var first = (await Categories(db, tenant).ListAsync(includeInactive: false, locale: "es", default))!;
        Assert.Equal(SeedCatalog.CategoryNames("es").OrderBy(n => n), first.Select(c => c.Name));
        Assert.All(first, c => Assert.True(c.IsActive));

        var second = (await Categories(db, tenant).ListAsync(false, "en", default))!; // a later reader's locale is ignored
        Assert.Equal(first.Count, second.Count);
        Assert.Equal(SeedCatalog.Categories.Count, await db.Categories.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Banks_SeedCashFirst_LocalizedOnlyForCash()
    {
        var tenant = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);

        var banks = await Banks(db, tenant).ListAsync(false, "es-CR", default);

        Assert.Contains(banks!, b => b.Name == "Efectivo");
        Assert.Contains(banks!, b => b.Name == "BAC Credomatic");
        Assert.Equal(9, banks!.Count);
    }

    [Fact]
    public async Task Create_TrimsAndPersists_ThenClashes_CaseInsensitively()
    {
        var tenant = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);
        var handler = Categories(db, tenant);

        var (created, error) = await handler.CreateAsync(new CreateCatalogEntryRequest("  Viajes "), default);
        Assert.Null(error);
        Assert.Equal("Viajes", created!.Name);
        Assert.True(created.IsActive);

        var (dup, clash) = await handler.CreateAsync(new CreateCatalogEntryRequest("VIAJES"), default);
        Assert.Null(dup);
        var conflict = Assert.IsType<CatalogConflictResponse>(clash);
        Assert.Equal("category_exists", conflict.Error);
        Assert.Null(conflict.ExistingId);
        Assert.Null(conflict.ExistingName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Create_BlankName_IsInvalidRequest(string? name)
    {
        var tenant = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);

        var (created, error) = await Categories(db, tenant).CreateAsync(new CreateCatalogEntryRequest(name), default);

        Assert.Null(created);
        Assert.Equal("invalid_request", error!.Error);
    }

    [Fact]
    public async Task InactiveClash_OffersReactivation_AndUpdateReactivates()
    {
        var tenant = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);
        var handler = Categories(db, tenant);

        var (created, _) = await handler.CreateAsync(new CreateCatalogEntryRequest("Gym"), default);
        await handler.UpdateAsync(created!.Id, new UpdateCatalogEntryRequest("Gym", IsActive: false), default);

        var (_, clash) = await handler.CreateAsync(new CreateCatalogEntryRequest("gym"), default);
        var offer = Assert.IsType<CatalogConflictResponse>(clash);
        Assert.Equal("category_exists_inactive", offer.Error);
        Assert.Equal(created.Id, offer.ExistingId);
        Assert.Equal("Gym", offer.ExistingName); // the stored name, so reactivation restores it as it was

        var (reactivated, error) = await handler.UpdateAsync(offer.ExistingId!.Value, new UpdateCatalogEntryRequest("Gym", IsActive: true), default);
        Assert.Null(error);
        Assert.True(reactivated!.IsActive);

        var active = await handler.ListAsync(false, "en", default);
        var all = await handler.ListAsync(true, "en", default);
        Assert.Contains(active!, c => c.Name == "Gym");
        Assert.Single(all!); // the household already had a row before its first list, so nothing was seeded
    }

    [Fact]
    public async Task List_ExcludesInactive_UnlessAsked()
    {
        var tenant = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);
        var handler = Categories(db, tenant);
        var (c, _) = await handler.CreateAsync(new CreateCatalogEntryRequest("Old"), default);
        await handler.UpdateAsync(c!.Id, new UpdateCatalogEntryRequest("Old", false), default);

        Assert.DoesNotContain(await handler.ListAsync(false, "en", default) ?? [], x => x.Name == "Old");
        Assert.Contains(await handler.ListAsync(true, "en", default) ?? [], x => x.Name == "Old" && !x.IsActive);
    }

    [Fact]
    public async Task Update_RenameClash_Is409_UnknownId_Is404()
    {
        var tenant = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);
        var handler = Categories(db, tenant);
        var (a, _) = await handler.CreateAsync(new CreateCatalogEntryRequest("A"), default);
        var (b, _) = await handler.CreateAsync(new CreateCatalogEntryRequest("B"), default);

        var (_, clash) = await handler.UpdateAsync(b!.Id, new UpdateCatalogEntryRequest("a", true), default);
        Assert.Equal("category_exists", Assert.IsType<CatalogConflictResponse>(clash).Error);

        var (_, sameName) = await handler.UpdateAsync(a!.Id, new UpdateCatalogEntryRequest("A", true), default);
        Assert.Null(sameName);                                   // renaming to your own name is fine

        var (_, missing) = await handler.UpdateAsync(Guid.CreateVersion7(), new UpdateCatalogEntryRequest("X", true), default);
        Assert.Equal("not_found", missing!.Error);
    }

    [Fact]
    public async Task Entries_AreInvisibleAndUnwritable_AcrossTenants()
    {
        var tenantA = Guid.CreateVersion7();
        var tenantB = Guid.CreateVersion7();
        Guid aId;

        await using (var db = Fixture.CreateContext(tenantA))
            aId = (await Categories(db, tenantA).CreateAsync(new CreateCatalogEntryRequest("A only"), default)).Entry!.Id;

        await using (var db = Fixture.CreateContext(tenantB))
        {
            var handler = Categories(db, tenantB);
            Assert.DoesNotContain(await handler.ListAsync(true, "en", default) ?? [], c => c.Name == "A only"); // read negative
            var (_, error) = await handler.UpdateAsync(aId, new UpdateCatalogEntryRequest("Hijacked", false), default);
            Assert.Equal("not_found", error!.Error);                                                            // write negative: 404, not 403
            Assert.Null((await handler.CreateAsync(new CreateCatalogEntryRequest("A only"), default)).Error);  // same name is free in B
        }

        await using (var db = Fixture.CreateContext(tenantA))
        {
            var a = await db.Categories.IgnoreQueryFilters().SingleAsync(c => c.Id == aId);
            Assert.Equal("A only", a.Name);
            Assert.True(a.IsActive);
        }
    }

    [Fact]
    public async Task Contributors_ReportWipeAndExport_PerTenant()
    {
        var tenant = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        await using (var db = Fixture.CreateContext(tenant))
        {
            await Categories(db, tenant).ListAsync(false, "en", default);
            await Banks(db, tenant).ListAsync(false, "en", default);
        }
        await using (var db = Fixture.CreateContext(other))
            await Categories(db, other).ListAsync(false, "en", default);

        await using (var db = Fixture.CreateContext(tenant))
        {
            var categories = new CategoryDataContributor(new EfRepository<Category>(db));
            var banks = new BankDataContributor(new EfRepository<Bank>(db));
            Assert.Equal("categories", categories.ExportKey);
            Assert.Equal("banks", banks.ExportKey);
            Assert.True(await categories.HasDataAsync(tenant));
            Assert.NotNull(await banks.ExportAsync(tenant));

            await categories.WipeAsync(tenant);
            await banks.WipeAsync(tenant);

            Assert.False(await categories.HasDataAsync(tenant));
            Assert.False(await banks.HasDataAsync(tenant));
            Assert.True(await categories.HasDataAsync(other));   // the other household keeps its rows
        }
    }
}
