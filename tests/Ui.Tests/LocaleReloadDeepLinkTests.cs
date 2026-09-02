using System.Globalization;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Shared.Ui.Layout;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// v3 audit UX-1 (T39): when a signed-in user's saved locale differs from the boot culture, MainLayout does
/// one full reload to fetch the right satellite assemblies. The reload must PRESERVE the current path for
/// pages a signed-in user still needs to finish — <c>/join</c> (accept an invite) and <c>/auth-callback</c>
/// (OAuth handoff) — and only bounce the dead-ends (<c>/login</c>, <c>/auth-error</c>) to home. The bug sent
/// EVERY anonymous path to "/", destroying the invite-acceptance + OAuth-callback journeys.
/// </summary>
public class LocaleReloadDeepLinkTests : ComponentTestBase
{
    [Theory]
    [InlineData("/join?token=abc123", "/join?token=abc123")] // invite acceptance — preserved
    [InlineData("/auth-callback", "/auth-callback")]         // OAuth handoff — preserved
    [InlineData("/household", "/household")]                 // an authed deep link — preserved
    [InlineData("/login", "/")]                              // dead-end for a signed-in user — bounce home
    [InlineData("/auth-error", "/")]                         // dead-end — bounce home
    public async Task LocaleMismatchReload_PreservesDeepLinks_BouncesOnlyDeadEnds(string startPath, string expectedTarget)
    {
        // Deterministic culture: boot as English so a saved "es" locale is a guaranteed mismatch.
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo("en");

        var nav = (BunitNavigationManager)Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(startPath);

        await SignInAsync(locale: "es"); // saved locale differs from the en boot culture → triggers the reload

        Render<MainLayout>(ps => ps.Add(m => m.Body, b => b.AddMarkupContent(0, "<div>body</div>")));

        // The locale switch is the single forceLoad reload; assert where it landed. Normalize both to
        // absolute — MainLayout passes "/" (relative) for the bounce but Nav.Uri (absolute) for the preserve.
        var reload = nav.History.Last(h => h.Options.ForceLoad);
        Uri Abs(string u) => new(new Uri(nav.BaseUri), u);
        Assert.Equal(Abs(expectedTarget), Abs(reload.Uri));
    }
}
