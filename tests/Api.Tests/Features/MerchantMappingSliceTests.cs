using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Api.Features.Email;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Features;

/// <summary>
/// EMAIL-5 on real Postgres (donor US-029 + WU-5 B12): create trims and persists with the category name,
/// validation codes, one rule per merchant text per household regardless of casing (pre-check and the
/// unique index agree on <c>mapping_exists</c>), update and delete are tenant-scoped (uniform 404), list
/// orders by pattern and names inactive categories, learn-on-confirm never overwrites, and the
/// dissolution contributor wipes and exports.
/// </summary>
[Collection(PostgresCollection.Name)]
public class MerchantMappingSliceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed record Ctx(AppDbContext Db, Guid Tenant, MerchantMappingHandler Handler, Guid GroceriesId, Guid DiningId, Guid InactiveId);

    private async Task<Ctx> ContextAsync(Guid? tenantId = null)
    {
        var tenant = tenantId ?? Guid.CreateVersion7();
        var db = Fixture.CreateContext(tenant);
        var groceries = new Category { TenantId = tenant, Name = "Groceries", CreatedAt = T0, UpdatedAt = T0 };
        var dining = new Category { TenantId = tenant, Name = "Dining", CreatedAt = T0, UpdatedAt = T0 };
        var old = new Category { TenantId = tenant, Name = "Old", IsActive = false, CreatedAt = T0, UpdatedAt = T0 };
        db.AddRange(groceries, dining, old);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return Build(db, tenant, groceries.Id, dining.Id, old.Id);
    }

    private static Ctx Build(AppDbContext db, Guid tenant, Guid groceries, Guid dining, Guid inactive) => new(db, tenant,
        new MerchantMappingHandler(new EfRepository<MerchantCategoryMapping>(db), new EfRepository<Category>(db), new TestCurrentTenant { TenantId = tenant }, new FakeTimeProvider(T0), NullLogger<MerchantMappingHandler>.Instance),
        groceries, dining, inactive);

    private Ctx Sibling(Ctx c) => Build(Fixture.CreateContext(c.Tenant), c.Tenant, c.GroceriesId, c.DiningId, c.InactiveId);

    [Fact]
    public async Task Create_TrimsThePattern_PersistsTheKey_AndNamesTheCategory()
    {
        var c = await ContextAsync();
        var (created, error) = await c.Handler.CreateAsync(new("  AutoMercado ", c.GroceriesId, null), default);

        Assert.Null(error);
        Assert.Equal(("AutoMercado", c.GroceriesId, "Groceries", null), (created!.MerchantPattern, created.CategoryId, created.CategoryName, created.SuggestedClass));
        c.Db.ChangeTracker.Clear();
        var stored = await c.Db.MerchantCategoryMappings.SingleAsync();
        Assert.Equal(("automercado", c.Tenant), (stored.PatternKey, stored.TenantId));
    }

    [Fact]
    public async Task Create_Validates_PatternClassAndCategory()
    {
        var c = await ContextAsync();
        var foreign = await ContextAsync();
        Assert.Equal("invalid_request", (await c.Handler.CreateAsync(new("  ", c.GroceriesId, null), default)).Error!.Error);
        Assert.Equal("invalid_request", (await c.Handler.CreateAsync(new("A", c.GroceriesId, "inflow"), default)).Error!.Error);
        Assert.Equal("invalid_request", (await c.Handler.CreateAsync(new("A", null, null), default)).Error!.Error);
        Assert.Equal("invalid_request", (await c.Handler.CreateAsync(new("A", c.InactiveId, null), default)).Error!.Error);
        Assert.Equal("invalid_request", (await c.Handler.CreateAsync(new("A", foreign.GroceriesId, null), default)).Error!.Error); // another household's category does not exist here
        Assert.Equal("invalid_request", (await c.Handler.CreateAsync(new(new string('x', 201), c.GroceriesId, null), default)).Error!.Error);
        Assert.Empty(await c.Handler.ListAsync(default));

        var (ok, _) = await c.Handler.CreateAsync(new("Taco Bell", c.DiningId, " Extraordinary "), default);
        Assert.Equal("extraordinary", ok!.SuggestedClass);
    }

    [Fact]
    public async Task Create_OneRulePerMerchantText_RegardlessOfCasing_AlsoUnderARace()
    {
        var c = await ContextAsync();
        Assert.Null((await c.Handler.CreateAsync(new("AutoMercado", c.GroceriesId, null), default)).Error);
        var dupe = await c.Handler.CreateAsync(new("AUTOMERCADO ", c.DiningId, null), default);
        Assert.Equal("mapping_exists", dupe.Error!.Error);

        var one = Sibling(c);
        var two = Sibling(c);
        var results = await Task.WhenAll(
            Task.Run(() => one.Handler.CreateAsync(new("Walmart", c.GroceriesId, null), default)),
            Task.Run(() => two.Handler.CreateAsync(new("walmart", c.DiningId, null), default)));
        Assert.Equal(1, results.Count(r => r.Error is null));
        Assert.Equal("mapping_exists", Assert.Single(results, r => r.Error is not null).Error!.Error);
        Assert.Equal(2, await c.Db.MerchantCategoryMappings.CountAsync());
    }

    [Fact]
    public async Task Update_ChangesTheRule_RejectsAClash_AndIsTenantScoped()
    {
        var c = await ContextAsync();
        var a = (await c.Handler.CreateAsync(new("AutoMercado", c.GroceriesId, null), default)).Mapping!;
        var b = (await c.Handler.CreateAsync(new("Walmart", c.GroceriesId, null), default)).Mapping!;

        var (updated, error) = await c.Handler.UpdateAsync(a.Id, new("Auto Mercado", c.DiningId, "unplanned_essential"), default);
        Assert.Null(error);
        Assert.Equal(("Auto Mercado", c.DiningId, "Dining", "unplanned_essential"), (updated!.MerchantPattern, updated.CategoryId, updated.CategoryName, updated.SuggestedClass));
        Assert.Null((await c.Handler.UpdateAsync(a.Id, new("AUTO MERCADO", c.DiningId, null), default)).Error); // same rule, only casing — allowed
        Assert.Equal("mapping_exists", (await c.Handler.UpdateAsync(a.Id, new("WALMART", c.DiningId, null), default)).Error!.Error);
        Assert.Equal("invalid_request", (await c.Handler.UpdateAsync(b.Id, new("Walmart", c.InactiveId, null), default)).Error!.Error);

        var other = await ContextAsync();
        Assert.Equal("not_found", (await other.Handler.UpdateAsync(a.Id, new("X", other.GroceriesId, null), default)).Error!.Error);
        Assert.False(await other.Handler.DeleteAsync(a.Id, default));
        Assert.Equal(2, (await c.Handler.ListAsync(default)).Count);
    }

    [Fact]
    public async Task List_OrdersByPattern_AndStillNamesAnInactiveCategory()
    {
        var c = await ContextAsync();
        var rule = (await c.Handler.CreateAsync(new("Zeta", c.GroceriesId, null), default)).Mapping!;
        await c.Handler.CreateAsync(new("alpha", c.DiningId, null), default);
        await c.Handler.CreateAsync(new("Mid", c.DiningId, null), default);

        var groceries = await c.Db.Categories.SingleAsync(x => x.Id == c.GroceriesId);
        groceries.IsActive = false;
        await c.Db.SaveChangesAsync();

        var list = await c.Handler.ListAsync(default);
        Assert.Equal(["alpha", "Mid", "Zeta"], list.Select(m => m.MerchantPattern));
        Assert.Equal("Groceries", list.Single(m => m.Id == rule.Id).CategoryName);

        Assert.True(await c.Handler.DeleteAsync(rule.Id, default));
        Assert.False(await c.Handler.DeleteAsync(rule.Id, default));
        Assert.Equal(2, (await c.Handler.ListAsync(default)).Count);
    }

    [Fact]
    public async Task Remember_CreatesARuleOnce_AndNeverOverwritesAnExistingOne()
    {
        var c = await ContextAsync();
        Assert.False(await c.Handler.RememberAsync("  ", c.GroceriesId, "budgeted", default));
        Assert.True(await c.Handler.RememberAsync("TACO BELL PLAZA REAL C", c.DiningId, "extraordinary", default));
        Assert.False(await c.Handler.RememberAsync("taco bell plaza real c", c.GroceriesId, "budgeted", default));

        var rule = Assert.Single(await c.Handler.ListAsync(default));
        Assert.Equal(("TACO BELL PLAZA REAL C", c.DiningId, "extraordinary"), (rule.MerchantPattern, rule.CategoryId, rule.SuggestedClass));
    }

    [Fact]
    public async Task Contributor_ReportsWipesAndExports_OnlyTheTenantsRules()
    {
        var a = await ContextAsync();
        var b = await ContextAsync();
        await a.Handler.CreateAsync(new("AutoMercado", a.GroceriesId, null), default);
        await b.Handler.CreateAsync(new("Walmart", b.GroceriesId, "extraordinary"), default);

        await using var admin = Fixture.CreateContext(a.Tenant); // dissolution enters the target tenant (ADR-003) — the wipe runs inside it
        var contributor = new MerchantMappingDataContributor(new EfRepository<MerchantCategoryMapping>(admin));
        Assert.Equal("merchant_mappings", contributor.ExportKey);
        Assert.True(await contributor.HasDataAsync(a.Tenant));
        Assert.NotNull(await contributor.ExportAsync(a.Tenant));
        Assert.Null(await contributor.ExportAsync(Guid.CreateVersion7()));

        await contributor.WipeAsync(a.Tenant);
        Assert.False(await contributor.HasDataAsync(a.Tenant));
        Assert.True(await contributor.HasDataAsync(b.Tenant));
    }
}
