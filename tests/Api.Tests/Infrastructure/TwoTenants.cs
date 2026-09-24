namespace Vuelto.Api.Tests.Infrastructure;

/// <summary>
/// The one way a cross-tenant test arranges its world (R146, v4 audit H4/T10): two tenants, each seeded through
/// the test's own path — a service, a context, the HTTP factory — and never the same tenant twice. A test
/// named <c>OtherTenant</c>/<c>CrossTenant</c>/<c>IsTenantScoped</c> must come through here
/// (<c>CrossTenantTestSeedingTests</c> fails otherwise): asking for a random id that exists nowhere proves
/// nothing, because "unknown" and "belongs to someone else" give the same answer whether or not the tenant
/// filter is there. <see cref="TwoTenants{T}.Other"/> and <see cref="TwoTenants{T}.OtherSeed"/> are the real
/// row the test then fails to reach.
/// </summary>
public static class TwoTenants
{
    /// <summary>Mints two tenant ids and seeds each with <paramref name="seed"/>.</summary>
    public static Task<TwoTenants<T>> SeedAsync<T>(Func<Guid, Task<T>> seed) =>
        SeedPairAsync(async () => { var tenant = Guid.CreateVersion7(); return (tenant, await seed(tenant)); });

    /// <summary>Mints two tenant ids and seeds each with <paramref name="seed"/>.</summary>
    public static async Task<TwoTenants<Guid>> SeedAsync(Func<Guid, Task> seed) =>
        await SeedAsync<Guid>(async tenant => { await seed(tenant); return tenant; });

    /// <summary>For a seeder that creates its own tenant (the HTTP factory's users): <paramref name="tenantOf"/> reads it back.</summary>
    public static Task<TwoTenants<T>> SeedAsync<T>(Func<Task<T>> seed, Func<T, Guid> tenantOf) =>
        SeedPairAsync(async () => { var row = await seed(); return (tenantOf(row), row); });

    private static async Task<TwoTenants<T>> SeedPairAsync<T>(Func<Task<(Guid Tenant, T Seed)>> seedOne)
    {
        var mine = await seedOne();
        var other = await seedOne();
        if (mine.Tenant == Guid.Empty || other.Tenant == Guid.Empty || mine.Tenant == other.Tenant)
            throw new InvalidOperationException(
                $"A cross-tenant arrange needs two distinct, real tenants; got {mine.Tenant} and {other.Tenant}.");
        return new TwoTenants<T>(mine.Tenant, mine.Seed, other.Tenant, other.Seed);
    }
}

/// <summary>The seeded pair: the tenant the test acts as, and the one whose rows it must not reach.</summary>
public sealed record TwoTenants<T>(Guid Mine, T MineSeed, Guid Other, T OtherSeed);
