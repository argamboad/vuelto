using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Time.Testing;
using OtpNet;
using Perezosoft.Api.Configuration;
using Perezosoft.Api.Services;
using Perezosoft.Api.Tests.Infrastructure;
using Perezosoft.Core.Entities;
using Perezosoft.Infrastructure.Persistence;
using Perezosoft.Infrastructure.Repositories;

namespace Perezosoft.Api.Tests.Mfa;

/// <summary>
/// MFA-2 (ADR-012): the login step-up. When MFA is off, a session is issued directly; when on, primary
/// auth yields a challenge (no session) that is only redeemable with a valid TOTP/recovery code, and
/// the original native flag is preserved. Postgres-backed (real session issuance).
/// </summary>
[Collection(PostgresCollection.Name)]
public class MfaLoginServiceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task NoMfa_IssuesSessionDirectly()
    {
        await using var db = Fixture.CreateContext();
        var (login, _, user) = await BuildWithUserAsync(db);

        var (session, challenge) = await login.CompleteOrChallengeAsync(user, "otp", "127.0.0.1", native: false);

        Assert.Null(challenge);
        Assert.NotNull(session);
    }

    [Fact]
    public async Task MfaEnabled_ReturnsChallenge_NotSession()
    {
        await using var db = Fixture.CreateContext();
        var (login, mfa, user) = await BuildWithUserAsync(db);
        await EnableMfaAsync(mfa, user.Id);

        var (session, challenge) = await login.CompleteOrChallengeAsync(user, "otp", "127.0.0.1", native: false);

        Assert.NotNull(challenge);
        Assert.Null(session);
    }

    [Fact]
    public async Task VerifyChallenge_ValidCode_IssuesSession()
    {
        await using var db = Fixture.CreateContext();
        var (login, mfa, user) = await BuildWithUserAsync(db);
        var secret = await EnableMfaAsync(mfa, user.Id);
        var (_, challenge) = await login.CompleteOrChallengeAsync(user, "otp", "127.0.0.1", native: false);

        var outcome = await login.VerifyChallengeAsync(challenge!, CurrentCode(secret), "127.0.0.1");

        Assert.NotNull(outcome);
        Assert.False(outcome!.Native);
        Assert.NotNull(outcome.Session);
    }

    [Fact]
    public async Task VerifyChallenge_WrongCode_ReturnsNull()
    {
        await using var db = Fixture.CreateContext();
        var (login, mfa, user) = await BuildWithUserAsync(db);
        await EnableMfaAsync(mfa, user.Id);
        var (_, challenge) = await login.CompleteOrChallengeAsync(user, "otp", "127.0.0.1", native: false);

        Assert.Null(await login.VerifyChallengeAsync(challenge!, "000000", "127.0.0.1"));
    }

    [Fact]
    public async Task VerifyChallenge_TamperedChallenge_ReturnsNull()
    {
        await using var db = Fixture.CreateContext();
        var (login, mfa, user) = await BuildWithUserAsync(db);
        var secret = await EnableMfaAsync(mfa, user.Id);
        var (_, challenge) = await login.CompleteOrChallengeAsync(user, "otp", "127.0.0.1", native: false);

        Assert.Null(await login.VerifyChallengeAsync("AA" + challenge![2..], CurrentCode(secret), "127.0.0.1"));
    }

    [Fact]
    public async Task VerifyChallenge_ReplayedChallengeAndCode_IsRejectedOnSecondUse()
    {
        // v2 audit LOGIC-S1: one captured {challenge, code} must mint at most one session — the challenge
        // is single-use and the TOTP timestep is anti-replayed.
        await using var db = Fixture.CreateContext();
        var (login, mfa, user) = await BuildWithUserAsync(db);
        var secret = await EnableMfaAsync(mfa, user.Id);
        var (_, challenge) = await login.CompleteOrChallengeAsync(user, "otp", "127.0.0.1", native: false);
        var code = CurrentCode(secret);

        var first = await login.VerifyChallengeAsync(challenge!, code, "127.0.0.1");
        var replay = await login.VerifyChallengeAsync(challenge!, code, "127.0.0.1");

        Assert.NotNull(first);   // first redemption issues a session
        Assert.Null(replay);     // identical replay refused
    }

    [Fact]
    public async Task VerifyChallenge_PreservesNativeFlag()
    {
        await using var db = Fixture.CreateContext();
        var (login, mfa, user) = await BuildWithUserAsync(db);
        var secret = await EnableMfaAsync(mfa, user.Id);
        var (_, challenge) = await login.CompleteOrChallengeAsync(user, "google", "127.0.0.1", native: true);

        var outcome = await login.VerifyChallengeAsync(challenge!, CurrentCode(secret), "127.0.0.1");

        Assert.NotNull(outcome);
        Assert.True(outcome!.Native);
    }

    // --- LB-AUTH-1: the challenge is claimed BEFORE the factor is touched ---

    [Fact]
    public async Task VerifyChallenge_WrongCode_LeavesTheChallengeUsable_ForRetry()
    {
        // The claim is taken first (so a lost claim can't burn a factor), but a wrong code burns nothing —
        // so it must be handed back rather than forcing a fresh sign-in for a typo.
        await using var db = Fixture.CreateContext();
        var (login, mfa, user) = await BuildWithUserAsync(db);
        var secret = await EnableMfaAsync(mfa, user.Id);
        var (_, challenge) = await login.CompleteOrChallengeAsync(user, "otp", "127.0.0.1", native: false);

        Assert.Null(await login.VerifyChallengeAsync(challenge!, "000000", "127.0.0.1"));            // typo
        Assert.NotNull(await login.VerifyChallengeAsync(challenge!, CurrentCode(secret), "127.0.0.1")); // same challenge still works
    }

    [Fact]
    public async Task VerifyChallenge_AlreadyRedeemed_DoesNotBurnARecoveryCode()
    {
        // LB-AUTH-1: replaying a spent challenge with a DIFFERENT, still-valid recovery code used to burn
        // that code (VerifyAsync ran before Consume) and issue no session — the code was simply gone.
        await using var db = Fixture.CreateContext();
        var (login, mfa, user) = await BuildWithUserAsync(db);
        var secret = await EnableMfaAsync(mfa, user.Id);
        var codes = await RecoveryCodesAsync(mfa, user.Id, secret);

        var (_, challenge) = await login.CompleteOrChallengeAsync(user, "otp", "127.0.0.1", native: false);
        Assert.NotNull(await login.VerifyChallengeAsync(challenge!, CurrentCode(secret), "127.0.0.1")); // challenge spent

        Assert.Null(await login.VerifyChallengeAsync(challenge!, codes[0], "127.0.0.1")); // replay refused…

        await using var read = Fixture.CreateContext();
        var used = await read.Set<MfaRecoveryCode>().CountAsync(c => c.UserId == user.Id && c.UsedAt != null);
        Assert.Equal(0, used); // …and the recovery code was NOT spent
    }

    // --- ADM-3: per-user step-up brute-force cap ---
    // Each verify runs on a FRESH context (mirroring a per-request scope), sharing the DP keys (so the
    // enrolled secret + challenges stay decryptable) and the challenge cache — so the per-user lockout
    // state is read from the DB each time, exactly as in production.

    [Fact]
    public async Task Verify_CapWrongCodesAcrossFreshChallenges_LocksOut_EvenWithACorrectCode()
    {
        // TB-AUTH-3: the attacker mints a NEW challenge per guess and sprays across IPs, so the cap must be
        // per-user, not per-challenge/per-IP. After MaxAttempts wrong codes the user is locked — a correct
        // code is then rejected too.
        var ctx = new StepUpCtx(MaxAttempts: 3);
        var (user, secret) = await SeedEnabledUserAsync(ctx);

        for (var i = 0; i < ctx.Settings.MaxAttempts; i++)
            Assert.Null(await AttemptAsync(ctx, user, "000000", $"10.0.0.{i}")); // fresh challenge + context each time

        Assert.Null(await AttemptAsync(ctx, user, CurrentCode(secret), "10.0.0.99")); // locked despite a valid code
    }

    [Fact]
    public async Task Lockout_LiftsAfterTheWindow()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var ctx = new StepUpCtx(MaxAttempts: 3, LockoutWindowMinutes: 15, Clock: clock);
        var (user, secret) = await SeedEnabledUserAsync(ctx);

        for (var i = 0; i < ctx.Settings.MaxAttempts; i++)
            await AttemptAsync(ctx, user, "000000");
        Assert.Null(await AttemptAsync(ctx, user, CurrentCode(secret))); // still locked

        clock.Advance(TimeSpan.FromMinutes(16)); // window passes (TOTP itself runs on real time, unaffected)

        Assert.NotNull(await AttemptAsync(ctx, user, CurrentCode(secret))); // lock lifted
    }

    [Fact]
    public async Task Success_ResetsTheFailureCounter()
    {
        // A correct code mid-way clears the running count, so failures don't accumulate across separate
        // legitimate logins into a lockout.
        var ctx = new StepUpCtx(MaxAttempts: 3);
        var (user, secret) = await SeedEnabledUserAsync(ctx);

        await AttemptAsync(ctx, user, "000000");
        await AttemptAsync(ctx, user, "000000"); // count = 2 (cap 3)

        Assert.NotNull(await AttemptAsync(ctx, user, CurrentCode(secret))); // success → reset to 0

        await AttemptAsync(ctx, user, "000000");
        await AttemptAsync(ctx, user, "000000"); // count = 2 again, NOT 4 — so no lockout

        await using var read = Fixture.CreateContext();
        var state = await read.Set<UserMfa>().FirstAsync(m => m.UserId == user.Id);
        Assert.Equal(2, state.FailedAttemptCount);
        Assert.Null(state.LockedUntil);
    }

    // --- step-up harness (fresh context per attempt, shared DP + challenge cache) ---

    private sealed class StepUpCtx(int MaxAttempts, int LockoutWindowMinutes = 15, TimeProvider? Clock = null)
    {
        public IDataProtectionProvider Dp { get; } = new EphemeralDataProtectionProvider();
        public IMemoryCache Cache { get; } = new MemoryCache(new MemoryCacheOptions());
        public TimeProvider Clock { get; } = Clock ?? TimeProvider.System;
        public TestMfaSettings Settings { get; } = new() { MaxAttempts = MaxAttempts, LockoutWindowMinutes = LockoutWindowMinutes };
    }

    private (MfaLoginService login, MfaService mfa) NewStepUp(AppDbContext db, StepUpCtx ctx)
    {
        var mfa = new MfaService(
            new EfRepository<UserMfa>(db), new EfRepository<MfaRecoveryCode>(db), new UserRepository(db),
            ctx.Dp, new RecoveryCodeHasher(new TestJwtSettings()), ctx.Settings, ctx.Clock);
        var challenges = new MfaChallengeService(ctx.Dp, ctx.Cache);
        var harness = new ServiceHarness(db);
        return (new MfaLoginService(mfa, challenges, harness.SessionService(), harness.UserService()), mfa);
    }

    private async Task<(User user, string secret)> SeedEnabledUserAsync(StepUpCtx ctx)
    {
        await using var db = Fixture.CreateContext();
        var (_, mfa) = NewStepUp(db, ctx);
        var user = await SeedUserAsync(db);
        var secret = await EnableMfaAsync(mfa, user.Id);
        return (user, secret);
    }

    /// <summary>One full step-up attempt (mint a fresh challenge + verify) on a fresh context.</summary>
    private async Task<MfaVerifyOutcome?> AttemptAsync(StepUpCtx ctx, User user, string code, string ip = "10.0.0.1")
    {
        await using var db = Fixture.CreateContext();
        var (login, _) = NewStepUp(db, ctx);
        var (_, challenge) = await login.CompleteOrChallengeAsync(user, "otp", ip, native: false);
        return await login.VerifyChallengeAsync(challenge!, code, ip);
    }

    // --- helpers ---

    private async Task<(MfaLoginService login, MfaService mfa, User user)> BuildWithUserAsync(
        AppDbContext db, IMfaSettings? mfaSettings = null, TimeProvider? clock = null)
    {
        var mfa = new MfaService(
            new EfRepository<UserMfa>(db), new EfRepository<MfaRecoveryCode>(db), new UserRepository(db),
            new EphemeralDataProtectionProvider(), new RecoveryCodeHasher(new TestJwtSettings()),
            mfaSettings ?? new TestMfaSettings(), clock ?? TimeProvider.System);
        var challenges = new MfaChallengeService(new EphemeralDataProtectionProvider(), new MemoryCache(new MemoryCacheOptions()));
        var harness = new ServiceHarness(db);
        var login = new MfaLoginService(mfa, challenges, harness.SessionService(), harness.UserService());

        var user = await SeedUserAsync(db);
        return (login, mfa, user);
    }

    private static async Task<string> EnableMfaAsync(MfaService mfa, Guid userId)
    {
        var secret = (await mfa.BeginEnrollmentAsync(userId))!.Secret;
        await mfa.ConfirmEnrollmentAsync(userId, CurrentCode(secret));
        return secret;
    }

    /// <summary>Re-issues enrollment to capture the one-time recovery codes (shown only at confirm).</summary>
    private static async Task<IReadOnlyList<string>> RecoveryCodesAsync(MfaService mfa, Guid userId, string secret)
    {
        var (_, codes) = await mfa.ConfirmEnrollmentAsync(userId, CurrentCode(secret));
        return codes;
    }

    private static string CurrentCode(string base32Secret) =>
        new Totp(Base32Encoding.ToBytes(base32Secret)).ComputeTotp();

    private async Task<User> SeedUserAsync(AppDbContext db)
    {
        var tenantId = Guid.CreateVersion7();
        var user = new User { Id = Guid.CreateVersion7(), Email = $"u-{Guid.NewGuid():N}@x.com" };
        db.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = "T" });
        db.Set<User>().Add(user);
        db.Set<TenantMembership>().Add(new TenantMembership { TenantId = tenantId, UserId = user.Id, Role = TenantRoles.Owner });
        await db.SaveChangesAsync();
        return user;
    }
}
