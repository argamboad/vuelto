using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Tests;

/// <summary>
/// v3 audit LB-AUTH-3 (TB-AUTH-4) + LB-AUTH-2 (TB-AUTH-5): the passwordless credential paths under real
/// concurrency. Each racer gets its OWN DbContext — exactly like separate HTTP requests — because the bugs
/// are read-then-write races that a single shared context would hide.
/// <list type="bullet">
/// <item><b>LB-AUTH-3</b> — a single-use credential redeemed twice at once (email-client prefetch, a
/// double-click) must mint exactly ONE session, not two refresh tokens.</item>
/// <item><b>LB-AUTH-2</b> — concurrent wrong guesses must EACH be counted; a lost increment lets the
/// IP-independent brute-force cap be exceeded.</item>
/// </list>
/// </summary>
[Collection(PostgresCollection.Name)]
public class PasswordlessConcurrencyTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private const int Racers = 8;

    [Fact]
    public async Task MagicLink_ConcurrentRedemption_IssuesExactlyOneSession()
    {
        const string email = "ml-race@example.com";
        string token;
        await using (var db = Fixture.CreateContext())
            token = await new ServiceHarness(db).PasswordlessService().IssueMagicLinkTokenAsync(email);

        var results = await RaceAsync(sut => sut.RedeemMagicLinkAsync(email, token));

        Assert.Equal(1, results.Count(u => u is not null)); // exactly one redemption wins
    }

    [Fact]
    public async Task Otp_ConcurrentRedemption_SucceedsExactlyOnce()
    {
        const string email = "otp-race@example.com";
        string code;
        await using (var db = Fixture.CreateContext())
            code = await new ServiceHarness(db).PasswordlessService().IssueOtpAsync(email);

        var results = await RaceAsync(sut => sut.RedeemOtpAsync(email, code));

        Assert.Equal(1, results.Count(r => r.Status == OtpStatus.Success));
    }

    [Fact]
    public async Task Otp_ConcurrentWrongCodes_CountEveryAttempt()
    {
        // Stay under the cap so nothing consumes the code mid-race — this isolates the counter itself.
        const string email = "otp-count-race@example.com";
        var settings = new TestPasswordlessSettings { OtpMaxAttempts = 100 };
        await using (var db = Fixture.CreateContext())
            await new ServiceHarness(db).PasswordlessService(settings).IssueOtpAsync(email);

        await RaceAsync(sut => sut.RedeemOtpAsync(email, "000000"), settings);

        await using var read = Fixture.CreateContext();
        var counted = await read.LoginTokens
            .Where(t => t.Email == email && t.Purpose == LoginTokenPurpose.Otp)
            .SumAsync(t => t.AttemptCount);
        Assert.Equal(Racers, counted); // a lost read-modify-write would land well under this
    }

    /// <summary>Runs <paramref name="act"/> on <see cref="Racers"/> concurrent services, each on its own context.</summary>
    private async Task<IReadOnlyList<T>> RaceAsync<T>(
        Func<IPasswordlessService, Task<T>> act, TestPasswordlessSettings? settings = null)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var racers = Enumerable.Range(0, Racers).Select(async _ =>
        {
            await using var db = Fixture.CreateContext();
            var sut = new ServiceHarness(db).PasswordlessService(settings);
            await start.Task;                 // line everyone up so the writes genuinely overlap
            return await act(sut);
        }).ToArray();

        start.SetResult();
        return await Task.WhenAll(racers);
    }
}
