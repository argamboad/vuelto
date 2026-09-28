using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Api.Configuration;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;

namespace Vuelto.Api.Tests;

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

    // ── Rotation grace window (ADR-002 addendum, 2026-09-18) ─────────────────────────────────────
    // A rotated-out token presented again shortly after its rotation, while its successor is still
    // live, is a benign race (two tabs refreshing at once; a refresh whose response was lost) — not
    // theft. Anything else stays Reuse.

    private static readonly DateTimeOffset GraceEpoch = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Issues a token and rotates it the way POST /refresh does: old → new, linked.</summary>
    private static async Task<(IssuedRefreshToken Old, IssuedRefreshToken New)> RotateAsync(RefreshTokenService sut, Guid userId)
    {
        var old = await sut.IssueRefreshTokenAsync(userId, "127.0.0.1", "google");
        var successor = await sut.IssueRefreshTokenAsync(userId, "127.0.0.1", "google");
        await sut.MarkRotatedAsync(old.Token.Id, successor.Token.Id);
        return (old, successor);
    }

    [Fact]
    public async Task MarkRotated_RevokesTheOldToken_AndLinksItToItsSuccessor()
    {
        await using var db = Fixture.CreateContext();
        var clock = new FakeTimeProvider(GraceEpoch);
        var sut = new ServiceHarness(db, clock).RefreshTokenService();

        var (old, successor) = await RotateAsync(sut, Guid.CreateVersion7());

        db.ChangeTracker.Clear(); // read back what was persisted, not the tracked copy
        var stored = await db.RefreshTokens.SingleAsync(t => t.Id == old.Token.Id);
        Assert.True(stored.IsRevoked);
        Assert.Equal(GraceEpoch, stored.RotatedAt);
        Assert.Equal(successor.Token.Id, stored.ReplacedByTokenId);
        Assert.Null(await sut.ValidateRefreshTokenAsync(old.RawToken));
    }

    [Fact]
    public async Task Revoke_WithoutRotation_DoesNotStampTheRotationLink()
    {
        // Only rotation sets RotatedAt/ReplacedByTokenId — a plain revoke (logout, staff reset) never does.
        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db).RefreshTokenService();

        var issued = await sut.IssueRefreshTokenAsync(Guid.CreateVersion7(), "127.0.0.1", "google");
        await sut.RevokeRefreshTokenAsync(issued.Token.Id);
        await sut.RevokeAllUserTokensAsync(issued.Token.UserId);

        db.ChangeTracker.Clear();
        var stored = await db.RefreshTokens.SingleAsync(t => t.Id == issued.Token.Id);
        Assert.Null(stored.RotatedAt);
        Assert.Null(stored.ReplacedByTokenId);
    }

    [Fact]
    public async Task Inspect_RotatedTokenWithinGrace_WithLiveSuccessor_IsRotatedWithinGrace()
    {
        await using var db = Fixture.CreateContext();
        var clock = new FakeTimeProvider(GraceEpoch);
        var sut = new ServiceHarness(db, clock).RefreshTokenService(reuseGraceSeconds: 60);
        var userId = Guid.CreateVersion7();

        var (old, _) = await RotateAsync(sut, userId);
        clock.Advance(TimeSpan.FromSeconds(60)); // the boundary is inclusive

        var inspection = await sut.InspectRefreshTokenAsync(old.RawToken);
        Assert.Equal(RefreshTokenStatus.RotatedWithinGrace, inspection.Status);
        Assert.Equal(userId, inspection.Token!.UserId);
    }

    [Fact]
    public async Task TryConsumeGrace_IsOneShot_AndStampsTheClock()
    {
        // v4 AUTH-1 (T28, R81): the first consumer wins the stamp; a second call — a third tab, or a replay
        // racing the forgiven presentation — gets false and is treated as reuse by the caller.
        await using var db = Fixture.CreateContext();
        var clock = new FakeTimeProvider(GraceEpoch);
        var sut = new ServiceHarness(db, clock).RefreshTokenService(reuseGraceSeconds: 60);

        var (old, _) = await RotateAsync(sut, Guid.CreateVersion7());
        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.True(await sut.TryConsumeGraceAsync(old.Token.Id));
        Assert.False(await sut.TryConsumeGraceAsync(old.Token.Id));

        db.ChangeTracker.Clear();
        var stored = await db.RefreshTokens.SingleAsync(t => t.Id == old.Token.Id);
        Assert.Equal(GraceEpoch.AddSeconds(2), stored.GraceUsedAt);
        Assert.Equal(1, await sut.CountGraceUsesAsync(old.Token.UserId));
    }

    [Fact]
    public async Task Inspect_RotatedTokenWithinGrace_AfterTheGraceWasSpent_IsReuse()
    {
        // Still inside the window, successor still live — but the grace has been used on this token once.
        await using var db = Fixture.CreateContext();
        var clock = new FakeTimeProvider(GraceEpoch);
        var sut = new ServiceHarness(db, clock).RefreshTokenService(reuseGraceSeconds: 60);

        var (old, _) = await RotateAsync(sut, Guid.CreateVersion7());
        Assert.True(await sut.TryConsumeGraceAsync(old.Token.Id));
        clock.Advance(TimeSpan.FromSeconds(5));

        var inspection = await sut.InspectRefreshTokenAsync(old.RawToken);
        Assert.Equal(RefreshTokenStatus.Reuse, inspection.Status);
    }

    [Fact]
    public async Task Inspect_RotatedTokenPastGrace_IsReuse()
    {
        await using var db = Fixture.CreateContext();
        var clock = new FakeTimeProvider(GraceEpoch);
        var sut = new ServiceHarness(db, clock).RefreshTokenService(reuseGraceSeconds: 60);

        var (old, _) = await RotateAsync(sut, Guid.CreateVersion7());
        clock.Advance(TimeSpan.FromSeconds(61));

        var inspection = await sut.InspectRefreshTokenAsync(old.RawToken);
        Assert.Equal(RefreshTokenStatus.Reuse, inspection.Status);
    }

    [Fact]
    public async Task Inspect_RotatedTokenWithinGrace_ButSuccessorRevoked_IsReuse()
    {
        // Logout / revoke-all kill the successor — a stale token from another tab must never undo that.
        await using var db = Fixture.CreateContext();
        var clock = new FakeTimeProvider(GraceEpoch);
        var sut = new ServiceHarness(db, clock).RefreshTokenService(reuseGraceSeconds: 60);
        var userId = Guid.CreateVersion7();

        var (old, _) = await RotateAsync(sut, userId);
        await sut.RevokeAllUserTokensAsync(userId); // set-based, as logout does
        clock.Advance(TimeSpan.FromSeconds(5));

        var inspection = await sut.InspectRefreshTokenAsync(old.RawToken);
        Assert.Equal(RefreshTokenStatus.Reuse, inspection.Status);
    }

    [Fact]
    public async Task Inspect_RotatedTokenWithinGrace_ButSuccessorExpired_IsReuse()
    {
        await using var db = Fixture.CreateContext();
        var clock = new FakeTimeProvider(GraceEpoch);
        var sut = new ServiceHarness(db, clock).RefreshTokenService(reuseGraceSeconds: 60);

        var (old, successor) = await RotateAsync(sut, Guid.CreateVersion7());
        await db.RefreshTokens.Where(t => t.Id == successor.Token.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, GraceEpoch.AddSeconds(1)));
        clock.Advance(TimeSpan.FromSeconds(5)); // still inside the window, but the successor is past its expiry

        var inspection = await sut.InspectRefreshTokenAsync(old.RawToken);
        Assert.Equal(RefreshTokenStatus.Reuse, inspection.Status);
    }

    [Fact]
    public async Task Inspect_RotatedTokenWithinGrace_WhenGraceIsZero_IsReuse()
    {
        // ReuseGraceSeconds = 0 restores the strict behaviour: every replay is theft.
        await using var db = Fixture.CreateContext();
        var clock = new FakeTimeProvider(GraceEpoch);
        var sut = new ServiceHarness(db, clock).RefreshTokenService(reuseGraceSeconds: 0);

        var (old, _) = await RotateAsync(sut, Guid.CreateVersion7());

        var inspection = await sut.InspectRefreshTokenAsync(old.RawToken);
        Assert.Equal(RefreshTokenStatus.Reuse, inspection.Status);
    }

    [Fact]
    public async Task Inspect_RevokedButNeverRotated_IsReuse_EvenWithinTheWindow()
    {
        // A token revoked by anything other than rotation (logout, staff MFA reset) has no RotatedAt:
        // presenting it again is never a race.
        await using var db = Fixture.CreateContext();
        var clock = new FakeTimeProvider(GraceEpoch);
        var sut = new ServiceHarness(db, clock).RefreshTokenService(reuseGraceSeconds: 60);
        var userId = Guid.CreateVersion7();

        var issued = await sut.IssueRefreshTokenAsync(userId, "127.0.0.1", "google");
        await sut.IssueRefreshTokenAsync(userId, "127.0.0.1", "google"); // another live session exists
        await sut.RevokeRefreshTokenAsync(issued.Token.Id);
        clock.Advance(TimeSpan.FromSeconds(1));

        var inspection = await sut.InspectRefreshTokenAsync(issued.RawToken);
        Assert.Equal(RefreshTokenStatus.Reuse, inspection.Status);
    }
}

/// <summary>RefreshToken:* settings — defaults and the grace-window knob.</summary>
public class RefreshTokenSettingsTests
{
    private static RefreshTokenSettings Bind(params (string Key, string Value)[] values) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build());

    [Fact]
    public void EmptyConfiguration_UsesTheDefaults()
    {
        var settings = Bind();
        Assert.Equal(30, settings.ExpiryDays);
        Assert.Equal(60, settings.ReuseGraceSeconds);
    }

    [Fact]
    public void ReuseGraceSeconds_IsReadFromConfiguration()
    {
        Assert.Equal(0, Bind(("RefreshToken:ReuseGraceSeconds", "0")).ReuseGraceSeconds);
        Assert.Equal(15, Bind(("RefreshToken:ReuseGraceSeconds", "15")).ReuseGraceSeconds);
    }
}
