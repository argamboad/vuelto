using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Shared.Ui;
using Vuelto.Shared.Ui.Components;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// The native system bars follow the app's theme (2026-09-16). On Android the status bar is drawn by the
/// OS, not the page, so it kept the platform template's sage green over a dark or light header alike.
/// <see cref="SystemBarThemeSync"/> tells a host that has system bars which theme theme.js just applied,
/// and whether the screen under the bar is the page's ground (no header — sign-in) or the header's
/// surface. A host without system bars — the web — registers nothing, and the component stays out of the way.
/// </summary>
public class SystemBarThemeSyncTests : ComponentTestBase
{
    private sealed class FakeSystemBars : ISystemBarTheme
    {
        public List<string> Applied { get; } = [];

        public Task ApplyAsync(string resolvedTheme, bool ground)
        {
            Applied.Add(ground ? $"{resolvedTheme}/ground" : resolvedTheme);
            return Task.CompletedTask;
        }
    }

    private int Watches => JSInterop.Invocations.Count(i => i.Identifier == "appTheme.watch");

    private FakeSystemBars RegisterBars()
    {
        var bars = new FakeSystemBars();
        Services.AddSingleton<ISystemBarTheme>(bars);
        return bars;
    }

    [Fact]
    public async Task WithNativeSystemBars_ItWatchesTheTheme_AndPassesEachOneOn()
    {
        var bars = RegisterBars();

        var sync = Render<SystemBarThemeSync>();

        Assert.Equal(1, Watches);
        await sync.InvokeAsync(() => sync.Instance.OnThemeApplied("dark"));
        await sync.InvokeAsync(() => sync.Instance.OnThemeApplied("light"));
        Assert.Equal(["dark", "light"], bars.Applied);
    }

    [Fact]
    public async Task AnythingButDark_ReadsAsLight()
    {
        var bars = RegisterBars();
        var sync = Render<SystemBarThemeSync>();

        // theme.js only ever resolves to light or dark, but a bar painted from a typo should still be a
        // real colour rather than whatever the host does with an unknown string.
        await sync.InvokeAsync(() => sync.Instance.OnThemeApplied("system"));

        Assert.Equal(["light"], bars.Applied);
    }

    [Fact]
    public async Task WithNoHeader_TheBarTakesThePageGround_AndFollowsWhenThatChanges()
    {
        var bars = RegisterBars();
        var sync = Render<SystemBarThemeSync>(ps => ps.Add(p => p.Ground, true));

        await sync.InvokeAsync(() => sync.Instance.OnThemeApplied("light"));
        // Signing in brings the header: the same theme is painted again, on the header's surface.
        sync.Render(ps => ps.Add(p => p.Ground, false));

        Assert.Equal(["light/ground", "light"], bars.Applied);
    }

    [Fact]
    public void OnTheWeb_ItStaysOutOfTheWay()
    {
        Render<SystemBarThemeSync>();

        Assert.Equal(0, Watches);
    }
}
