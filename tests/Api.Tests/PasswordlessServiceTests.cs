using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Api.Tests;

/// <summary>
/// Passwordless credential lifecycle: magic-link single-use + expiry, and OTP success,
/// wrong-code rejection, attempt lockout, and expiry.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PasswordlessServiceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task MagicLink_IssueThenRedeem_SignsInAndIsSingleUse()
    {
        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db).PasswordlessService();

        var token = await sut.IssueMagicLinkTokenAsync("ml@example.com");

        var user = await sut.RedeemMagicLinkAsync("ml@example.com", token);
        Assert.NotNull(user);
        Assert.True(user!.EmailVerified);

        // Second use is rejected — the token was consumed.
        Assert.Null(await sut.RedeemMagicLinkAsync("ml@example.com", token));
    }

    [Fact]
    public async Task MagicLink_Expired_IsRejected()
    {
        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db).PasswordlessService();

        var token = await sut.IssueMagicLinkTokenAsync("exp@example.com");
        await ExpireTokensAsync(db, "exp@example.com");

        Assert.Null(await sut.RedeemMagicLinkAsync("exp@example.com", token));
    }

    [Fact]
    public async Task Otp_CorrectCode_Succeeds()
    {
        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db).PasswordlessService();

        var code = await sut.IssueOtpAsync("otp@example.com");
        var result = await sut.RedeemOtpAsync("otp@example.com", code);

        Assert.Equal(OtpStatus.Success, result.Status);
        Assert.NotNull(result.User);
    }

    [Fact]
    public async Task Otp_WrongCode_LocksOutAfterMaxAttempts()
    {
        await using var db = Fixture.CreateContext();
        var settings = new TestPasswordlessSettings { OtpMaxAttempts = 3 };
        var sut = new ServiceHarness(db).PasswordlessService(settings);

        var code = await sut.IssueOtpAsync("lock@example.com");
        var wrong = WrongVariant(code);

        Assert.Equal(OtpStatus.Invalid, (await sut.RedeemOtpAsync("lock@example.com", wrong)).Status);
        Assert.Equal(OtpStatus.Invalid, (await sut.RedeemOtpAsync("lock@example.com", wrong)).Status);
        Assert.Equal(OtpStatus.TooManyAttempts, (await sut.RedeemOtpAsync("lock@example.com", wrong)).Status);

        // Locked out for the window: even the correct code is refused (cumulative lockout, CONF-5).
        Assert.Equal(OtpStatus.TooManyAttempts, (await sut.RedeemOtpAsync("lock@example.com", code)).Status);
    }

    [Fact]
    public async Task Otp_Expired_ReturnsExpired()
    {
        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db).PasswordlessService();

        var code = await sut.IssueOtpAsync("otpexp@example.com");
        await ExpireTokensAsync(db, "otpexp@example.com");

        Assert.Equal(OtpStatus.Expired, (await sut.RedeemOtpAsync("otpexp@example.com", code)).Status);
    }

    [Fact]
    public async Task Otp_ResendAfterLockout_DoesNotResetTheAttemptBudget()
    {
        // The brute-force defense must be CUMULATIVE per email/window, not per code: requesting a
        // fresh code after exhausting the budget must NOT hand the attacker another N guesses (CONF-5).
        await using var db = Fixture.CreateContext();
        var settings = new TestPasswordlessSettings { OtpMaxAttempts = 5, OtpLockoutWindowMinutes = 15 };
        var sut = new ServiceHarness(db).PasswordlessService(settings);
        const string email = "brute@example.com";

        var code = await sut.IssueOtpAsync(email);
        var wrong = WrongVariant(code);

        // Burn the whole budget on the first code.
        for (var i = 0; i < 4; i++)
            Assert.Equal(OtpStatus.Invalid, (await sut.RedeemOtpAsync(email, wrong)).Status);
        Assert.Equal(OtpStatus.TooManyAttempts, (await sut.RedeemOtpAsync(email, wrong)).Status);

        // Resend mints a brand-new AttemptCount=0 code...
        var fresh = await sut.IssueOtpAsync(email);

        // ...but the email is still locked for the window — even the CORRECT new code is refused.
        Assert.Equal(OtpStatus.TooManyAttempts, (await sut.RedeemOtpAsync(email, fresh)).Status);
    }

    [Fact]
    public async Task Otp_LockoutClearsAfterWindowElapses()
    {
        await using var db = Fixture.CreateContext();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero));
        var settings = new TestPasswordlessSettings { OtpMaxAttempts = 5, OtpLockoutWindowMinutes = 15 };
        var sut = new ServiceHarness(db, clock).PasswordlessService(settings);
        const string email = "cooldown@example.com";

        var code = await sut.IssueOtpAsync(email);
        var wrong = WrongVariant(code);
        for (var i = 0; i < 5; i++) await sut.RedeemOtpAsync(email, wrong);
        Assert.Equal(OtpStatus.TooManyAttempts, (await sut.RedeemOtpAsync(email, wrong)).Status);

        // After the window passes, the old failures age out and a fresh code works again.
        clock.Advance(TimeSpan.FromMinutes(16));
        var fresh = await sut.IssueOtpAsync(email);
        Assert.Equal(OtpStatus.Success, (await sut.RedeemOtpAsync(email, fresh)).Status);
    }

    // Drive the stored token's expiry into the past — the "active" repo queries filter
    // on ExpiresAt in SQL, so the redeem then sees nothing active.
    private static async Task ExpireTokensAsync(AppDbContext db, string email)
    {
        var past = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.LoginTokens.Where(t => t.Email == email)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, past));
    }

    // A code guaranteed to differ from the issued one (flips the first digit).
    private static string WrongVariant(string code) =>
        (code[0] == '0' ? '1' : '0') + code[1..];
}
