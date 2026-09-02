using System.Globalization;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Perezosoft.Shared.Ui.Layout;
using Perezosoft.Ui.Tests.Infrastructure;
using Xunit;

namespace Perezosoft.Ui.Tests;

/// <summary>
/// v3 audit TB-UI-5 (T45c) — the reconcile state machine as a [Theory] matrix. MainLayout's
/// ReconcilePreferencesAsync is two-way per axis (theme, locale): a SET server value wins (apply +
/// cache on the device), a NEVER-SET server value adopts an explicit device choice (PUT), and
/// everything is compare-then-act so an in-sync state is a full no-op. The reload legs of the locale
/// axis are pinned separately (LocaleReloadDeepLinkTests / LocaleReloadLoopGuardTests), and the
/// impersonation guard in PreferenceScopingTests — this matrix pins the apply/adopt/no-op rows.
/// </summary>
public class ReconcileMatrixTests : ComponentTestBase
{
    [Theory]
    //          server   device(JS)  device(store)  expectApply  expectAdoptPut
    [InlineData("dark", "light", null, true, false)]  // server set, device differs → apply + cache
    [InlineData("dark", "dark", null, false, false)]  // in sync → no writes at all
    [InlineData(null, "light", "dark", false, true)]  // server never set → adopt the device choice
    [InlineData(null, "light", null, false, false)]   // nothing anywhere → full no-op
    public async Task ThemeAxis_AppliesAdoptsOrNoOps(
        string? serverTheme, string jsTheme, string? deviceTheme, bool expectApply, bool expectAdoptPut)
    {
        JSInterop.Setup<string>("appTheme.current").SetResult(jsTheme);
        if (deviceTheme is not null)
            await ThemeStore.PersistAsync(deviceTheme);
        Http.On(HttpMethod.Put, "/api/auth/theme");

        await SignInAsync(theme: serverTheme);
        Render<MainLayout>(ps => ps.Add(m => m.Body, b => b.AddMarkupContent(0, "<div>body</div>")));

        var applied = JSInterop.Invocations.Any(i => i.Identifier == "appTheme.set");
        Assert.Equal(expectApply, applied);
        if (expectApply)
            Assert.Equal(serverTheme, await ThemeStore.GetAsync()); // applied value cached on the device

        var adoptPuts = Http.Requests.Count(r =>
            r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath == "/api/auth/theme");
        Assert.Equal(expectAdoptPut ? 1 : 0, adoptPuts);
    }

    [Theory]
    //          server  device(store)  expectAdoptPut
    [InlineData("en", null, false)]  // server matches the boot culture → no reload, no PUT
    [InlineData(null, "es", true)]   // server never set → adopt the device choice
    [InlineData(null, null, false)]  // nothing anywhere → full no-op
    public async Task LocaleAxis_AdoptsOrNoOps_WithoutReloading(
        string? serverLocale, string? deviceLocale, bool expectAdoptPut)
    {
        // Chassis boots the test culture as "en", so serverLocale "en" is the in-sync row.
        if (deviceLocale is not null)
            await CultureStore.PersistAsync(deviceLocale);
        Http.On(HttpMethod.Put, "/api/auth/locale");

        await SignInAsync(locale: serverLocale);
        Render<MainLayout>(ps => ps.Add(m => m.Body, b => b.AddMarkupContent(0, "<div>body</div>")));

        var adoptPuts = Http.Requests.Count(r =>
            r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath == "/api/auth/locale");
        Assert.Equal(expectAdoptPut ? 1 : 0, adoptPuts);

        // None of these rows is a mismatch, so none may trigger the satellite-assembly reload.
        var nav = (Bunit.TestDoubles.BunitNavigationManager)
            Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        Assert.DoesNotContain(nav.History, h => h.Options.ForceLoad);
    }

    [Fact]
    public async Task UnknownServerLocale_IsSwallowed_LayoutStillRenders()
    {
        // A corrupt/unknown saved locale must not take the whole layout down — the
        // CultureNotFoundException is caught and the current culture stands.
        await SignInAsync(locale: "not a locale!");
        var cut = Render<MainLayout>(ps => ps.Add(m => m.Body, b => b.AddMarkupContent(0, "<div id='ok'>body</div>")));

        Assert.NotNull(cut.Find("#ok"));
        Assert.Equal("en", CultureInfo.CurrentUICulture.TwoLetterISOLanguageName); // unchanged
    }

    [Fact]
    public async Task SignedOut_ReconcileTouchesNothing()
    {
        // Not authenticated → the reconcile returns up front: no JS, no PUTs, no store writes.
        await ThemeStore.PersistAsync("dark"); // a device value that would otherwise be adopted
        Render<MainLayout>(ps => ps.Add(m => m.Body, b => b.AddMarkupContent(0, "<div>body</div>")));

        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "appTheme.set");
        Assert.DoesNotContain(Http.Requests, r => r.Method == HttpMethod.Put);
    }
}
