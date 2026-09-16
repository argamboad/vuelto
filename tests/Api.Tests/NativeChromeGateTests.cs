using System.Text.RegularExpressions;

namespace Vuelto.Api.Tests;

/// <summary>
/// The app as it looks on a phone (2026-09-16), ported from the sibling app JiggerJot, where running the
/// real Android app on an emulator rather than a 390px browser window showed each defect held here. This
/// repo carried the identical platform-template shell code.
/// </summary>
public class NativeChromeGateTests
{
    [Fact]
    public void TheAndroidShell_CarriesNoTemplateColour()
    {
        // The platform template's sage (#6B8A72 / #465D4D) painted the status bar above every screen of an
        // indigo app. The bars now follow the app's own tokens in each theme.
        foreach (var file in new[]
                 {
                     Path.Combine("src", "Maui", "Platforms", "Android", "MainActivity.cs"),
                     Path.Combine("src", "Maui", "Platforms", "Android", "Resources", "values", "colors.xml"),
                 })
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), file));
            Assert.DoesNotContain("6B8A72", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("465D4D", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheSystemBarColours_AreTheAppsOwnTokens()
    {
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Shared.Ui", "wwwroot", "css", "app.css"));
        var bars = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Maui", "Platforms", "Android", "AndroidSystemBarTheme.cs"));

        var light = Block(css, @":root\s*\{");
        var dark = Block(css, @"\[data-bs-theme=""dark""\]\s*\{");

        // The header is --bs-primary in BOTH themes (AppHeader.razor.css; the dark block leaves the brand
        // indigo alone), so the bar above it is that indigo in both. Without a header — sign-in, the boot
        // state — it takes the page's ground, which does change with the theme.
        var primary = Token(light, "--bs-primary");
        Assert.DoesNotContain("--bs-primary:", dark);
        Assert.Equal(primary, Const(bars, "Light"));
        Assert.Equal(primary, Const(bars, "Dark"));
        Assert.Equal(Token(light, "--app-bg"), Const(bars, "LightGround"));
        Assert.Equal(Token(dark, "--app-bg"), Const(bars, "DarkGround"));
    }

    [Fact]
    public void TheAndroidShell_PadsForTheStatusBarOnce()
    {
        var activity = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Maui", "Platforms", "Android", "MainActivity.cs"));

        // The shell pads its content below the status bar. Current Android WebViews also report that inset
        // to CSS, so the header's env(safe-area-inset-top) added it a second time — a 55px empty band.
        // The shell hands the WebView insets with the top already spent.
        Assert.Contains("SetPadding(bars.Left, bars.Top, bars.Right, 0)", activity);
        Assert.Matches(new Regex(@"SetInsets\(\s*WindowInsetsCompat\.Type\.StatusBars\(\)"), activity);
    }

    [Fact]
    public void ThemeJs_TellsAWatcherEveryThemeItApplies()
    {
        var js = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Shared.Ui", "wwwroot", "js", "theme.js"));

        Assert.Contains("watch:", js);
        Assert.Contains("invokeMethodAsync('OnThemeApplied'", js);
    }

    [Fact]
    public void Rebranding_NamesTheAndroidSystemBars()
    {
        var rebranding = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "REBRANDING.md"));

        Assert.Contains("Platforms/Android/Resources/values/colors.xml", rebranding);
        Assert.Contains("SystemBarColors", rebranding);
    }

    private static string Block(string css, string opener)
    {
        var m = Regex.Match(css, opener + @"([^}]*)\}");
        Assert.True(m.Success, $"no block matching {opener} in app.css");
        return Regex.Replace(m.Groups[1].Value, @"/\*.*?\*/", "", RegexOptions.Singleline);
    }

    private static string Token(string block, string name)
    {
        var m = Regex.Match(block, Regex.Escape(name) + @":\s*(#[0-9A-Fa-f]{6})\s*;");
        Assert.True(m.Success, $"{name} is not a hex colour in its block");
        return m.Groups[1].Value.ToUpperInvariant();
    }

    private static string Const(string code, string name)
    {
        var m = Regex.Match(code, @"const string " + name + @"\s*=\s*""(#[0-9A-Fa-f]{6})""");
        Assert.True(m.Success, $"SystemBarColors.{name} not found");
        return m.Groups[1].Value.ToUpperInvariant();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root.");
    }
}
