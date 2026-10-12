using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Api.Features.Envelopes;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Features;

/// <summary>
/// ENV-1 on real Postgres: create with targets + cadence (normalized, trimmed), validation → 400 with
/// nothing written, the 409 reactivation offer (id + stored name), rename/retarget/deactivate/
/// reactivate, list filter, uniform 404 for foreign ids, tenant isolation for reads AND writes, and
/// the contributor. Envelopes are never seeded.
/// </summary>
[Collection(PostgresCollection.Name)]
public class EnvelopeSliceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private static EnvelopeHandler Handler(AppDbContext db, Guid tenantId) =>
        new(new EfRepository<Envelope>(db), new TestCurrentTenant { TenantId = tenantId }, new FakeTimeProvider(T0));

    private static CreateEnvelopeRequest Marchamo(string name = "Marchamo", string cadence = "monthly") =>
        new(name, 718_000m, 0m, cadence);

    [Fact]
    public async Task FirstList_IsEmpty_NothingIsSeeded()
    {
        var tenant = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);

        Assert.Empty((await Handler(db, tenant).ListAsync(true, default))!);
        Assert.Equal(0, await db.Envelopes.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenant));
    }

    [Fact]
    public async Task Create_PersistsTargetsAndCadence_TrimmedAndNormalized()
    {
        var tenant = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);

        var (created, error) = await Handler(db, tenant).CreateAsync(new("  Marchamo ", 718_000.075m, 25.504m, " Five_Week_Months "), default);

        Assert.Null(error);
        Assert.Equal("Marchamo", created!.Name);
        Assert.Equal(718_000.08m, created.AnnualTargetCrc); // money: 2 dp, half away from zero — matches NUMERIC(12,2)
        Assert.Equal(25.50m, created.AnnualTargetUsd);
        Assert.Equal("five_week_months", created.ReminderCadence);
        Assert.True(created.IsActive);

        var row = await db.Envelopes.SingleAsync(e => e.Id == created.Id);
        Assert.Equal(tenant, row.TenantId);
        Assert.Equal(T0, row.CreatedAt);
    }

    public static TheoryData<CreateEnvelopeRequest, string> Invalid => new()
    {
        { new(null, 1m, 0m, "monthly"), "name" },
        { new("   ", 1m, 0m, "monthly"), "name" },
        { new(new string('x', 101), 1m, 0m, "monthly"), "100 characters" },
        { new("Marchamo", 1m, 0m, null), "reminder_cadence" },
        { new("Marchamo", 1m, 0m, "whenever"), "reminder_cadence" },
        { new("Marchamo", -1m, 0m, "monthly"), "negative" },
        { new("Marchamo", 0m, -0.01m, "monthly"), "negative" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public async Task Create_InvalidRequest_Is400_AndWritesNothing(CreateEnvelopeRequest request, string messageMentions)
    {
        var tenant = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);

        var (created, error) = await Handler(db, tenant).CreateAsync(request, default);

        Assert.Null(created);
        Assert.Equal("invalid_request", error!.Error);
        Assert.Contains(messageMentions, error.Message);
        Assert.Equal(0, await db.Envelopes.CountAsync());
    }

    [Fact]
    public async Task ActiveClash_Is409_CaseInsensitively_WithoutAnOffer()
    {
        var tenant = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);
        var handler = Handler(db, tenant);
        await handler.CreateAsync(Marchamo(), default);

        var (dup, clash) = await handler.CreateAsync(Marchamo("MARCHAMO"), default);

        Assert.Null(dup);
        var conflict = Assert.IsType<EnvelopeConflictResponse>(clash);
        Assert.Equal("envelope_exists", conflict.Error);
        Assert.Null(conflict.ExistingId);
        Assert.Null(conflict.ExistingName);
    }

    [Fact]
    public async Task InactiveClash_OffersReactivation_AndUpdateRestoresIt()
    {
        var tenant = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);
        var handler = Handler(db, tenant);
        var (created, _) = await handler.CreateAsync(Marchamo(), default);
        await handler.UpdateAsync(created!.Id, new("Marchamo", 718_000m, 0m, "monthly", IsActive: false), default);

        var (_, clash) = await handler.CreateAsync(Marchamo("marchamo"), default);
        var offer = Assert.IsType<EnvelopeConflictResponse>(clash);
        Assert.Equal("envelope_exists_inactive", offer.Error);
        Assert.Equal(created.Id, offer.ExistingId);
        Assert.Equal("Marchamo", offer.ExistingName);

        var (restored, error) = await handler.UpdateAsync(offer.ExistingId!.Value, new(offer.ExistingName, 750_000m, 0m, "five_week_months", IsActive: true), default);
        Assert.Null(error);
        Assert.True(restored!.IsActive);
        Assert.Equal("Marchamo", restored.Name);
        Assert.Equal(750_000m, restored.AnnualTargetCrc);
        Assert.Equal("five_week_months", restored.ReminderCadence);
    }

    [Fact]
    public async Task List_ExcludesInactive_UnlessAsked_OrderedByName()
    {
        var tenant = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);
        var handler = Handler(db, tenant);
        await handler.CreateAsync(Marchamo("Viaje"), default);
        var (old, _) = await handler.CreateAsync(Marchamo("Castillo"), default);
        await handler.UpdateAsync(old!.Id, new("Castillo", 0m, 0m, "monthly", false), default);

        var active = await handler.ListAsync(false, default);
        var all = await handler.ListAsync(true, default);

        Assert.Equal(["Viaje"], active!.Select(e => e.Name));
        Assert.Equal(["Castillo", "Viaje"], all!.Select(e => e.Name));
        Assert.False(all![0].IsActive);
    }

    [Fact]
    public async Task Update_RenameClash_Is409_OwnName_IsFine_UnknownId_Is404()
    {
        var tenant = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);
        var handler = Handler(db, tenant);
        var (a, _) = await handler.CreateAsync(Marchamo("A"), default);
        var (b, _) = await handler.CreateAsync(Marchamo("B"), default);

        var (_, clash) = await handler.UpdateAsync(b!.Id, new("a", 1m, 0m, "monthly", true), default);
        Assert.Equal("envelope_exists", Assert.IsType<EnvelopeConflictResponse>(clash).Error);

        var (_, sameName) = await handler.UpdateAsync(a!.Id, new("A", 1m, 0m, "monthly", true), default);
        Assert.Null(sameName);

        var (_, missing) = await handler.UpdateAsync(Guid.CreateVersion7(), new("X", 1m, 0m, "monthly", true), default);
        Assert.Equal("not_found", missing!.Error);
    }

    [Fact]
    public async Task Envelopes_AreInvisibleAndUnwritable_AcrossTenants()
    {
        var tenantA = Guid.CreateVersion7();
        var tenantB = Guid.CreateVersion7();
        Guid aId;

        await using (var db = Fixture.CreateContext(tenantA))
            aId = (await Handler(db, tenantA).CreateAsync(Marchamo("A only"), default)).Envelope!.Id;

        await using (var db = Fixture.CreateContext(tenantB))
        {
            var handler = Handler(db, tenantB);
            Assert.DoesNotContain(await handler.ListAsync(true, default) ?? [], e => e.Name == "A only");            // read negative
            var (_, error) = await handler.UpdateAsync(aId, new("Hijacked", 0m, 0m, "monthly", false), default);
            Assert.Equal("not_found", error!.Error);                                                                 // write negative: 404, not 403
            Assert.Null((await handler.CreateAsync(Marchamo("A only"), default)).Error);                            // same name is free in B
        }

        await using (var db = Fixture.CreateContext(tenantA))
        {
            var a = await db.Envelopes.IgnoreQueryFilters().SingleAsync(e => e.Id == aId);
            Assert.Equal("A only", a.Name);
            Assert.True(a.IsActive);
        }
    }

    [Fact]
    public async Task Contributor_ReportsWipesAndExports_PerTenant()
    {
        var tenant = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        await using (var db = Fixture.CreateContext(tenant))
            await Handler(db, tenant).CreateAsync(Marchamo(), default);
        await using (var db = Fixture.CreateContext(other))
            await Handler(db, other).CreateAsync(Marchamo(), default);

        await using (var db = Fixture.CreateContext(tenant))
        {
            var contributor = new EnvelopeDataContributor(new EfRepository<Envelope>(db));
            Assert.Equal("envelopes", contributor.ExportKey);
            Assert.True(await contributor.HasDataAsync(tenant));
            Assert.NotNull(await contributor.ExportAsync(tenant));

            await contributor.WipeAsync(tenant);

            Assert.False(await contributor.HasDataAsync(tenant));
            Assert.Null(await contributor.ExportAsync(tenant));
            Assert.True(await contributor.HasDataAsync(other)); // the other household keeps its rows
        }
    }
}
