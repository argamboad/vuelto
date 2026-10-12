using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Abstractions;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Api.Tests;

/// <summary>
/// The structural proof for the <c>EnterTenant</c> primitive (ADR-003): a write made while a tenant is
/// <em>entered</em> (no JWT — a webhook-like context) is stamped and scoped by the normal interceptor +
/// global filter, exactly as an authenticated request would be — <b>no <c>IgnoreQueryFilters</c> /
/// escape hatch</b>. The companion test shows that without entering, the same context is a system
/// context that does not stamp — so entering is precisely what gives such operations safe scoping.
/// </summary>
[Collection(PostgresCollection.Name)]
public class EnterTenantScopingTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task EnteredTenant_StampsAndScopes_AWrite_WithoutEscapeHatch()
    {
        var tenant = Guid.CreateVersion7();
        var current = new HttpCurrentTenant(new HttpContextAccessor()); // no JWT

        await using (var db = NewContext(current))
        using (current.EnterTenant(tenant))
        {
            db.Set<TestWidget>().Add(new TestWidget { Name = "from a webhook-like context" }); // TenantId left unset
            await db.SaveChangesAsync(); // interceptor stamps the ENTERED tenant
        }

        // Visible under the entered tenant via the normal global filter (no IgnoreQueryFilters).
        await using (var read = Fixture.CreateContext(tenant))
            Assert.Equal(tenant, (await read.Set<TestWidget>().SingleAsync()).TenantId);

        // Invisible to any other tenant.
        await using (var other = Fixture.CreateContext(Guid.CreateVersion7()))
            Assert.Empty(await other.Set<TestWidget>().ToListAsync());
    }

    [Fact]
    public async Task WithoutEntering_NoJwtContext_IsSystem_AndDoesNotStamp()
    {
        var current = new HttpCurrentTenant(new HttpContextAccessor()); // no JWT, no entered tenant

        await using (var db = NewContext(current))
        {
            db.Set<TestWidget>().Add(new TestWidget { Name = "unstamped" });
            await db.SaveChangesAsync();
        }

        await using var read = Fixture.CreateContext();
        var note = await read.Set<TestWidget>().IgnoreQueryFilters().SingleAsync();
        Assert.Equal(Guid.Empty, note.TenantId); // system context → not stamped
    }

    private TestAppDbContext NewContext(ICurrentTenant currentTenant) =>
        new(new DbContextOptionsBuilder<TestAppDbContext>().UseNpgsql(Fixture.ConnectionString).Options, currentTenant);
}
