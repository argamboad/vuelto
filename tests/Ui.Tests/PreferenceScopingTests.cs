using System.Globalization;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Perezosoft.Shared.Ui.Layout;
using Perezosoft.Ui.Tests.Infrastructure;
using Xunit;

namespace Perezosoft.Ui.Tests;

/// <summary>
/// v3 audit ADM-9 + the impersonation-hardening half of T40. ADM-9: the device pref store cached a
/// signed-in user's server pref and never cleared it, so the next user (never chose) on a shared device
/// inherited it — sign-out must wipe the device stores. Impersonation: reconcile must not touch the
/// device or the server while impersonating (defence-in-depth beyond the token happening to omit the claims).
/// </summary>
public class PreferenceScopingTests : ComponentTestBase
{
    private static void RenderLayout(BunitContext ctx) =>
        ctx.Render<MainLayout>(ps => ps.Add(m => m.Body, b => b.AddMarkupContent(0, "<div>body</div>")));

    [Fact]
    public async Task SignOut_ClearsDevicePrefStores_NoCrossUserPoison()
    {
        // A signed-in user whose reconcile wrote their theme/locale to the device store.
        await SignInAsync(theme: "dark", locale: "es");
        RenderLayout(this);
        Assert.NotNull(await ThemeStore.GetAsync()); // reconcile persisted the account theme to the device

        await Auth.LogoutAsync();

        // The device stores must be wiped so the NEXT user can't inherit this account's prefs (ADM-9).
        Assert.True(ThemeStore.Cleared);
        Assert.True(CultureStore.Cleared);
        Assert.Null(await ThemeStore.GetAsync());
        Assert.Null(await CultureStore.GetAsync());
    }

    [Fact]
    public async Task WhileImpersonating_ReconcileWritesNeitherDeviceNorServer()
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en");

        // An impersonation session that (defensively) DOES carry the target's prefs.
        await SignInAsync(theme: "dark", locale: "es", impersonatedBy: "00000000-0000-0000-0000-000000000009");
        Assert.True(Auth.IsImpersonating);

        RenderLayout(this);

        // No device write (the admin's device must not adopt the impersonated user's theme/locale) and no
        // forceLoad locale reload, and no server pref PUT.
        Assert.Null(await ThemeStore.GetAsync());
        Assert.Null(await CultureStore.GetAsync());
        var nav = Services.GetRequiredService<NavigationManager>();
        Assert.DoesNotContain(((Bunit.TestDoubles.BunitNavigationManager)nav).History, h => h.Options.ForceLoad);
        Assert.DoesNotContain(Http.Requests, r =>
            r.RequestUri!.AbsolutePath is "/api/auth/theme" or "/api/auth/locale");
    }
}
