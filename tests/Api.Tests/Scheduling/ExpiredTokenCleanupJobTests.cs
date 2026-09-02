using Microsoft.EntityFrameworkCore;
using Perezosoft.Api.Tests.Infrastructure;
using Perezosoft.Core.Entities;
using Perezosoft.Infrastructure.Scheduling;

namespace Perezosoft.Api.Tests.Scheduling;

/// <summary>
/// Drives the JOBS-3 reference job: it deletes expired passwordless and refresh tokens while leaving
/// active ones untouched. Real Postgres because it uses <c>ExecuteDeleteAsync</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ExpiredTokenCleanupJobTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task Run_DeletesExpiredTokens_KeepsActiveOnes()
    {
        var now = DateTimeOffset.UtcNow;

        await using (var seed = Fixture.CreateContext())
        {
            seed.LoginTokens.AddRange(
                NewLogin("expired@x.com", now.AddMinutes(-5)),
                NewLogin("active@x.com", now.AddMinutes(30)));
            seed.RefreshTokens.AddRange(
                NewRefresh(now.AddDays(-1)),
                NewRefresh(now.AddDays(7)));
            await seed.SaveChangesAsync();
        }

        await using (var db = Fixture.CreateContext())
            await new ExpiredTokenCleanupJob(db, TimeProvider.System).RunAsync();

        await using var read = Fixture.CreateContext();
        Assert.Equal("active@x.com", Assert.Single(await read.LoginTokens.Select(t => t.Email).ToListAsync()));
        Assert.True(await read.RefreshTokens.AllAsync(t => t.ExpiresAt > now));
        Assert.Equal(1, await read.RefreshTokens.CountAsync());
    }

    private static LoginToken NewLogin(string email, DateTimeOffset expiresAt) => new()
    {
        Email = email,
        CodeHash = "hash",
        Purpose = LoginTokenPurpose.Otp,
        ExpiresAt = expiresAt,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static RefreshToken NewRefresh(DateTimeOffset expiresAt) => new()
    {
        UserId = Guid.CreateVersion7(),
        TokenHash = Guid.NewGuid().ToString("N"),
        IssuedFromIp = "127.0.0.1",
        Provider = "google",
        IssuedAt = DateTimeOffset.UtcNow,
        ExpiresAt = expiresAt,
    };
}
