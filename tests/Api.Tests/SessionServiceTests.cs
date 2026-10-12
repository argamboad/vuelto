using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;

namespace Vuelto.Api.Tests;

/// <summary>
/// The extracted session assembly: a JWT carrying the tenant claim + a refresh token
/// delivered by the right transport (body for native, cookie-bound for web).
/// </summary>
[Collection(PostgresCollection.Name)]
public class SessionServiceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task IssueAsync_Web_JwtHasTenantClaim_AndRefreshTokenIsNotOnBody()
    {
        await using var db = Fixture.CreateContext();
        var harness = new ServiceHarness(db);
        var user = await harness.UserService().GetOrCreateByEmailAsync("sess@example.com");
        var tenantId = (await db.TenantMemberships.SingleAsync(m => m.UserId == user.Id)).TenantId;

        var session = await harness.SessionService().IssueAsync(user, "google", "127.0.0.1", native: false);

        Assert.Null(session.Response.RefreshToken);                 // web: nothing on the body (cookie instead)
        Assert.False(string.IsNullOrEmpty(session.RefreshToken));   // raw token for the caller to set as a cookie
        Assert.Equal(user.Id, session.Response.UserId);

        var principal = harness.JwtTokenService().ValidateToken(session.Response.AccessToken);
        Assert.NotNull(principal);
        Assert.Equal(tenantId.ToString(), principal!.FindFirst(JwtTokenService.TenantIdClaim)?.Value);
    }

    [Fact]
    public async Task IssueAsync_Native_PutsRefreshTokenOnBody()
    {
        await using var db = Fixture.CreateContext();
        var harness = new ServiceHarness(db);
        var user = await harness.UserService().GetOrCreateByEmailAsync("native@example.com");

        var session = await harness.SessionService().IssueAsync(user, "google", "127.0.0.1", native: true);

        Assert.Equal(session.RefreshToken, session.Response.RefreshToken); // native carries it on the body
    }
}
