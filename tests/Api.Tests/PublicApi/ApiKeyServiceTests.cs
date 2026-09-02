using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.PublicApi;

/// <summary>
/// Drives PUBAPI (ADR-015): tenant API keys. Create returns the raw key once and stores only its hash;
/// authentication resolves a raw key to its tenant + scopes across tenants (pre-scope), failing closed on
/// unknown / wrong-prefix / revoked / expired keys; management reads are tenant-scoped. Real Postgres.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ApiKeyServiceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly Guid Creator = Guid.CreateVersion7();

    [Fact]
    public async Task Create_ReturnsRawKeyOnce_AndStoresOnlyHash()
    {
        var tenant = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);
        var svc = Build(db);

        var created = (await svc.CreateAsync(Creator, "CI", null, null, default))!;

        Assert.StartsWith("pk_", created.RawKey);
        Assert.NotEqual(created.RawKey, created.Key.KeyHash);       // raw never stored
        Assert.StartsWith("pk_", created.Key.Prefix);              // non-secret display prefix
        Assert.Equal(tenant, created.Key.TenantId);
        Assert.Equal(new HashSet<string> { ApiScopes.Read, ApiScopes.Write }, ApiScopes.Parse(created.Key.Scopes));
    }

    [Fact]
    public async Task Create_RespectsRequestedScopes_AndDropsUnknown()
    {
        var tenant = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);
        var svc = Build(db);

        var created = (await svc.CreateAsync(Creator, "read-only", ["read", "bogus"], null, default))!;

        Assert.Equal(new HashSet<string> { ApiScopes.Read }, ApiScopes.Parse(created.Key.Scopes));
    }

    [Fact]
    public async Task Create_AllInvalidScopes_IsRejected_NotGrantedAll()
    {
        // v2 audit SOLID-3: a request that names scopes but none are known must be REJECTED, not silently
        // minted with full access.
        await using var db = Fixture.CreateContext(Guid.CreateVersion7());

        var created = await Build(db).CreateAsync(Creator, "typo", ["raed", "wrte"], null, default);

        Assert.Null(created);
    }

    [Fact]
    public async Task Authenticate_ValidKey_ResolvesTenantAndScopes()
    {
        var tenant = Guid.CreateVersion7();
        string raw;
        await using (var db = Fixture.CreateContext(tenant))
            raw = (await Build(db).CreateAsync(Creator, "k", ["read"], null, default))!.RawKey;

        await using var authDb = Fixture.CreateContext(); // no ambient tenant — the key selects it
        var result = await Build(authDb).AuthenticateAsync(raw, default);

        Assert.NotNull(result);
        Assert.Equal(tenant, result!.TenantId);
        Assert.Contains(ApiScopes.Read, result.Scopes);
    }

    [Theory]
    [InlineData("not-a-key")]
    [InlineData("pk_totally-unknown")]
    public async Task Authenticate_BadKey_ReturnsNull(string raw)
    {
        await using var db = Fixture.CreateContext();
        Assert.Null(await Build(db).AuthenticateAsync(raw, default));
    }

    [Fact]
    public async Task Authenticate_RevokedKey_ReturnsNull()
    {
        var tenant = Guid.CreateVersion7();
        Guid keyId; string raw;
        await using (var db = Fixture.CreateContext(tenant))
        {
            var svc = Build(db);
            var created = (await svc.CreateAsync(Creator, "k", null, null, default))!;
            (keyId, raw) = (created.Key.Id, created.RawKey);
            Assert.True(await svc.RevokeAsync(keyId, default));
        }

        await using var authDb = Fixture.CreateContext();
        Assert.Null(await Build(authDb).AuthenticateAsync(raw, default));
    }

    [Fact]
    public async Task Authenticate_ExpiredKey_ReturnsNull()
    {
        var tenant = Guid.CreateVersion7();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        string raw;
        await using (var db = Fixture.CreateContext(tenant))
            raw = (await Build(db, clock).CreateAsync(Creator, "k", null, clock.GetUtcNow().AddHours(1), default))!.RawKey;

        clock.Advance(TimeSpan.FromHours(2)); // past expiry
        await using var authDb = Fixture.CreateContext();
        Assert.Null(await Build(authDb, clock).AuthenticateAsync(raw, default));
    }

    [Fact]
    public async Task Authenticate_StampsLastUsedAt()
    {
        var tenant = Guid.CreateVersion7();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        string raw;
        await using (var db = Fixture.CreateContext(tenant))
            raw = (await Build(db, clock).CreateAsync(Creator, "k", null, null, default))!.RawKey;

        await using (var authDb = Fixture.CreateContext())
            await Build(authDb, clock).AuthenticateAsync(raw, default);

        await using var read = Fixture.CreateContext(tenant);
        var key = await read.Set<ApiKey>().SingleAsync();
        Assert.NotNull(key.LastUsedAt);
    }

    [Fact]
    public async Task Revoke_CannotRevokeAnotherTenantsKey()
    {
        // v2 audit B8: write-side tenancy negative — a caller cannot revoke a key owned by another tenant.
        var tenantB = Guid.CreateVersion7();
        Guid bKeyId;
        await using (var db = Fixture.CreateContext(tenantB))
            bKeyId = (await Build(db).CreateAsync(Creator, "b-key", null, null, default))!.Key.Id;

        var tenantA = Guid.CreateVersion7();
        await using (var db = Fixture.CreateContext(tenantA))
            Assert.False(await Build(db).RevokeAsync(bKeyId, default)); // not found under tenant A → no-op

        await using var read = Fixture.CreateContext(tenantB);
        Assert.Null((await new EfRepository<ApiKey>(read).Query().SingleAsync()).RevokedAt); // B's key untouched
    }

    [Fact]
    public async Task List_IsTenantScoped()
    {
        var mine = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        await using (var db = Fixture.CreateContext(mine)) await Build(db).CreateAsync(Creator, "mine", null, null, default);
        await using (var db = Fixture.CreateContext(other)) await Build(db).CreateAsync(Creator, "theirs", null, null, default);

        await using var read = Fixture.CreateContext(mine);
        var keys = await Build(read).ListAsync(default);
        Assert.Single(keys);
        Assert.Equal("mine", keys[0].Name);
    }

    private static ApiKeyService Build(Vuelto.Infrastructure.Persistence.AppDbContext db, TimeProvider? clock = null) =>
        new(new EfRepository<ApiKey>(db), new TokenGenerator(), new TokenHasher(), new TestCurrentTenant(), clock ?? TimeProvider.System);
}
