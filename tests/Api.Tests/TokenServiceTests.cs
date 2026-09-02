using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Perezosoft.Api.Services;
using Perezosoft.Api.Tests.Infrastructure;

namespace Perezosoft.Api.Tests;

/// <summary>JWT issuance/validation — every claim round-trips, including the new tenant_id.</summary>
public class JwtTokenServiceTests
{
    private static JwtTokenService Sut(TimeProvider? clock = null) =>
        new(new TestJwtSettings(), clock ?? TimeProvider.System, NullLogger<JwtTokenService>.Instance);

    [Fact]
    public void IssueAccessToken_StampsExpiryFromInjectedClock()
    {
        // Token lifetime must come from the injected clock, not ambient DateTime.UtcNow (CONF-8).
        var now = new DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);

        var jwt = Sut(clock).IssueAccessToken(Guid.CreateVersion7(), "u@example.com", "google");

        var exp = new JwtSecurityTokenHandler().ReadJwtToken(jwt).ValidTo;
        // TestJwtSettings.ExpiryMinutes == 60; JWT exp has whole-second resolution.
        Assert.Equal(now.UtcDateTime.AddMinutes(60), exp, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void IssueAndValidate_RoundTripsAllClaims()
    {
        var sut = Sut();
        var userId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();

        var jwt = sut.IssueAccessToken(userId, "u@example.com", "google", "Display", "Acme", "es", tenantId, "dark");
        var principal = sut.ValidateToken(jwt);

        Assert.NotNull(principal);
        Assert.Equal(userId.ToString(), principal!.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal("u@example.com", principal.FindFirst(ClaimTypes.Email)?.Value);
        Assert.Equal("google", principal.FindFirst("provider")?.Value);
        Assert.Equal("Display", principal.FindFirst(ClaimTypes.Name)?.Value);
        Assert.Equal("Acme", principal.FindFirst("tenant_name")?.Value);
        Assert.Equal("es", principal.FindFirst("locale")?.Value);
        Assert.Equal(tenantId.ToString(), principal.FindFirst(JwtTokenService.TenantIdClaim)?.Value);
        Assert.Equal("dark", principal.FindFirst("theme")?.Value);
    }

    [Fact]
    public void ValidateToken_Garbage_ReturnsNull() =>
        Assert.Null(Sut().ValidateToken("not-a-jwt"));
}

/// <summary>Refresh-token rotation: a revoked (rotated-out) token no longer validates.</summary>
[Collection(PostgresCollection.Name)]
public class RefreshTokenServiceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task IssueThenValidate_ReturnsToken()
    {
        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db).RefreshTokenService();
        var userId = Guid.CreateVersion7();

        var issued = await sut.IssueRefreshTokenAsync(userId, "127.0.0.1", "google");
        var validated = await sut.ValidateRefreshTokenAsync(issued.RawToken);

        Assert.NotNull(validated);
        Assert.Equal(userId, validated!.UserId);
    }

    [Fact]
    public async Task RevokedToken_NoLongerValidates()
    {
        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db).RefreshTokenService();

        var issued = await sut.IssueRefreshTokenAsync(Guid.CreateVersion7(), "127.0.0.1", "google");
        await sut.RevokeRefreshTokenAsync(issued.Token.Id); // rotation revokes the used token

        Assert.Null(await sut.ValidateRefreshTokenAsync(issued.RawToken));
    }

    [Fact]
    public async Task ValidateRefreshToken_Garbage_ReturnsNull()
    {
        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db).RefreshTokenService();

        Assert.Null(await sut.ValidateRefreshTokenAsync("nope"));
    }

    [Fact]
    public async Task Inspect_ValidToken_ReturnsValid()
    {
        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db).RefreshTokenService();

        var issued = await sut.IssueRefreshTokenAsync(Guid.CreateVersion7(), "127.0.0.1", "google");

        var inspection = await sut.InspectRefreshTokenAsync(issued.RawToken);
        Assert.Equal(RefreshTokenStatus.Valid, inspection.Status);
        Assert.Equal(issued.Token.Id, inspection.Token!.Id);
    }

    [Fact]
    public async Task Inspect_UnknownToken_ReturnsUnknown_NotReuse()
    {
        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db).RefreshTokenService();

        var inspection = await sut.InspectRefreshTokenAsync("never-issued-token");
        Assert.Equal(RefreshTokenStatus.Unknown, inspection.Status);
        Assert.Null(inspection.Token);
    }

    [Fact]
    public async Task Inspect_ExpiredToken_ReturnsExpired()
    {
        await using var db = Fixture.CreateContext();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero));
        var sut = new ServiceHarness(db, clock).RefreshTokenService(expiryDays: 30);

        var issued = await sut.IssueRefreshTokenAsync(Guid.CreateVersion7(), "127.0.0.1", "google");
        clock.Advance(TimeSpan.FromDays(31)); // past expiry

        var inspection = await sut.InspectRefreshTokenAsync(issued.RawToken);
        Assert.Equal(RefreshTokenStatus.Expired, inspection.Status);
    }

    [Fact]
    public async Task Inspect_ReplayedRotatedToken_IsReuse_AndRevokingAllKillsTheLiveToken()
    {
        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db).RefreshTokenService();
        var userId = Guid.CreateVersion7();

        // Legit login, then a rotation: the old token is revoked and a new one issued (mirrors
        // the AuthController /refresh flow).
        var first = await sut.IssueRefreshTokenAsync(userId, "127.0.0.1", "google");
        await sut.RevokeRefreshTokenAsync(first.Token.Id);
        var rotated = await sut.IssueRefreshTokenAsync(userId, "127.0.0.1", "google");

        // Attacker replays the already-rotated (revoked) token: detected as reuse, not "unknown".
        var inspection = await sut.InspectRefreshTokenAsync(first.RawToken);
        Assert.Equal(RefreshTokenStatus.Reuse, inspection.Status);
        Assert.Equal(userId, inspection.Token!.UserId);

        // The theft response revokes every live session for that user — the legit rotated token dies too.
        await sut.RevokeAllUserTokensAsync(userId);
        Assert.Null(await sut.ValidateRefreshTokenAsync(rotated.RawToken));
    }
}
