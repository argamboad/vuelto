using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Vuelto.Api.Controllers;
using Vuelto.Api.Models;
using Vuelto.Api.Tests.Infrastructure;

namespace Vuelto.Api.Tests;

/// <summary>
/// The preference endpoints' storage rules (PREFS-1, ADR-022): "system" is a real, stored
/// preference — it must follow the user across devices exactly like "light"/"dark" — and
/// unsupported values are rejected before touching the store.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AccountControllerTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task SetTheme_System_IsStoredAsThePreference_NotNull()
    {
        await using var db = Fixture.CreateContext();
        var users = new ServiceHarness(db).UserService();
        var user = await users.GetOrCreateByEmailAsync("prefs-theme@example.com");

        var sut = ControllerFor(users, user.Id);
        var result = await sut.SetTheme(new ThemeRequest("system"), CancellationToken.None);

        Assert.IsType<OkResult>(result);
        await using var read = Fixture.CreateContext();
        Assert.Equal("system", (await read.Users.SingleAsync(u => u.Id == user.Id)).Theme);
    }

    [Fact]
    public async Task SetTheme_Unsupported_IsRejected()
    {
        await using var db = Fixture.CreateContext();
        var users = new ServiceHarness(db).UserService();
        var user = await users.GetOrCreateByEmailAsync("prefs-theme-bad@example.com");

        var sut = ControllerFor(users, user.Id);
        var result = await sut.SetTheme(new ThemeRequest("sepia"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        await using var read = Fixture.CreateContext();
        Assert.Null((await read.Users.SingleAsync(u => u.Id == user.Id)).Theme);
    }

    [Fact]
    public async Task SetLocale_Supported_IsStored()
    {
        await using var db = Fixture.CreateContext();
        var users = new ServiceHarness(db).UserService();
        var user = await users.GetOrCreateByEmailAsync("prefs-locale@example.com");

        var sut = ControllerFor(users, user.Id);
        var result = await sut.SetLocale(new LocaleRequest("es"), CancellationToken.None);

        Assert.IsType<OkResult>(result);
        await using var read = Fixture.CreateContext();
        Assert.Equal("es", (await read.Users.SingleAsync(u => u.Id == user.Id)).Locale);
    }

    // Only the preference endpoints are exercised, so the unrelated collaborators stay null.
    private static AccountController ControllerFor(Vuelto.Api.Services.IUserService users, Guid userId) => new(
        users, null!, null!, null!, null!, NullLogger<AccountController>.Instance)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")),
            },
        },
    };
}
