using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Api.Features.Income;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Features;

/// <summary>
/// INCOME-1 on real Postgres: the household's income lines — create/update with every field rule (currency, kind, pay
/// period, positive amount, biweekly pay days, a member who must be in the household but may be kept once they left),
/// the catalog rules (case-insensitive name, 409 with the reactivation offer, 404 for another household, reorder of
/// exactly the active set), needs_review cleared by an update, and the two data hooks (export/wipe for the household,
/// member cleared on account erasure in every household).
/// </summary>
[Collection(PostgresCollection.Name)]
public class IncomeSliceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private sealed record Ctx(AppDbContext Db, Guid Tenant, Guid Member, IncomeHandler Handler);

    /// <summary>A household with one member (a real user + membership) the lines may name.</summary>
    private async Task<Ctx> SeedAsync()
    {
        var tenant = Guid.CreateVersion7();
        var db = Fixture.CreateContext(tenant);
        var user = new User { Email = $"ana-{tenant:N}@example.com", DisplayName = "Ana", CreatedAt = T0, UpdatedAt = T0 };
        db.Add(new Tenant { Id = tenant, Name = "Casa", CreatedAt = T0, UpdatedAt = T0 });
        db.Add(user);
        db.Add(new TenantMembership { TenantId = tenant, UserId = user.Id, Role = TenantRoles.Owner, JoinedAt = T0 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var handler = new IncomeHandler(new EfRepository<IncomeLine>(db), new EfRepository<MonthIncome>(db), new TenantRepository(db), new TestCurrentTenant { TenantId = tenant }, new FakeTimeProvider(T0));
        return new Ctx(db, tenant, user.Id, handler);
    }

    private static IncomeLineRequest Weekly(string name = "Allan salary", Guid? member = null, decimal amount = 500m) =>
        new(name, member, "usd", "fixed", "Weekly", amount);

    [Fact]
    public async Task Create_NormalizesTheFields_AndAppends()
    {
        var c = await SeedAsync();

        var (first, error) = await c.Handler.CreateAsync(Weekly(" Allan salary ", c.Member, 500.004m), default);
        var (second, _) = await c.Handler.CreateAsync(new("Son", null, "CRC", "fixed", "monthly", 600_000m), default);
        var (third, _) = await c.Handler.CreateAsync(new("Freelance", null, "USD", "Variable", "biweekly", 300m, [31, 15]), default);

        Assert.Null(error);
        Assert.Equal(("Allan salary", c.Member, "USD", "fixed", "weekly", 500m, 0, true, false),
            (first!.Name, first.MemberUserId!.Value, first.Currency, first.Kind, first.PayPeriod, first.Amount, first.SortOrder, first.IsActive, first.NeedsReview));
        Assert.Null(first.PayDays);
        Assert.Equal((1, "monthly"), (second!.SortOrder, second.PayPeriod));
        Assert.Equal(("variable", "biweekly", 2), (third!.Kind, third.PayPeriod, third.SortOrder));
        Assert.Equal([15, 31], third.PayDays); // ordered
        Assert.Equal(3, await c.Db.IncomeLines.CountAsync());
    }

    [Fact]
    public async Task Biweekly_DefaultsToTheFifteenthAndTheLastDay()
    {
        var c = await SeedAsync();
        var (line, _) = await c.Handler.CreateAsync(new("Quincena", null, "CRC", "fixed", "biweekly", 400_000m), default);
        Assert.Equal([15, 31], line!.PayDays);
    }

    public static TheoryData<IncomeLineRequest, string> InvalidRequests => new()
    {
        { new(" ", null, "USD", "fixed", "weekly", 1m), "name" },
        { new(new string('x', 101), null, "USD", "fixed", "weekly", 1m), "100" },
        { new("x", null, "EUR", "fixed", "weekly", 1m), "currency" },
        { new("x", null, "USD", "sometimes", "weekly", 1m), "kind" },
        { new("x", null, "USD", "fixed", "yearly", 1m), "pay_period" },
        { new("x", null, "USD", "fixed", "weekly", 0m), "amount" },
        { new("x", null, "USD", "fixed", "weekly", -5m), "amount" },
        { new("x", null, "USD", "fixed", "monthly", 1m, [15, 31]), "pay_days" },
        { new("x", null, "USD", "fixed", "biweekly", 1m, [15]), "pay_days" },
        { new("x", null, "USD", "fixed", "biweekly", 1m, [15, 15]), "pay_days" },
        { new("x", null, "USD", "fixed", "biweekly", 1m, [0, 15]), "pay_days" },
        { new("x", null, "USD", "fixed", "biweekly", 1m, [15, 32]), "pay_days" },
        { new("x", Guid.CreateVersion7(), "USD", "fixed", "weekly", 1m), "member" },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Create_RefusesBadInput_AndWritesNothing(IncomeLineRequest request, string expectedInMessage)
    {
        var c = await SeedAsync();

        var (line, error) = await c.Handler.CreateAsync(request, default);

        Assert.Null(line);
        Assert.Equal("invalid_request", error!.Error);
        Assert.Contains(expectedInMessage, error.Message);
        Assert.Equal(0, await c.Db.IncomeLines.CountAsync());
    }

    [Fact]
    public async Task Names_AreUniqueCaseInsensitively_WithTheReactivationOffer()
    {
        var c = await SeedAsync();
        var (salary, _) = await c.Handler.CreateAsync(Weekly(), default);

        var (_, active) = await c.Handler.CreateAsync(Weekly("ALLAN SALARY"), default);
        var conflict = Assert.IsType<IncomeConflictResponse>(active);
        Assert.Equal(("income_exists", null, null), (conflict.Error, conflict.ExistingId, conflict.ExistingName));

        await c.Handler.UpdateAsync(salary!.Id, Weekly() with { IsActive = false }, default);
        var (_, inactive) = await c.Handler.CreateAsync(Weekly("allan salary"), default);
        var offer = Assert.IsType<IncomeConflictResponse>(inactive);
        Assert.Equal(("income_exists_inactive", salary.Id, "Allan salary"), (offer.Error, offer.ExistingId, offer.ExistingName));

        var (other, _) = await c.Handler.CreateAsync(Weekly("Bonus"), default);
        var (_, rename) = await c.Handler.UpdateAsync(other!.Id, Weekly("allan SALARY"), default);
        Assert.Equal("income_exists", rename!.Error);
    }

    [Fact]
    public async Task Update_ChangesEverything_ButTheOrder_AndClearsNeedsReview()
    {
        var c = await SeedAsync();
        var line = new IncomeLine { TenantId = c.Tenant, Name = "Primary income", Currency = "USD", PayPeriod = PayPeriods.Weekly, Amount = 500m, SortOrder = 4, NeedsReview = true, CreatedAt = T0, UpdatedAt = T0 };
        c.Db.Add(line);
        await c.Db.SaveChangesAsync();
        c.Db.ChangeTracker.Clear();

        var (updated, error) = await c.Handler.UpdateAsync(line.Id, new("Allan salary", c.Member, "CRC", "variable", "biweekly", 250_000m, [1, 16], IsActive: false), default);

        Assert.Null(error);
        Assert.Equal(("Allan salary", c.Member, "CRC", "variable", "biweekly", 250_000m, 4, false, false),
            (updated!.Name, updated.MemberUserId!.Value, updated.Currency, updated.Kind, updated.PayPeriod, updated.Amount, updated.SortOrder, updated.IsActive, updated.NeedsReview));
        Assert.Equal([1, 16], updated.PayDays);

        // Switching to monthly clears the pay days.
        var (monthly, _) = await c.Handler.UpdateAsync(line.Id, new("Allan salary", c.Member, "CRC", "fixed", "monthly", 900_000m), default);
        Assert.Null(monthly!.PayDays);
        Assert.Null((await c.Db.IncomeLines.AsNoTracking().SingleAsync()).PayDay1);
    }

    [Fact]
    public async Task Update_ANewMember_FollowsTheLineIntoItsMonths_ButNothingElseDoes()
    {
        // The migrated month rows had no member; naming the line's member re-labels them. Amounts, labels, currencies,
        // rows of other lines, one-offs, and a row whose member was set to someone else by hand stay as they were.
        var c = await SeedAsync();
        var (line, _) = await c.Handler.CreateAsync(Weekly("Primary income", null, 1_787.5m), default);
        var (other, _) = await c.Handler.CreateAsync(new("Rent", null, "CRC", "fixed", "monthly", 250_000m), default);
        var elsewhere = Guid.CreateVersion7(); // a member who has since left, set on one row by hand
        var september = new Month { TenantId = c.Tenant, Year = 2026, MonthNumber = 9, WeekCount = 5, Week1StartDate = new DateOnly(2026, 8, 25), CreatedAt = T0, UpdatedAt = T0 };
        var october = new Month { TenantId = c.Tenant, Year = 2026, MonthNumber = 10, WeekCount = 4, Week1StartDate = new DateOnly(2026, 9, 29), CreatedAt = T0, UpdatedAt = T0 };
        MonthIncome Row(Month m, Guid? lineId, string label, decimal amount, Guid? member = null) => new()
        {
            TenantId = c.Tenant, MonthId = m.Id, IncomeLineId = lineId, Label = label, MemberUserId = member, Currency = "USD",
            Amount = amount, PlannedAmount = lineId is null ? null : amount, CreatedAt = T0, UpdatedAt = T0,
        };
        c.Db.AddRange(september, october);
        c.Db.AddRange(
            Row(september, line!.Id, "Primary income", 8_950m),
            Row(october, line.Id, "Old salary name", 7_150m),
            Row(october, line.Id, "Primary income (split)", 100m, elsewhere),
            Row(september, other!.Id, "Rent", 250_000m),
            Row(september, null, "Sold the bike", 300m));
        await c.Db.SaveChangesAsync();
        c.Db.ChangeTracker.Clear();

        var (updated, error) = await c.Handler.UpdateAsync(line.Id, Weekly("Primary income", c.Member, 1_790m) with { IsActive = true }, default);

        Assert.Null(error);
        Assert.Equal(c.Member, updated!.MemberUserId);
        var rows = await c.Db.MonthIncomes.AsNoTracking().OrderBy(r => r.Amount).ToListAsync();
        Assert.Equal(
            [
                ("Primary income (split)", (Guid?)elsewhere, 100m),
                ("Sold the bike", null, 300m),
                ("Old salary name", c.Member, 7_150m),
                ("Primary income", c.Member, 8_950m),
                ("Rent", null, 250_000m),
            ],
            rows.Select(r => (r.Label, r.MemberUserId, r.Amount)));

        // Back to the household: the rows that followed follow again; the hand-set one still doesn't.
        await c.Handler.UpdateAsync(line.Id, Weekly("Primary income", null, 1_790m), default);
        var after = await c.Db.MonthIncomes.AsNoTracking().Where(r => r.IncomeLineId == line.Id).ToDictionaryAsync(r => r.Amount, r => r.MemberUserId);
        Assert.Equal((null, null, (Guid?)elsewhere), (after[8_950m], after[7_150m], after[100m]));
    }

    [Fact]
    public async Task Update_WithTheSameMember_TouchesNoMonth()
    {
        var c = await SeedAsync();
        var (line, _) = await c.Handler.CreateAsync(Weekly("Salary", c.Member), default);
        var month = new Month { TenantId = c.Tenant, Year = 2026, MonthNumber = 9, WeekCount = 5, Week1StartDate = new DateOnly(2026, 8, 25), CreatedAt = T0, UpdatedAt = T0 };
        c.Db.Add(month);
        c.Db.Add(new MonthIncome { TenantId = c.Tenant, MonthId = month.Id, IncomeLineId = line!.Id, Label = "Salary", MemberUserId = null, Currency = "USD", Amount = 1m, CreatedAt = T0, UpdatedAt = T0 });
        await c.Db.SaveChangesAsync();
        c.Db.ChangeTracker.Clear();

        await c.Handler.UpdateAsync(line.Id, Weekly("Salary", c.Member, 600m), default);

        Assert.Null((await c.Db.MonthIncomes.AsNoTracking().SingleAsync()).MemberUserId); // the member didn't change, so the months are left alone
    }

    [Fact]
    public async Task Update_KeepsAMemberWhoLeft_ButRefusesNamingAnotherOutsider()
    {
        var c = await SeedAsync();
        var gone = Guid.CreateVersion7(); // not (or no longer) a member
        var line = new IncomeLine { TenantId = c.Tenant, Name = "Ana salary", MemberUserId = gone, Currency = "USD", PayPeriod = PayPeriods.Monthly, Amount = 900m, CreatedAt = T0, UpdatedAt = T0 };
        c.Db.Add(line);
        await c.Db.SaveChangesAsync();
        c.Db.ChangeTracker.Clear();

        var (kept, error) = await c.Handler.UpdateAsync(line.Id, new("Ana salary (old)", gone, "USD", "fixed", "monthly", 900m), default);
        Assert.Null(error);
        Assert.Equal(gone, kept!.MemberUserId);

        var (_, outsider) = await c.Handler.UpdateAsync(line.Id, new("Ana salary (old)", Guid.CreateVersion7(), "USD", "fixed", "monthly", 900m), default);
        Assert.Equal("invalid_request", outsider!.Error);

        var (cleared, _) = await c.Handler.UpdateAsync(line.Id, new("Ana salary (old)", null, "USD", "fixed", "monthly", 900m), default);
        Assert.Null(cleared!.MemberUserId);
    }

    [Fact]
    public async Task List_IsInOrder_ActiveOnlyUnlessAsked()
    {
        var c = await SeedAsync();
        var (a, _) = await c.Handler.CreateAsync(Weekly("A"), default);
        var (b, _) = await c.Handler.CreateAsync(Weekly("B"), default);
        await c.Handler.UpdateAsync(a!.Id, Weekly("A") with { IsActive = false }, default);

        Assert.Equal(["B"], (await c.Handler.ListAsync(false, default))!.Select(l => l.Name));
        Assert.Equal(["A", "B"], (await c.Handler.ListAsync(true, default))!.Select(l => l.Name));
        Assert.NotNull(b);
    }

    [Fact]
    public async Task Reorder_NeedsExactlyTheActiveSet()
    {
        var c = await SeedAsync();
        var (a, _) = await c.Handler.CreateAsync(Weekly("A"), default);
        var (b, _) = await c.Handler.CreateAsync(Weekly("B"), default);
        var (off, _) = await c.Handler.CreateAsync(Weekly("Off"), default);
        await c.Handler.UpdateAsync(off!.Id, Weekly("Off") with { IsActive = false }, default);

        Assert.Equal("invalid_request", (await c.Handler.ReorderAsync(new([a!.Id]), default))!.Error);
        Assert.Equal("invalid_request", (await c.Handler.ReorderAsync(new([a.Id, b!.Id, off.Id]), default))!.Error);
        Assert.Equal("invalid_request", (await c.Handler.ReorderAsync(new([a.Id, a.Id]), default))!.Error);
        Assert.Equal("invalid_request", (await c.Handler.ReorderAsync(new(null), default))!.Error);

        Assert.Null(await c.Handler.ReorderAsync(new([b.Id, a.Id]), default));
        Assert.Equal(["B", "A"], (await c.Handler.ListAsync(false, default))!.Select(l => l.Name));
    }

    [Fact]
    public async Task AnotherHouseholdsLine_IsNotFound()
    {
        var mine = await SeedAsync();
        var theirs = await SeedAsync();
        var (line, _) = await theirs.Handler.CreateAsync(Weekly(), default);

        var (_, error) = await mine.Handler.UpdateAsync(line!.Id, Weekly("Mine now"), default);

        Assert.Equal("not_found", error!.Error);
        Assert.Empty((await mine.Handler.ListAsync(true, default))!);
    }

    [Fact]
    public async Task TheHouseholdHook_ExportsAndWipes_BothTables_OnlyForThatHousehold()
    {
        var c = await SeedAsync();
        var other = await SeedAsync();
        var (line, _) = await c.Handler.CreateAsync(Weekly(), default);
        await other.Handler.CreateAsync(Weekly(), default);
        var month = new Month { TenantId = c.Tenant, Year = 2026, MonthNumber = 9, WeekCount = 5, Week1StartDate = new DateOnly(2026, 8, 25), CreatedAt = T0, UpdatedAt = T0 };
        c.Db.Add(month);
        c.Db.Add(new MonthIncome { TenantId = c.Tenant, MonthId = month.Id, IncomeLineId = line!.Id, Label = "Allan salary", Amount = 2_500m, PlannedAmount = 2_500m, Currency = "USD", CreatedAt = T0, UpdatedAt = T0 });
        await c.Db.SaveChangesAsync();
        c.Db.ChangeTracker.Clear();
        var hook = new IncomeDataContributor(new EfRepository<IncomeLine>(c.Db), new EfRepository<MonthIncome>(c.Db));

        Assert.True(await hook.HasDataAsync(c.Tenant));
        var export = JsonSerializer.SerializeToElement(await hook.ExportAsync(c.Tenant));
        Assert.Equal("Allan salary", export.GetProperty("lines")[0].GetProperty("Name").GetString());
        Assert.Equal(2_500m, export.GetProperty("month_rows")[0].GetProperty("Amount").GetDecimal());

        await hook.WipeAsync(c.Tenant);

        Assert.False(await hook.HasDataAsync(c.Tenant));
        Assert.True(await hook.HasDataAsync(other.Tenant));
        Assert.Null(await hook.ExportAsync(c.Tenant));
    }

    [Fact]
    public async Task AccountErasure_ClearsTheMember_InEveryHousehold_AndKeepsTheAmounts()
    {
        var a = await SeedAsync();
        var b = await SeedAsync();
        var person = a.Member;
        var (inA, _) = await a.Handler.CreateAsync(Weekly(member: person), default);
        var month = new Month { TenantId = a.Tenant, Year = 2026, MonthNumber = 9, WeekCount = 5, Week1StartDate = new DateOnly(2026, 8, 25), CreatedAt = T0, UpdatedAt = T0 };
        a.Db.Add(month);
        a.Db.Add(new MonthIncome { TenantId = a.Tenant, MonthId = month.Id, Label = "Allan salary", MemberUserId = person, Amount = 2_500m, Currency = "USD", CreatedAt = T0, UpdatedAt = T0 });
        await a.Db.SaveChangesAsync();
        // The same person, recorded in a household they have since left.
        b.Db.Add(new IncomeLine { TenantId = b.Tenant, Name = "Old salary", MemberUserId = person, Currency = "USD", Amount = 1m, CreatedAt = T0, UpdatedAt = T0 });
        var bystander = new IncomeLine { TenantId = b.Tenant, Name = "Someone else", MemberUserId = b.Member, Currency = "USD", Amount = 1m, CreatedAt = T0, UpdatedAt = T0 };
        b.Db.Add(bystander);
        await b.Db.SaveChangesAsync();

        // The erasure runs in the person's own household context.
        var current = new TestCurrentTenant { TenantId = a.Tenant };
        await using var db = Fixture.CreateTestContext(current); // the filter follows this instance, so EnterTenant drives it
        var hook = new IncomeUserDataContributor(new EfRepository<IncomeLine>(db), new EfRepository<MonthIncome>(db), current);
        await hook.WipeAsync(person);

        Assert.Equal(a.Tenant, current.TenantId); // restored afterwards
        await using var check = Fixture.CreateContext();
        Assert.Equal(0, await check.IncomeLines.IgnoreQueryFilters().CountAsync(l => l.MemberUserId == person));
        Assert.Equal(0, await check.MonthIncomes.IgnoreQueryFilters().CountAsync(r => r.MemberUserId == person));
        Assert.Equal(2_500m, (await check.MonthIncomes.IgnoreQueryFilters().SingleAsync()).Amount);
        Assert.Equal(500m, (await check.IncomeLines.IgnoreQueryFilters().SingleAsync(l => l.Id == inA!.Id)).Amount);
        Assert.Equal(b.Member, (await check.IncomeLines.IgnoreQueryFilters().SingleAsync(l => l.Id == bystander.Id)).MemberUserId);
    }
}
