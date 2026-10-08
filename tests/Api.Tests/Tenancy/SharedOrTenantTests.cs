using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Tenancy;

/// <summary>
/// The shared-or-tenant shape (Arch A4, <see cref="ISharedOrTenantScoped"/>) at the EF level, on the
/// <see cref="TestSharedWidget"/> fixture: the second query filter, the write rule of the stamping interceptor, and
/// the four lifecycle facets the gate <c>EverySharedOrTenantEntity_ShipsItsLifecycleSpec</c> asks of every such
/// entity (<c>&lt;Entity&gt;_SharedOrTenant_{Dissolve,Export,SharedWrites,Erasure}_*</c>). The database wall for the
/// same rows is <c>SharedOrTenantRlsTests</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SharedOrTenantTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly Guid _mine = Guid.CreateVersion7();
    private readonly Guid _theirs = Guid.CreateVersion7();

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        // A shared row is written by a tenant-less context; owned rows say whose they are.
        await using (var system = fixture.CreateTestContext())
        {
            system.TestSharedWidgets.Add(new TestSharedWidget { Name = "shared-gin" });
            await system.SaveChangesAsync();
        }
        await using (var mine = fixture.CreateTestContext(_mine))
        {
            mine.TestSharedWidgets.Add(new TestSharedWidget { Name = "my-infusion", TenantId = _mine });
            await mine.SaveChangesAsync();
        }
        await using (var theirs = fixture.CreateTestContext(_theirs))
        {
            theirs.TestSharedWidgets.Add(new TestSharedWidget { Name = "their-infusion", TenantId = _theirs });
            await theirs.SaveChangesAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── the filter ──

    [Fact]
    public async Task Query_SeesSharedAndOwn_NeverAnotherTenants()
    {
        await using var db = fixture.CreateTestContext(_mine);
        var names = await db.TestSharedWidgets.Select(w => w.Name).OrderBy(n => n).ToListAsync();
        Assert.Equal(["my-infusion", "shared-gin"], names);
    }

    [Fact]
    public async Task NoAmbientTenant_SeesTheSharedRowsOnly()
    {
        await using var db = fixture.CreateTestContext();
        Assert.Equal(["shared-gin"], await db.TestSharedWidgets.Select(w => w.Name).ToListAsync());
    }

    // ── the write rule ──

    [Fact]
    public async Task Insert_OfASharedRow_UnderATenant_IsRefused()
    {
        // No stamping for this shape: a null TenantId under a tenant is an intent the writer must make explicit, and a
        // tenant request may not author shared rows (the database would refuse it too: SharedOrTenantRlsTests).
        await using var db = fixture.CreateTestContext(_mine);
        db.TestSharedWidgets.Add(new TestSharedWidget { Name = "sneaky-shared" });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("shared row", error.Message);
    }

    [Fact]
    public async Task Insert_ForAnotherTenant_IsRefused()
    {
        await using var db = fixture.CreateTestContext(_mine);
        db.TestSharedWidgets.Add(new TestSharedWidget { Name = "planted", TenantId = _theirs });
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Update_OfASharedRow_UnderATenant_IsRefused()
    {
        await using var db = fixture.CreateTestContext(_mine);
        var shared = await db.TestSharedWidgets.SingleAsync(w => w.TenantId == null);
        shared.Name = "renamed by a tenant";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Update_OfAnOwnRow_UnderItsTenant_Succeeds()
    {
        await using var db = fixture.CreateTestContext(_mine);
        var own = await db.TestSharedWidgets.SingleAsync(w => w.TenantId == _mine);
        own.Name = "my-infusion, aged";
        await db.SaveChangesAsync();
        await using var read = fixture.CreateTestContext(_mine);
        Assert.Contains("my-infusion, aged", await read.TestSharedWidgets.Select(w => w.Name).ToListAsync());
    }

    // ── the lifecycle facets, named for the gate ──

    [Fact]
    public async Task TestSharedWidget_SharedOrTenant_Dissolve_RemovesTheTenantsRows_AndKeepsTheShared()
    {
        await using (var db = fixture.CreateTestContext(_mine))
            await new TestSharedWidgetDataContributor(new EfRepository<TestSharedWidget>(db)).WipeAsync(_mine);

        await using var read = fixture.CreateTestContext();
        var all = await read.TestSharedWidgets.IgnoreQueryFilters().Select(w => w.Name).OrderBy(n => n).ToListAsync();
        Assert.Equal(["shared-gin", "their-infusion"], all); // only mine are gone
    }

    [Fact]
    public async Task TestSharedWidget_SharedOrTenant_Export_CarriesTheTenantsRowsOnly()
    {
        await using var db = fixture.CreateTestContext(_mine);
        var contributor = new TestSharedWidgetDataContributor(new EfRepository<TestSharedWidget>(db));
        Assert.True(await contributor.HasDataAsync(_mine));
        Assert.False(await contributor.HasDataAsync(Guid.CreateVersion7()));
        var json = JsonSerializer.Serialize(await contributor.ExportAsync(_mine));
        Assert.Contains("my-infusion", json);
        Assert.DoesNotContain("shared-gin", json);   // not the tenant's data to take
        Assert.DoesNotContain("their-infusion", json);
    }

    [Fact]
    public async Task TestSharedWidget_SharedOrTenant_SharedWrites_ComeFromTenantlessContextsOnly()
    {
        await using (var system = fixture.CreateTestContext())
        {
            system.TestSharedWidgets.Add(new TestSharedWidget { Name = "shared-orgeat" });
            await system.SaveChangesAsync(); // the sanctioned origin: no ambient tenant
        }
        await using var tenant = fixture.CreateTestContext(_mine);
        Assert.Contains("shared-orgeat", await tenant.TestSharedWidgets.Select(w => w.Name).ToListAsync());
        tenant.TestSharedWidgets.Add(new TestSharedWidget { Name = "shared-by-a-tenant" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => tenant.SaveChangesAsync());
    }

    [Fact]
    public void TestSharedWidget_SharedOrTenant_Erasure_LeavesTheTenantsRows()
    {
        // An account erasure removes a USER's data (GDPR-2): identity rows and the entities keyed by UserId that an
        // IUserDataContributor claims. A shared-or-tenant row carries no user key — it is the tenant's, and the tenant
        // outlives any one member — so the erasure never addresses this table. The user-keyed canary
        // (EveryUserKeyedEntity_IsWiredIntoAccountErasure) is what would change if it ever grew one.
        Assert.Null(typeof(TestSharedWidget).GetProperty("UserId"));
        Assert.DoesNotContain(typeof(TestSharedWidget).GetInterfaces(), i => i.Name.Contains("UserKeyed", StringComparison.Ordinal));
    }
}
