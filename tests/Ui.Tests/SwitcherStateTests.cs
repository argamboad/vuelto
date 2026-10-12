using System.Net;
using Bunit;
using Vuelto.Shared.Ui.Components;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// v3 audit UX-3 + UX-4 (T41), the theme/language switchers. UX-3: a pick whose best-effort PUT fails is
/// silently reverted by the next sign-in's server-wins reconcile — the switcher must SURFACE the failure so
/// the user knows it didn't sync. UX-4: ThemeSwitcher read the applied theme once at init, so a reconcile
/// that applied the server theme afterwards left the select showing a stale value all session — re-read it.
/// </summary>
public class SwitcherStateTests : ComponentTestBase
{
    [Fact]
    public async Task ThemeSwitcher_ServerSyncFails_ShowsMarker()
    {
        await SignInAsync(); // authenticated ⇒ the change PUTs server-side
        Http.On(HttpMethod.Put, "/api/auth/theme", "{}", HttpStatusCode.InternalServerError);
        JSInterop.Setup<string>("appTheme.current").SetResult("system");

        var cut = Render<ThemeSwitcher>();
        await cut.Find("select").ChangeAsync(new() { Value = "dark" });

        Assert.NotEmpty(cut.FindAll("[data-testid='theme-sync-failed']"));
    }

    [Fact]
    public async Task ThemeSwitcher_ServerSyncSucceeds_NoMarker()
    {
        await SignInAsync();
        Http.On(HttpMethod.Put, "/api/auth/theme"); // 200
        JSInterop.Setup<string>("appTheme.current").SetResult("system");

        var cut = Render<ThemeSwitcher>();
        await cut.Find("select").ChangeAsync(new() { Value = "dark" });

        Assert.Empty(cut.FindAll("[data-testid='theme-sync-failed']"));
    }

    [Fact]
    public async Task ThemeSwitcher_ReReadsServerTheme_OnSignIn_NoStaleValue()
    {
        // Rendered while anonymous showing the device value; sign-in then applies a different server theme.
        JSInterop.Setup<string>("appTheme.current").SetResult("light");
        var cut = Render<ThemeSwitcher>();
        Assert.Equal("light", cut.Find("select").GetAttribute("value"));

        await SignInAsync(theme: "dark"); // server theme differs — reconcile applied it; the select must follow

        Assert.Equal("dark", cut.Find("select").GetAttribute("value"));
    }

    [Fact]
    public async Task LanguageSwitcher_ServerSyncFails_ShowsMarker()
    {
        await SignInAsync();
        Http.On(HttpMethod.Put, "/api/auth/locale", "{}", HttpStatusCode.InternalServerError);

        var cut = Render<LanguageSwitcher>();
        await cut.Find("select").ChangeAsync(new() { Value = "es" });

        Assert.NotEmpty(cut.FindAll("[data-testid='locale-sync-failed']"));
    }
}
