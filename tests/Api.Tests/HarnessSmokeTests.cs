using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Perezosoft.Api.Tests.Infrastructure;
using Perezosoft.Core.Entities;

namespace Perezosoft.Api.Tests;

/// <summary>
/// Proves the test harness itself works: a real Postgres round-trip through the
/// production <see cref="Perezosoft.Infrastructure.Persistence.AppDbContext"/>, and a
/// controllable clock. These are the two primitives the Phase 2 service tests build on.
/// </summary>
[Collection(PostgresCollection.Name)]
public class HarnessSmokeTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task RealPostgres_PersistsAndReadsBack()
    {

        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();

        await using (var db = Fixture.CreateContext())
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "QA House", CreatedAt = now, UpdatedAt = now });
            db.Users.Add(new User { Id = userId, Email = "harness@example.com", DisplayName = "Harness", CreatedAt = now, UpdatedAt = now });
            db.TenantMemberships.Add(new TenantMembership
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                UserId = userId,
                Role = TenantRoles.Owner,
                JoinedAt = now,
            });
            await db.SaveChangesAsync();
        }

        await using var read = Fixture.CreateContext();
        var membership = await read.TenantMemberships.SingleAsync(m => m.UserId == userId);

        Assert.Equal(tenantId, membership.TenantId);
        Assert.Equal(TenantRoles.Owner, membership.Role);
    }

    [Fact]
    public void FakeTimeProvider_ControlsTime()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero));
        var start = clock.GetUtcNow();

        clock.Advance(TimeSpan.FromMinutes(15));

        Assert.Equal(start.AddMinutes(15), clock.GetUtcNow());
    }
}
