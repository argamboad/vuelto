using System.Globalization;
using System.Net;
using System.Net.Http;
using Bunit;
using Vuelto.Shared.Ui.Components;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// A preference saved to the account is what the session reports straight away — without asking for a
/// new token (2026-09-16).
/// <para>
/// MainLayout's reconcile trusts <see cref="Shared.Ui.Auth.AuthService.Theme"/> and
/// <see cref="Shared.Ui.Auth.AuthService.Locale"/>, which read the claims of the access token in memory.
/// The Android app keeps that token across a WebView reload, so a language change (which reloads) or any
/// reload after a theme change re-applied the OLD value. The first fix refreshed the session after each
/// save — and on the web that rotated the refresh cookie while the ThemeJourney reloaded, which came back
/// signed out (JiggerJot's develop run 35128350182, where the same change had shipped). So the service
/// remembers the saved value beside the token instead: no request, nothing to race.
/// </para>
/// </summary>
public class PreferenceSyncClaimTests : ComponentTestBase
{
    private int Refreshes => Http.Requests.Count(r =>
        r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/auth/refresh");

    [Fact]
    public async Task ASavedTheme_IsWhatTheSessionReports_WithoutARefresh()
    {
        await SignInAsync(theme: "dark");
        Http.On(HttpMethod.Put, "/api/auth/theme", status: HttpStatusCode.NoContent);
        var before = Refreshes;
        var switcher = Render<ThemeSwitcher>();

        switcher.Find("[data-testid='theme-switcher']").Change("light");

        switcher.WaitForAssertion(() => Assert.Equal("light", Auth.Theme));
        Assert.Equal(before, Refreshes);
    }

    [Fact]
    public async Task AThemeTheServerRefused_LeavesTheSessionAsItWas()
    {
        await SignInAsync(theme: "dark");
        Http.On(HttpMethod.Put, "/api/auth/theme", status: HttpStatusCode.InternalServerError);
        var switcher = Render<ThemeSwitcher>();

        switcher.Find("[data-testid='theme-switcher']").Change("light");

        switcher.WaitForAssertion(() => switcher.Find("[data-testid='theme-sync-failed']"));
        Assert.Equal("dark", Auth.Theme);
    }

    [Fact]
    public async Task ASavedLanguage_IsWhatTheSessionReports_BeforeTheReload_WithoutARefresh()
    {
        try
        {
            await SignInAsync(locale: "en");
            Http.On(HttpMethod.Put, "/api/auth/locale", status: HttpStatusCode.NoContent);
            var before = Refreshes;
            var switcher = Render<LanguageSwitcher>();

            switcher.Find("[data-testid='language-switcher']").Change("es");

            switcher.WaitForAssertion(() => Assert.Equal("es", Auth.Locale));
            Assert.Equal(before, Refreshes);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = null;
        }
    }

    [Fact]
    public async Task ARememberedChoice_SurvivesARenewal_AndGoesWithTheSession()
    {
        await SignInAsync(theme: "dark", locale: "en");
        Auth.RememberTheme("light");
        Auth.RememberLocale("es");
        Assert.Equal(("light", "es"), (Auth.Theme, Auth.Locale));

        // The same account's renewal keeps the choice (v4 UX-17): that token may have been minted before the
        // choice was saved and still carry the old claim — the memory is what bridges the gap.
        await Auth.TryRefreshAsync();
        Assert.Equal(("light", "es"), (Auth.Theme, Auth.Locale));

        // A DIFFERENT account's token does not inherit it: its own claims are the truth.
        Http.On(HttpMethod.Post, "/api/auth/refresh",
            $"{{\"access_token\":\"{TestJwt.Build(userId: "99999999-9999-9999-9999-999999999999", theme: "dark", locale: "en")}\"}}");
        await Auth.TryRefreshAsync();
        Assert.Equal(("dark", "en"), (Auth.Theme, Auth.Locale));

        Auth.RememberTheme("light");
        await Auth.LogoutAsync();
        Assert.Null(Auth.Theme);
    }
}
