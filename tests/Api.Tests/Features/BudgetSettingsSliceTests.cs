using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Api.Features.Budget;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Features;

/// <summary>
/// BUDGET-1 on real Postgres: defaults before the first save, upsert into a single row, tenant
/// isolation for reads AND writes (through the platform's filter + stamping interceptor), field
/// validation, and the dissolve/export contributor.
/// </summary>
[Collection(PostgresCollection.Name)]
public class BudgetSettingsSliceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    private static BudgetSettingsHandler Handler(AppDbContext db, Guid tenantId, TimeProvider? clock = null) =>
        new(new EfRepository<BudgetSettings>(db), new TestCurrentTenant { TenantId = tenantId }, clock ?? new FakeTimeProvider(T0));

    private static UpdateBudgetSettingsRequest Valid(int weekday = 1, string anchor = MonthAnchors.FirstOfMonth) =>
        new(weekday, anchor, 1500.00m, 1800.00m, "USD", 400000m, 500000m, "crc");

    [Fact]
    public async Task Get_BeforeAnySave_ReturnsDefaults_AndWritesNothing()
    {
        var tenant = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);

        var current = await Handler(db, tenant).GetAsync(default);

        Assert.NotNull(current);
        Assert.True(current!.IsDefault);
        Assert.Equal(4, current.WeekStartWeekday);
        Assert.Equal(MonthAnchors.LastWeekdayPrev, current.MonthAnchor);
        Assert.Equal(0m, current.PrimaryIncome4w);
        Assert.Equal(Currencies.Usd, current.SecondaryIncomeCurrency);
        Assert.Null(current.UpdatedAt);
        Assert.Equal(0, await db.BudgetSettings.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Update_CreatesTheRow_ThenUpdatesTheSameRow()
    {
        var tenant = Guid.CreateVersion7();
        var clock = new FakeTimeProvider(T0);

        await using (var db = Fixture.CreateContext(tenant))
        {
            var (saved, error) = await Handler(db, tenant, clock).UpdateAsync(Valid(), default);
            Assert.Null(error);
            Assert.False(saved!.IsDefault);
            Assert.Equal(1, saved.WeekStartWeekday);
            Assert.Equal("CRC", saved.SecondaryIncomeCurrency); // normalized from "crc"
            Assert.Equal(T0, saved.UpdatedAt);
        }

        clock.Advance(TimeSpan.FromHours(1));
        await using (var db = Fixture.CreateContext(tenant))
        {
            var (saved, _) = await Handler(db, tenant, clock).UpdateAsync(Valid(weekday: 5, anchor: MonthAnchors.FirstWeekdayCurrent), default);
            Assert.Equal(5, saved!.WeekStartWeekday);
            Assert.Equal(T0.AddHours(1), saved.UpdatedAt);

            var rows = await db.BudgetSettings.IgnoreQueryFilters().ToListAsync();
            Assert.Single(rows);                              // upsert, never a second row
            Assert.Equal(T0, rows[0].CreatedAt);              // creation stamp survives the update
            Assert.Equal(MonthAnchors.FirstWeekdayCurrent, rows[0].MonthAnchor);
        }
    }

    [Fact]
    public async Task Settings_AreVisibleOnlyToTheirTenant_ForReadsAndWrites()
    {
        var tenantA = Guid.CreateVersion7();
        var tenantB = Guid.CreateVersion7();

        await using (var db = Fixture.CreateContext(tenantA))
            Assert.Null((await Handler(db, tenantA).UpdateAsync(Valid(weekday: 2), default)).Error);

        await using (var db = Fixture.CreateContext(tenantB))
        {
            var seenByB = await Handler(db, tenantB).GetAsync(default);
            Assert.True(seenByB!.IsDefault);                  // read negative: B does not see A's row

            Assert.Null((await Handler(db, tenantB).UpdateAsync(Valid(weekday: 6), default)).Error);
        }

        await using (var db = Fixture.CreateContext(tenantA))
        {
            var a = await Handler(db, tenantA).GetAsync(default);
            Assert.Equal(2, a!.WeekStartWeekday);             // write negative: B's save created B's row, A untouched
            var all = await db.BudgetSettings.IgnoreQueryFilters().OrderBy(s => s.WeekStartWeekday).ToListAsync();
            Assert.Equal(2, all.Count);
            Assert.Equal(tenantA, all[0].TenantId);
            Assert.Equal(tenantB, all[1].TenantId);
        }
    }

    [Theory]
    [InlineData(7, MonthAnchors.FirstOfMonth, 1, "USD", "USD", "week_start_weekday")]
    [InlineData(-1, MonthAnchors.FirstOfMonth, 1, "USD", "USD", "week_start_weekday")]
    [InlineData(4, "middle_of_month", 1, "USD", "USD", "month_anchor")]
    [InlineData(4, null, 1, "USD", "USD", "month_anchor")]
    [InlineData(4, MonthAnchors.FirstOfMonth, -1, "USD", "USD", "negative")]
    [InlineData(4, MonthAnchors.FirstOfMonth, 1, "EUR", "USD", "primary_income_currency")]
    [InlineData(4, MonthAnchors.FirstOfMonth, 1, "USD", "", "secondary_income_currency")]
    public async Task Update_InvalidInput_Is400_AndWritesNothing(
        int weekday, string? anchor, decimal primary4w, string primaryCurrency, string secondaryCurrency, string expectedInMessage)
    {
        var tenant = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);
        var request = new UpdateBudgetSettingsRequest(weekday, anchor, primary4w, 0, primaryCurrency, 0, 0, secondaryCurrency);

        var (saved, error) = await Handler(db, tenant).UpdateAsync(request, default);

        Assert.Null(saved);
        Assert.Equal("invalid_request", error!.Error);
        Assert.Contains(expectedInMessage, error.Message);
        Assert.Equal(0, await db.BudgetSettings.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Contributor_ReportsWipesAndExportsTenantData()
    {
        var tenant = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();

        await using (var db = Fixture.CreateContext(tenant))
            await Handler(db, tenant).UpdateAsync(Valid(), default);
        await using (var db = Fixture.CreateContext(other))
            await Handler(db, other).UpdateAsync(Valid(weekday: 3), default);

        // Wipe runs inside the dissolve's EnterTenant(target) scope, so the context is the target here.
        await using (var db = Fixture.CreateContext(tenant))
        {
            var contributor = new BudgetSettingsDataContributor(new EfRepository<BudgetSettings>(db));
            Assert.True(await contributor.HasDataAsync(tenant));
            Assert.Equal("budget_settings", contributor.ExportKey);
            Assert.NotNull(await contributor.ExportAsync(tenant));

            await contributor.WipeAsync(tenant);

            Assert.False(await contributor.HasDataAsync(tenant));
            Assert.Null(await contributor.ExportAsync(tenant));
            Assert.True(await contributor.HasDataAsync(other));   // the other household's row survives
        }
    }
}
