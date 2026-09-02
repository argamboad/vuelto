using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Tests;

/// <summary>
/// Security-critical account resolution: the unverified-email takeover guard and the
/// atomic user+tenant+owner-membership provisioning.
/// </summary>
[Collection(PostgresCollection.Name)]
public class UserServiceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task GetOrCreateUser_BrandNew_ProvisionsUserTenantAndOwnerMembership()
    {
        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db).UserService();

        var user = await sut.GetOrCreateUserAsync("New@Example.com", "g-1", "google", "New User", emailVerified: true);

        Assert.Equal("new@example.com", user.Email); // normalized to lower-case

        await using var read = Fixture.CreateContext();
        var membership = await read.TenantMemberships.SingleAsync(m => m.UserId == user.Id);
        Assert.Equal(TenantRoles.Owner, membership.Role);
        Assert.True(await read.Tenants.AnyAsync(t => t.Id == membership.TenantId));
        Assert.True(await read.UserLogins.AnyAsync(l => l.UserId == user.Id && l.Provider == "google"));
    }

    [Fact]
    public async Task GetOrCreateUser_UnverifiedEmailMatchingExisting_RefusesTakeover()
    {
        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db).UserService();

        // Existing account, established with a verified Google login.
        await sut.GetOrCreateUserAsync("victim@example.com", "g-1", "google", emailVerified: true);

        // A different provider presents the SAME email but UNVERIFIED — must be refused
        // (credential-attachment takeover vector).
        await Assert.ThrowsAsync<UnverifiedEmailConflictException>(() =>
            sut.GetOrCreateUserAsync("victim@example.com", "ms-2", "microsoft", emailVerified: false));

        await using var read = Fixture.CreateContext();
        Assert.False(await read.UserLogins.AnyAsync(l => l.Provider == "microsoft"));
    }

    [Fact]
    public async Task GetOrCreateUser_VerifiedEmailMatchingExisting_LinksNewProvider()
    {
        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db).UserService();

        var first = await sut.GetOrCreateUserAsync("user@example.com", "g-1", "google", emailVerified: true);
        var second = await sut.GetOrCreateUserAsync("user@example.com", "ms-2", "microsoft", emailVerified: true);

        Assert.Equal(first.Id, second.Id); // same account

        await using var read = Fixture.CreateContext();
        var logins = await read.UserLogins.Where(l => l.UserId == first.Id)
            .Select(l => l.Provider).ToListAsync();
        Assert.Contains("google", logins);
        Assert.Contains("microsoft", logins);
    }

    [Fact]
    public async Task GetOrCreateByEmail_New_MarksVerifiedAndProvisionsTenant()
    {
        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db).UserService();

        var user = await sut.GetOrCreateByEmailAsync("pwl@example.com");

        Assert.True(user.EmailVerified); // ownership proven by redeeming the link/code
        await using var read = Fixture.CreateContext();
        Assert.True(await read.TenantMemberships.AnyAsync(m => m.UserId == user.Id && m.Role == TenantRoles.Owner));
    }

    [Fact]
    public async Task GetOrCreateByEmail_Existing_ReturnsSameUserRegardlessOfCasing()
    {
        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db).UserService();

        var a = await sut.GetOrCreateByEmailAsync("dup@example.com");
        var b = await sut.GetOrCreateByEmailAsync("DUP@example.com");

        Assert.Equal(a.Id, b.Id);
    }

    [Fact]
    public async Task UpdateTheme_SetsOverwritesAndClearsThePreference()
    {
        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db).UserService();
        var user = await sut.GetOrCreateByEmailAsync("theme@example.com");

        await sut.UpdateThemeAsync(user.Id, "dark");
        await using (var read = Fixture.CreateContext())
            Assert.Equal("dark", (await read.Users.SingleAsync(u => u.Id == user.Id)).Theme);

        await sut.UpdateThemeAsync(user.Id, "light");
        await using (var read = Fixture.CreateContext())
            Assert.Equal("light", (await read.Users.SingleAsync(u => u.Id == user.Id)).Theme);

        // null = "never chose" (PREFS-1: "system" is stored verbatim by the controller).
        await sut.UpdateThemeAsync(user.Id, null);
        await using (var read = Fixture.CreateContext())
            Assert.Null((await read.Users.SingleAsync(u => u.Id == user.Id)).Theme);
    }

    [Fact]
    public async Task UpdateTheme_UnknownUser_IsANoOp()
    {
        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db).UserService();

        await sut.UpdateThemeAsync(Guid.CreateVersion7(), "dark"); // must not throw
    }
}
