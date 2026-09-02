using System.Globalization;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Perezosoft.Shared.Ui.Layout;
using Perezosoft.Ui.Tests.Infrastructure;
using Xunit;

namespace Perezosoft.Ui.Tests;

/// <summary>
/// v3 audit UX-2 (T39): the locale-mismatch reload must not loop. When the culture store is readable but
/// NOT writable (quota/policy), PersistAsync swallows the failure, the reboot reads the OLD value, sees the
/// same mismatch, and reloads forever. The reload must be gated on the write actually sticking.
/// </summary>
public class LocaleReloadLoopGuardTests : ComponentTestBase
{
    [Fact]
    public async Task ReloadingBoot_DoesNotRenderTheBody_NoDoubledPageSideEffects()
    {
        // v3 T45c (found by the TB-UI-16 E2E journey): when the cold-start reconcile issues the
        // locale reload, THIS boot is doomed — rendering @Body anyway lets pages fire their boot
        // side effects (Join's auto-accept POST!) which then re-fire after the reload; the user
        // joined but was shown the error state. A reloading boot must keep the body unrendered.
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en");
        await SignInAsync(locale: "es"); // mismatch → the reconcile reloads

        var cut = Render<MainLayout>(ps => ps.Add(m => m.Body, b => b.AddMarkupContent(0, "<div id='page-body'>side effects live here</div>")));

        var nav = (Bunit.TestDoubles.BunitNavigationManager)Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        Assert.Contains(nav.History, h => h.Options.ForceLoad); // the reload WAS issued…
        Assert.Empty(cut.FindAll("#page-body"));                // …so the body must not have rendered
    }

    [Fact]
    public async Task NonReloadingBoot_RendersTheBody()
    {
        // The counterpart: an in-sync boot renders normally.
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en");
        await SignInAsync(locale: "en");

        var cut = Render<MainLayout>(ps => ps.Add(m => m.Body, b => b.AddMarkupContent(0, "<div id='page-body'>body</div>")));

        Assert.NotNull(cut.Find("#page-body"));
    }

    [Fact]
    public async Task WritableStore_ReloadsOnce()
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en");
        var nav = (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/household");

        await SignInAsync(locale: "es");
        Render<MainLayout>(ps => ps.Add(m => m.Body, b => b.AddMarkupContent(0, "<div>b</div>")));

        // The persist stuck, so the one reload is expected.
        Assert.Single(nav.History, h => h.Options.ForceLoad);
    }

    [Fact]
    public async Task WriteBlockedStore_DoesNotReload_NoLoop()
    {
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en");
        CultureStore.WritesBlocked = true; // readable, not writable

        var nav = (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/household");

        await SignInAsync(locale: "es");
        Render<MainLayout>(ps => ps.Add(m => m.Body, b => b.AddMarkupContent(0, "<div>b</div>")));

        // The write didn't stick — reloading would loop forever, so there must be NO forceLoad reload.
        // (The in-process culture switch still serves this session; only the satellite-assembly reload is skipped.)
        Assert.DoesNotContain(nav.History, h => h.Options.ForceLoad);
    }
}
