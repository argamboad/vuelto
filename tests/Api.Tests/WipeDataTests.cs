using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests;

/// <summary>
/// <see cref="TenantRepository.WipeDataAsync"/> must target exactly the argument tenant
/// regardless of which tenant is "current". The invitation table is <c>ITenantScoped</c>, so
/// a naive <c>Where(i =&gt; i.TenantId == tenantId)</c> is also narrowed by the global read
/// filter and silently orphans another tenant's invitations when an off-tenant caller runs
/// the dissolve (CONF-2). These tests pin teardown to the argument, not the ambient tenant.
/// </summary>
[Collection(PostgresCollection.Name)]
public class WipeDataTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task WipeData_FromADifferentCurrentTenant_RemovesTargetTenantsInvitations()
    {
        var now = DateTimeOffset.UtcNow;
        var target = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();

        // Seed the target tenant + a pending invitation under a system (no current tenant)
        // context so the write isn't tenant-restricted.
        await using (var seed = Fixture.CreateContext())
        {
            seed.Tenants.Add(new Tenant { Id = target, Name = "target", CreatedAt = now, UpdatedAt = now });
            seed.TenantInvitations.Add(new TenantInvitation
            {
                Id = Guid.CreateVersion7(),
                TenantId = target,
                InvitedEmail = "invitee@example.com",
                InvitedByUserId = Guid.CreateVersion7(),
                Status = InvitationStatuses.Pending,
                TokenHash = Guid.NewGuid().ToString("N"),
                CreatedAt = now,
                ExpiresAt = now.AddDays(7),
            });
            await seed.SaveChangesAsync();
        }

        // Dissolve the target while a DIFFERENT tenant is current — the read filter would
        // otherwise hide the target's invitation from the delete.
        await using (var asOther = Fixture.CreateContext(other))
            await new TenantRepository(asOther).WipeDataAsync(target);

        await using var read = Fixture.CreateContext();
        Assert.Empty(await read.TenantInvitations.IgnoreQueryFilters().Where(i => i.TenantId == target).ToListAsync());
        Assert.False(await read.Tenants.AnyAsync(t => t.Id == target));
    }
}
