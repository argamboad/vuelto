using System.Globalization;
using System.Net;
using System.Net.Http;
using Bunit;
using Vuelto.Shared.Ui.Components;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// A preference saved to the account refreshes the session straight away (2026-09-16), so the claims the
/// app holds match what was just saved. MainLayout's reconcile trusts the theme and locale claims of the
/// access token it holds; on the Android app that token outlives a WebView reload, so a language change
/// (which reloads) or any reload after a theme change re-applied the OLD value from the stale claim —
/// found by running the real app on an emulator. The web got a fresh token on every reload and never saw it.
/// </summary>
public class PreferenceSyncRefreshTests : ComponentTestBase
{
    private int Refreshes => Http.Requests.Count(r =>
        r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/auth/refresh");

    [Fact]
    public async Task ASavedTheme_RefreshesTheSession()
    {
        await SignInAsync(theme: "dark");
        Http.On(HttpMethod.Put, "/api/auth/theme", status: HttpStatusCode.NoContent);
        var before = Refreshes;
        var switcher = Render<ThemeSwitcher>();

        switcher.Find("[data-testid='theme-switcher']").Change("light");

        switcher.WaitForAssertion(() => Assert.Equal(before + 1, Refreshes));
    }

    [Fact]
    public async Task AThemeTheServerRefused_DoesNotRefresh()
    {
        await SignInAsync(theme: "dark");
        Http.On(HttpMethod.Put, "/api/auth/theme", status: HttpStatusCode.InternalServerError);
        var before = Refreshes;
        var switcher = Render<ThemeSwitcher>();

        switcher.Find("[data-testid='theme-switcher']").Change("light");

        switcher.WaitForAssertion(() => switcher.Find("[data-testid='theme-sync-failed']"));
        Assert.Equal(before, Refreshes);
    }

    [Fact]
    public async Task ASavedLanguage_RefreshesTheSession_BeforeTheReload()
    {
        try
        {
            await SignInAsync(locale: "en");
            Http.On(HttpMethod.Put, "/api/auth/locale", status: HttpStatusCode.NoContent);
            var before = Refreshes;
            var switcher = Render<LanguageSwitcher>();

            switcher.Find("[data-testid='language-switcher']").Change("es");

            switcher.WaitForAssertion(() => Assert.Equal(before + 1, Refreshes));
            // The refresh comes after the save and before anything else leaves the page.
            var put = Http.Requests.FindLastIndex(r => r.RequestUri!.AbsolutePath == "/api/auth/locale");
            var refresh = Http.Requests.FindLastIndex(r => r.RequestUri!.AbsolutePath == "/api/auth/refresh");
            Assert.True(refresh > put);
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = null;
        }
    }
}
