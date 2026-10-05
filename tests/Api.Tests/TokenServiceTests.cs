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
        Assert.True(await sut.TryMarkRotatedAsync(old.Token.Id, successor.Token.Id));
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

    // --- v4 T54 (TB-AUTH-30): the rotation is serialized against revoke-all, per user ---
    // POST /refresh inspects the presented token, issues its successor and marks the rotation, in one transaction.
    // A revoke-all (logout everywhere, erasure, the theft response) that lands anywhere in between must leave no
    // live token: the mark is CONDITIONAL on the presented token still being live, and both writers take the
    // same per-user lock for the rest of their transaction, so the revoke-all either goes first (the rotation is
    // refused and its successor rolled back) or waits and then sees the successor too.

    [Fact]
    public async Task Rotation_RevokeAllLandsBeforeTheMark_IsRefused_AndTheSuccessorRollsBack()
    {
        var userId = Guid.CreateVersion7();
        string rawA;
        await using (var seed = Fixture.CreateContext())
            rawA = (await new ServiceHarness(seed).RefreshTokenService().IssueRefreshTokenAsync(userId, "127.0.0.1", "google")).RawToken;

        // The revoke-all runs on another connection right before the successor's INSERT: the inspection has
        // already said "valid", and the transaction is open.
        var faults = new DbFaultInjector().BeforeCommand(sql => sql.Contains("INSERT INTO \"RefreshTokens\""), async () =>
        {
            await using var other = Fixture.CreateContext();
            await new ServiceHarness(other).RefreshTokenService().RevokeAllUserTokensAsync(userId);
        });
        await using var db = Fixture.CreateContext(faults: faults);
        var harness = new ServiceHarness(db);
        var sut = harness.RefreshTokenService();

        var inspection = await sut.InspectRefreshTokenAsync(rawA);
        Assert.Equal(RefreshTokenStatus.Valid, inspection.Status);
        bool marked;
        await using (var rotation = await harness.UnitOfWork.BeginTransactionAsync())
        {
            var successor = await sut.IssueRefreshTokenAsync(userId, "127.0.0.1", "google");
            marked = await sut.TryMarkRotatedAsync(inspection.Token!.Id, successor.Token.Id);
            if (marked) await rotation.CommitAsync(); // the controller commits only a won mark
        }

        Assert.False(marked); // OLD code: MarkRotated re-revoked the already-revoked token and reported nothing
        await using var read = Fixture.CreateContext();
        Assert.Equal(0, await read.RefreshTokens.CountAsync(t => t.UserId == userId && !t.IsRevoked));
        Assert.Equal(1, await read.RefreshTokens.CountAsync(t => t.UserId == userId)); // the successor never landed
    }

    [Fact]
    public async Task Rotation_RevokeAllArrivesBeforeTheCommit_WaitsForIt_AndRevokesTheSuccessorToo()
    {
        var userId = Guid.CreateVersion7();
        string rawA;
        await using (var seed = Fixture.CreateContext())
            rawA = (await new ServiceHarness(seed).RefreshTokenService().IssueRefreshTokenAsync(userId, "127.0.0.1", "google")).RawToken;

        // The revoke-all starts right before the rotation COMMITS — the successor is inserted (uncommitted), the
        // presented token is marked (its row locked) — and is left running: it must WAIT for the commit, then see
        // the successor. Without the chain lock its single UPDATE, whose snapshot predates the successor, queues
        // on the presented token's row instead, finds it already revoked when the commit releases it, and never
        // looks at the successor — which stays live after "sign out everywhere".
        Task? revokeAll = null;
        var faults = new DbFaultInjector().BeforeCommit(async () =>
        {
            revokeAll = Task.Run(async () =>
            {
                await using var other = Fixture.CreateContext();
                await new ServiceHarness(other).RefreshTokenService().RevokeAllUserTokensAsync(userId);
            });
            await Task.Delay(300); // long enough for it to be queued (on the chain lock, or on the row)
        });
        await using var db = Fixture.CreateContext(faults: faults);
        var harness = new ServiceHarness(db);
        var sut = harness.RefreshTokenService();

        var inspection = await sut.InspectRefreshTokenAsync(rawA);
        await using (var rotation = await harness.UnitOfWork.BeginTransactionAsync())
        {
            var successor = await sut.IssueRefreshTokenAsync(userId, "127.0.0.1", "google");
            Assert.True(await sut.TryMarkRotatedAsync(inspection.Token!.Id, successor.Token.Id));
            await rotation.CommitAsync();
        }
        await revokeAll!;

        await using var read = Fixture.CreateContext();
        Assert.Equal(2, await read.RefreshTokens.CountAsync(t => t.UserId == userId));
        Assert.Equal(0, await read.RefreshTokens.CountAsync(t => t.UserId == userId && !t.IsRevoked)); // OLD code: the successor is live
    }

    [Fact]
    public async Task TryConsumeGrace_WhenTheSuccessorWasRevokedMeanwhile_IsRefused()
    {
        // The grace path's claim: one-shot AND conditional on the successor still being live, so a revoke-all
        // between the inspection (which saw a live successor) and the claim is not forgiven.
        await using var db = Fixture.CreateContext();
        var clock = new FakeTimeProvider(GraceEpoch);
        var sut = new ServiceHarness(db, clock).RefreshTokenService(reuseGraceSeconds: 60);
        var (old, _) = await RotateAsync(sut, Guid.CreateVersion7());
        Assert.Equal(RefreshTokenStatus.RotatedWithinGrace, (await sut.InspectRefreshTokenAsync(old.RawToken)).Status);

        await sut.RevokeAllUserTokensAsync(old.Token.UserId); // lands between the inspection and the claim

        Assert.False(await sut.TryConsumeGraceAsync(old.Token.Id));
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

    // ── absolute session lifetime (v4 T36, decision #2: an optional knob, off by default) ──

    [Fact]
    public async Task AbsoluteLifetime_OffByDefault_TokensOutliveNothingButTheirOwnExpiry()
    {
        await using var db = Fixture.CreateContext();
        var clock = new FakeTimeProvider(GraceEpoch);
        var sut = new ServiceHarness(db, clock).RefreshTokenService(expiryDays: 30);

        var issued = await sut.IssueRefreshTokenAsync(Guid.CreateVersion7(), "127.0.0.1", "google");

        Assert.Null(issued.Token.SessionExpiresAt);
        Assert.Equal(GraceEpoch.AddDays(30), issued.Token.ExpiresAt);
    }

    [Fact]
    public async Task AbsoluteLifetime_WhenSet_CapsTheChain_AcrossRotations()
    {
        // With the keep-alive an open tab stays signed in indefinitely: every rotation minted another 30 days.
        // RefreshToken:AbsoluteLifetimeDays caps the whole chain from the first sign-in — each successor
        // inherits the session's end and never expires later than it.
        await using var db = Fixture.CreateContext();
        var clock = new FakeTimeProvider(GraceEpoch);
        var sut = new ServiceHarness(db, clock).RefreshTokenService(expiryDays: 30, absoluteLifetimeDays: 1);
        var userId = Guid.CreateVersion7();

        var first = await sut.IssueRefreshTokenAsync(userId, "127.0.0.1", "google");
        Assert.Equal(GraceEpoch.AddDays(1), first.Token.SessionExpiresAt);
        Assert.Equal(GraceEpoch.AddDays(1), first.Token.ExpiresAt); // the cap is nearer than 30 days

        clock.Advance(TimeSpan.FromHours(20));
        var rotated = await sut.IssueRefreshTokenAsync(userId, "127.0.0.1", "google", sessionExpiresAt: first.Token.SessionExpiresAt);

        Assert.Equal(GraceEpoch.AddDays(1), rotated.Token.SessionExpiresAt);
        Assert.Equal(GraceEpoch.AddDays(1), rotated.Token.ExpiresAt); // not now + 30 days, not even now + 1 day
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
    public async Task Inspect_RotatedTokenWithinGrace_ButSuccessorRowGone_IsReuse()
    {
        // v4 audit T25: the cleanup job deletes dead tokens. A rotated token whose successor row no longer
        // exists cannot prove a live session behind it — it is reuse, never a crash and never a renewal.
        await using var db = Fixture.CreateContext();
        var clock = new FakeTimeProvider(GraceEpoch);
        var sut = new ServiceHarness(db, clock).RefreshTokenService(reuseGraceSeconds: 60);

        var (old, successor) = await RotateAsync(sut, Guid.CreateVersion7());
        await using (var cleanup = Fixture.CreateContext())
            Assert.Equal(1, await cleanup.Set<Vuelto.Core.Entities.RefreshToken>().Where(t => t.Id == successor.Token.Id).ExecuteDeleteAsync());
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
    public void AbsoluteLifetimeDays_IsOffUnlessConfigured()
    {
        Assert.Null(Bind().AbsoluteLifetimeDays);
        Assert.Null(Bind(("RefreshToken:AbsoluteLifetimeDays", "")).AbsoluteLifetimeDays);
        Assert.Null(Bind(("RefreshToken:AbsoluteLifetimeDays", "0")).AbsoluteLifetimeDays); // 0 = off, not "expire now"
        Assert.Equal(90, Bind(("RefreshToken:AbsoluteLifetimeDays", "90")).AbsoluteLifetimeDays);
    }

    [Fact]
    public void ReuseGraceSeconds_IsReadFromConfiguration()
    {
        Assert.Equal(0, Bind(("RefreshToken:ReuseGraceSeconds", "0")).ReuseGraceSeconds);
        Assert.Equal(15, Bind(("RefreshToken:ReuseGraceSeconds", "15")).ReuseGraceSeconds);
    }
}
