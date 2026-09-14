using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Api.Features.Expenses;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Features;

/// <summary>
/// EXPENSES-1 on real Postgres (donor US-016/017/054 rules): never seeded; ordered by sort_order; create
/// appends; validation (single-currency, method, required active category) writes
/// nothing; 409 offer with stored name; a category backs at most one active line ACROSS both lists;
/// names are unique per list only; update never touches sort_order; reorder needs the exact active set
/// and lands atomically; uniform 404; tenant isolation; contributors.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ExpenseLineSliceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    public sealed record Ctx(AppDbContext Db, Guid Tenant, FixedExpenseHandler Fixed, VariableExpenseHandler Variable, Guid Cat1, Guid Cat2, Guid Cat3, Guid InactiveCat);

    private async Task<Ctx> ContextAsync()
    {
        var tenant = Guid.CreateVersion7();
        var db = Fixture.CreateContext(tenant);
        Category C(string n, bool active = true) => new() { TenantId = tenant, Name = n, IsActive = active, CreatedAt = T0, UpdatedAt = T0 };
        var (c1, c2, c3, ci) = (C("Housing"), C("Food"), C("Transport"), C("Old", false));
        db.Categories.AddRange(c1, c2, c3, ci);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var current = new TestCurrentTenant { TenantId = tenant };
        var clock = new FakeTimeProvider(T0);
        var f = new EfRepository<FixedExpense>(db); var v = new EfRepository<VariableExpense>(db);
        var cats = new EfRepository<Category>(db);
        return new Ctx(db, tenant,
            new FixedExpenseHandler(f, f, v, cats, current, clock),
            new VariableExpenseHandler(v, f, v, cats, current, clock),
            c1.Id, c2.Id, c3.Id, ci.Id);
    }

    private static CreateExpenseRequest Line(string name, Guid category, decimal crc = 300_000m, decimal usd = 0m, string method = "bank_account") =>
        new(name, crc, usd, method, category);

    private static UpdateExpenseRequest Edit(string name, Guid category, decimal crc = 300_000m, decimal usd = 0m, string method = "bank_account", bool active = true) =>
        new(name, crc, usd, method, category, active);

    [Fact]
    public async Task FirstList_IsEmpty_NothingIsSeeded()
    {
        var c = await ContextAsync();
        Assert.Empty((await c.Fixed.ListAsync(true, default))!);
        Assert.Empty((await c.Variable.ListAsync(true, default))!);
    }

    [Fact]
    public async Task Create_Persists_Trims_Rounds_AndAppendsSortOrder()
    {
        var c = await ContextAsync();

        var (a, e1) = await c.Fixed.CreateAsync(Line("  Mortgage ", c.Cat1, crc: 300_000.005m), default);
        var (b, e2) = await c.Fixed.CreateAsync(Line("Netflix", c.Cat2, crc: 0m, usd: 13m, method: "Credit_Card"), default);

        Assert.Null(e1); Assert.Null(e2);
        Assert.Equal(("Mortgage", 300_000.01m, 0m, "bank_account", 0, true), (a!.Name, a.BudgetCrc, a.BudgetUsd, a.PaymentMethod, a.SortOrder, a.IsActive));
        Assert.Equal((0m, 13m, "credit_card", 1), (b!.BudgetCrc, b.BudgetUsd, b.PaymentMethod, b.SortOrder));
        Assert.Equal(c.Tenant, (await c.Db.FixedExpenses.SingleAsync(x => x.Id == a.Id)).TenantId);
    }

    public static TheoryData<Func<Ctx, CreateExpenseRequest>, string> Invalid => new()
    {
        { c => Line(" ", c.Cat1), "name" },
        { c => Line(new string('x', 101), c.Cat1), "100 characters" },
        { c => Line("X", c.Cat1, method: "cash"), "payment_method" },
        { c => Line("X", c.Cat1) with { CategoryId = null }, "category_id" },
        { c => Line("X", c.Cat1, crc: -1m), "negative" },
        { c => Line("X", c.Cat1, crc: 0m, usd: 0m), "exactly one" },
        { c => Line("X", c.Cat1, crc: 100m, usd: 5m), "exactly one" },
        { c => Line("X", Guid.CreateVersion7()), "category" },
        { c => Line("X", c.InactiveCat), "category" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public async Task Create_InvalidRequest_Is400_AndWritesNothing(Func<Ctx, CreateExpenseRequest> request, string mentions)
    {
        var c = await ContextAsync();

        var (line, error) = await c.Fixed.CreateAsync(request(c), default);

        Assert.Null(line);
        Assert.Equal("invalid_request", error!.Error);
        Assert.Contains(mentions, error.Message);
        Assert.Equal(0, await c.Db.FixedExpenses.CountAsync());
    }

    [Fact]
    public async Task NameClash_Is409_PerList_CaseInsensitively_WithTheOfferForInactive()
    {
        var c = await ContextAsync();
        await c.Fixed.CreateAsync(Line("Mortgage", c.Cat1), default);

        var (_, active) = await c.Fixed.CreateAsync(Line("MORTGAGE", c.Cat2), default);
        var offer1 = Assert.IsType<ExpenseConflictResponse>(active);
        Assert.Equal(("expense_exists", (Guid?)null), (offer1.Error, offer1.ExistingId));

        Assert.Null((await c.Variable.CreateAsync(Line("Mortgage", c.Cat2, method: "credit_card"), default)).Error); // per list: fine in the other list

        var (old, _) = await c.Fixed.CreateAsync(Line("Water", c.Cat3), default);
        await c.Fixed.UpdateAsync(old!.Id, Edit("Water", c.Cat3, active: false), default);
        var (_, inactive) = await c.Fixed.CreateAsync(Line("water", c.Cat3), default);
        var offer2 = Assert.IsType<ExpenseConflictResponse>(inactive);
        Assert.Equal(("expense_exists_inactive", old.Id, "Water"), (offer2.Error, offer2.ExistingId, offer2.ExistingName));

        var (restored, error) = await c.Fixed.UpdateAsync(offer2.ExistingId!.Value, Edit(offer2.ExistingName!, c.Cat3, crc: 15_000m, active: true), default);
        Assert.Null(error);
        Assert.True(restored!.IsActive);
        Assert.Equal(("Water", 15_000m), (restored.Name, restored.BudgetCrc));
    }

    [Fact]
    public async Task ACategoryBacksAtMostOneActiveLine_AcrossBothLists_InactiveLinesDoNotCount()
    {
        var c = await ContextAsync();
        var (mortgage, _) = await c.Fixed.CreateAsync(Line("Mortgage", c.Cat1), default);

        Assert.Contains("already backs", (await c.Fixed.CreateAsync(Line("Rent", c.Cat1), default)).Error!.Message);           // same list
        Assert.Contains("already backs", (await c.Variable.CreateAsync(Line("Groceries", c.Cat1, method: "credit_card"), default)).Error!.Message); // other list

        var (water, _) = await c.Fixed.CreateAsync(Line("Water", c.Cat2), default);
        Assert.Contains("already backs", (await c.Fixed.UpdateAsync(water!.Id, Edit("Water", c.Cat1), default)).Error!.Message); // reassign onto a taken category
        Assert.Null((await c.Fixed.UpdateAsync(mortgage!.Id, Edit("Mortgage", c.Cat1, crc: 350_000m), default)).Error);          // keeping your own category is fine

        await c.Fixed.UpdateAsync(mortgage.Id, Edit("Mortgage", c.Cat1, active: false), default);
        Assert.Null((await c.Variable.CreateAsync(Line("Groceries", c.Cat1, method: "credit_card"), default)).Error);            // the inactive line released it
    }

    [Fact]
    public async Task Update_ChangesEveryEditableField_NeverSortOrder_UnknownId_Is404()
    {
        var c = await ContextAsync();
        await c.Fixed.CreateAsync(Line("Mortgage", c.Cat1), default);
        var (line, _) = await c.Fixed.CreateAsync(Line("Water", c.Cat2), default);
        Assert.Equal(1, line!.SortOrder);

        var (updated, error) = await c.Fixed.UpdateAsync(line.Id, Edit("Agua", c.Cat3, crc: 0m, usd: 25m, method: "credit_card"), default);

        Assert.Null(error);
        Assert.Equal(("Agua", 0m, 25m, "credit_card", c.Cat3, 1, true), (updated!.Name, updated.BudgetCrc, updated.BudgetUsd, updated.PaymentMethod, updated.CategoryId, updated.SortOrder, updated.IsActive));
        Assert.Equal("not_found", (await c.Fixed.UpdateAsync(Guid.CreateVersion7(), Edit("X", c.Cat1), default)).Error!.Error);
    }

    [Fact]
    public async Task List_OrderedBySortOrder_ActiveByDefault()
    {
        var c = await ContextAsync();
        var (a, _) = await c.Variable.CreateAsync(Line("Groceries", c.Cat1, method: "credit_card"), default);
        var (b, _) = await c.Variable.CreateAsync(Line("Fuel", c.Cat2, method: "credit_card"), default);
        var (hidden, _) = await c.Variable.CreateAsync(Line("Old", c.Cat3, method: "credit_card"), default);
        await c.Variable.UpdateAsync(hidden!.Id, Edit("Old", c.Cat3, method: "credit_card", active: false), default);

        var active = await c.Variable.ListAsync(false, default);
        var all = await c.Variable.ListAsync(true, default);

        Assert.Equal([a!.Id, b!.Id], active!.Select(x => x.Id));
        Assert.Equal(3, all!.Count);
    }

    [Fact]
    public async Task Reorder_RequiresTheExactActiveSet_AndLandsTheNewOrder()
    {
        var c = await ContextAsync();
        var (a, _) = await c.Fixed.CreateAsync(Line("Mortgage", c.Cat1), default);
        var (b, _) = await c.Fixed.CreateAsync(Line("Electricity", c.Cat2), default);
        var (d, _) = await c.Fixed.CreateAsync(Line("Water", c.Cat3), default);
        await c.Fixed.UpdateAsync(d!.Id, Edit("Water", c.Cat3, active: false), default); // Water inactive: not part of the reorder set

        Assert.Contains("ordered_ids is required", (await c.Fixed.ReorderAsync(new(null), default))!.Message);
        Assert.Contains("exactly match", (await c.Fixed.ReorderAsync(new([a!.Id]), default))!.Message);                               // missing one
        Assert.Contains("exactly match", (await c.Fixed.ReorderAsync(new([a.Id, b!.Id, Guid.CreateVersion7()]), default))!.Message);   // unknown id
        Assert.Contains("exactly match", (await c.Fixed.ReorderAsync(new([a.Id, b.Id, d.Id]), default))!.Message);                     // includes the inactive one
        Assert.Contains("repeat", (await c.Fixed.ReorderAsync(new([a.Id, a.Id]), default))!.Message);

        Assert.Null(await c.Fixed.ReorderAsync(new([b.Id, a.Id]), default));

        var list = (await c.Fixed.ListAsync(false, default))!;
        Assert.Equal([b.Id, a.Id], list.Select(x => x.Id));
        Assert.Equal([0, 1], list.Select(x => x.SortOrder));
    }

    [Fact]
    public async Task Lines_AreInvisibleAndUnwritable_AcrossTenants()
    {
        var a = await ContextAsync();
        var (line, _) = await a.Fixed.CreateAsync(Line("Mortgage", a.Cat1), default);
        var b = await ContextAsync();

        Assert.Empty((await b.Fixed.ListAsync(true, default))!);
        Assert.Equal("not_found", (await b.Fixed.UpdateAsync(line!.Id, Edit("Hijacked", b.Cat1), default)).Error!.Error);
        Assert.Contains("category", (await b.Fixed.CreateAsync(Line("X", a.Cat1), default)).Error!.Message);   // another household's category does not exist
        Assert.Contains("exactly match", (await b.Fixed.ReorderAsync(new([line.Id]), default))!.Message);

        await using var verify = Fixture.CreateContext(a.Tenant);
        Assert.Equal("Mortgage", (await verify.FixedExpenses.SingleAsync(x => x.Id == line.Id)).Name);
    }

    [Fact]
    public async Task Contributors_ReportWipeAndExport_PerTenant()
    {
        var a = await ContextAsync();
        await a.Fixed.CreateAsync(Line("Mortgage", a.Cat1), default);
        await a.Variable.CreateAsync(Line("Groceries", a.Cat2, method: "credit_card"), default);
        var b = await ContextAsync();
        await b.Fixed.CreateAsync(Line("Rent", b.Cat1), default);

        var fixedC = new FixedExpenseDataContributor(new EfRepository<FixedExpense>(a.Db));
        var variableC = new VariableExpenseDataContributor(new EfRepository<VariableExpense>(a.Db));
        Assert.Equal(("fixed_expenses", "variable_expenses"), (fixedC.ExportKey, variableC.ExportKey));
        Assert.True(await fixedC.HasDataAsync(a.Tenant));
        Assert.NotNull(await variableC.ExportAsync(a.Tenant));

        await fixedC.WipeAsync(a.Tenant); await variableC.WipeAsync(a.Tenant);

        Assert.False(await fixedC.HasDataAsync(a.Tenant));
        Assert.False(await variableC.HasDataAsync(a.Tenant));
        Assert.True(await fixedC.HasDataAsync(b.Tenant));
    }
}
