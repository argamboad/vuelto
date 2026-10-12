using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OtpNet;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Mfa;

/// <summary>
/// MFA-1 (ADR-012): TOTP enrollment/management. Enroll → confirm enables MFA + returns hashed
/// single-use recovery codes; the secret is stored encrypted; verify accepts a valid TOTP or a
/// recovery code (once); disable requires a valid code and wipes everything. Postgres-backed.
/// </summary>
[Collection(PostgresCollection.Name)]
public class MfaServiceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task Enroll_ThenConfirm_EnablesMfa_ReturnsHashedRecoveryCodes_SecretEncrypted()
    {
        await using var db = Fixture.CreateContext();
        var service = NewService(db);
        var userId = await SeedUserAsync(db);

        var enrollment = await service.BeginEnrollmentAsync(userId);
        Assert.NotNull(enrollment);
        Assert.StartsWith("otpauth://totp/", enrollment!.ProvisioningUri);
        Assert.False(await service.IsEnabledAsync(userId)); // not enabled until confirmed

        var (result, codes) = await service.ConfirmEnrollmentAsync(userId, CurrentCode(enrollment.Secret));

        Assert.Equal(MfaConfirmResult.Enabled, result);
        Assert.Equal(10, codes.Count);
        Assert.True(await service.IsEnabledAsync(userId));

        // Recovery codes stored only as hashes; secret stored encrypted (not the base32 plaintext).
        await using var read = Fixture.CreateContext();
        var storedCodes = await read.Set<MfaRecoveryCode>().Where(c => c.UserId == userId).ToListAsync();
        Assert.Equal(10, storedCodes.Count);
        Assert.DoesNotContain(storedCodes, c => codes.Contains(c.CodeHash));
        var stored = await read.Set<UserMfa>().SingleAsync(m => m.UserId == userId);
        Assert.NotEqual(enrollment.Secret, stored.EncryptedSecret);
    }

    [Fact]
    public async Task Confirm_InvalidCode_DoesNotEnable()
    {
        await using var db = Fixture.CreateContext();
        var service = NewService(db);
        var userId = await SeedUserAsync(db);
        await service.BeginEnrollmentAsync(userId);

        var (result, codes) = await service.ConfirmEnrollmentAsync(userId, "000000");

        Assert.Equal(MfaConfirmResult.InvalidCode, result);
        Assert.Empty(codes);
        Assert.False(await service.IsEnabledAsync(userId));
    }

    [Fact]
    public async Task Confirm_WithoutEnrollment_ReturnsNotEnrolled()
    {
        await using var db = Fixture.CreateContext();
        var service = NewService(db);
        var userId = await SeedUserAsync(db);

        var (result, _) = await service.ConfirmEnrollmentAsync(userId, "123456");
        Assert.Equal(MfaConfirmResult.NotEnrolled, result);
    }

    [Fact]
    public async Task Verify_ValidTotp_True_WrongCode_False()
    {
        await using var db = Fixture.CreateContext();
        var service = NewService(db);
        var userId = await SeedUserAsync(db);
        var secret = (await service.BeginEnrollmentAsync(userId))!.Secret;
        await service.ConfirmEnrollmentAsync(userId, CurrentCode(secret));

        Assert.True(await service.VerifyAsync(userId, CurrentCode(secret)));
        Assert.False(await service.VerifyAsync(userId, "000000"));
    }

    [Fact]
    public async Task Verify_SameTotpTwice_SecondRejected_AntiReplay()
    {
        // v2 audit LOGIC-S1: a TOTP is valid for a ~90s window, but a given code must verify only once.
        await using var db = Fixture.CreateContext();
        var service = NewService(db);
        var userId = await SeedUserAsync(db);
        var secret = (await service.BeginEnrollmentAsync(userId))!.Secret;
        await service.ConfirmEnrollmentAsync(userId, CurrentCode(secret));

        var code = CurrentCode(secret);
        Assert.True(await service.VerifyAsync(userId, code));   // first login accepts
        Assert.False(await service.VerifyAsync(userId, code));  // same code/timestep replayed → rejected
    }

    [Fact]
    public async Task RecoveryCode_IsSingleUse()
    {
        await using var db = Fixture.CreateContext();
        var service = NewService(db);
        var userId = await SeedUserAsync(db);
        var secret = (await service.BeginEnrollmentAsync(userId))!.Secret;
        var (_, codes) = await service.ConfirmEnrollmentAsync(userId, CurrentCode(secret));

        Assert.True(await service.VerifyAsync(userId, codes[0]));  // consumed
        Assert.False(await service.VerifyAsync(userId, codes[0])); // no second use
    }

    [Fact]
    public async Task RecoveryCodes_AreShort_AndVerifyCaseAndSeparatorInsensitively()
    {
        // Regression: recovery codes were the 88-char opaque token, which overflowed the maxlength-14
        // code inputs and could never be entered. They must be short, unambiguous, and forgiving of
        // how the user retypes them (hyphen dropped, different case).
        await using var db = Fixture.CreateContext();
        var service = NewService(db);
        var userId = await SeedUserAsync(db);
        var secret = (await service.BeginEnrollmentAsync(userId))!.Secret;
        var (_, codes) = await service.ConfirmEnrollmentAsync(userId, CurrentCode(secret));

        var code = codes[0];
        Assert.Equal(11, code.Length);                                       // "xxxxx-xxxxx" fits maxlength=14
        Assert.All(code.Replace("-", ""), c => Assert.True(char.IsLetterOrDigit(c)));
        Assert.DoesNotContain(code, c => "01oilOIL".Contains(char.ToLowerInvariant(c))); // no ambiguous glyphs

        // Typed without the hyphen and in a different case, it still verifies — once.
        Assert.True(await service.VerifyAsync(userId, code.Replace("-", "").ToUpperInvariant()));
        Assert.False(await service.VerifyAsync(userId, code));               // now consumed
    }

    [Fact]
    public async Task Disable_WithValidCode_WipesEverything()
    {
        await using var db = Fixture.CreateContext();
        var service = NewService(db);
        var userId = await SeedUserAsync(db);
        var secret = (await service.BeginEnrollmentAsync(userId))!.Secret;
        await service.ConfirmEnrollmentAsync(userId, CurrentCode(secret));

        Assert.Equal(MfaDisableResult.Disabled, await service.DisableAsync(userId, CurrentCode(secret)));
        Assert.False(await service.IsEnabledAsync(userId));

        await using var read = Fixture.CreateContext();
        Assert.False(await read.Set<UserMfa>().AnyAsync(m => m.UserId == userId));
        Assert.False(await read.Set<MfaRecoveryCode>().AnyAsync(c => c.UserId == userId));
    }

    [Fact]
    public async Task Disable_WithInvalidCode_Fails_StaysEnabled()
    {
        await using var db = Fixture.CreateContext();
        var service = NewService(db);
        var userId = await SeedUserAsync(db);
        var secret = (await service.BeginEnrollmentAsync(userId))!.Secret;
        await service.ConfirmEnrollmentAsync(userId, CurrentCode(secret));

        Assert.Equal(MfaDisableResult.InvalidCode, await service.DisableAsync(userId, "000000"));
        Assert.True(await service.IsEnabledAsync(userId));
    }

    [Fact]
    public async Task BeginEnrollment_UnknownUser_ReturnsNull()
    {
        await using var db = Fixture.CreateContext();
        Assert.Null(await NewService(db).BeginEnrollmentAsync(Guid.CreateVersion7()));
    }

    // --- staff reset (ADR-021 addendum): the recovery path when both factors are lost ---

    [Fact]
    public async Task Reset_WithoutAnyCode_WipesEverything_ReturnsTrue()
    {
        await using var db = Fixture.CreateContext();
        var service = NewService(db);
        var userId = await SeedUserAsync(db);
        var secret = (await service.BeginEnrollmentAsync(userId))!.Secret;
        await service.ConfirmEnrollmentAsync(userId, CurrentCode(secret));

        // No code required — that's the point: the user lost both the authenticator and the codes.
        Assert.True(await service.ResetAsync(userId));
        Assert.False(await service.IsEnabledAsync(userId));

        await using var read = Fixture.CreateContext();
        Assert.False(await read.Set<UserMfa>().AnyAsync(m => m.UserId == userId));
        Assert.False(await read.Set<MfaRecoveryCode>().AnyAsync(c => c.UserId == userId));
    }

    [Fact]
    public async Task Reset_WithNothingEnrolled_ReturnsFalse()
    {
        await using var db = Fixture.CreateContext();
        var service = NewService(db);
        var userId = await SeedUserAsync(db);

        Assert.False(await service.ResetAsync(userId)); // nothing to wipe — caller can no-op
    }

    // --- helpers ---

    private static MfaService NewService(AppDbContext db) =>
        new(new EfRepository<UserMfa>(db),
            new EfRepository<MfaRecoveryCode>(db),
            new UserRepository(db),
            new EphemeralDataProtectionProvider(),
            new RecoveryCodeHasher(new TestJwtSettings()),
            new TestMfaSettings(),
            TimeProvider.System);

    private static string CurrentCode(string base32Secret) =>
        new Totp(Base32Encoding.ToBytes(base32Secret)).ComputeTotp();

    private static async Task<Guid> SeedUserAsync(AppDbContext db)
    {
        var userId = Guid.CreateVersion7();
        db.Set<User>().Add(new User { Id = userId, Email = $"u-{userId:N}@x.com" });
        await db.SaveChangesAsync();
        return userId;
    }
}
