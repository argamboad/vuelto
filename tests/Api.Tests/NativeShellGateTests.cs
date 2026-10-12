using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Vuelto.Api.Tests;

/// <summary>
/// v4 audit T51 (NAT-15, NAT-17 — R104, R105): the native shell's posture and its signing hygiene, held as
/// files rather than as a habit. The v3 tracker marked <c>allowBackup=false</c> done when it never landed; a
/// keystore generated in the repo root, as the first-release steps suggest, would have been committed without
/// a word; and the docs passed keystore passwords as <c>-p:</c> literals, which end up in shell history, the
/// process list and binlogs. None of these needs the MAUI workloads to check.
/// </summary>
public class NativeShellGateTests
{
    private static readonly XNamespace Android = "http://schemas.android.com/apk/res/android";

    [Fact]
    public void AndroidManifest_KeepsAppDataOnTheDevice_AndIsNeitherDebuggableNorCleartext()
    {
        var root = RepoRoot();
        var application = XDocument.Load(Path.Combine(root, "src", "Maui", "Platforms", "Android", "AndroidManifest.xml"))
            .Root!.Element("application")!;
        string? Attr(string name) => application.Attribute(Android + name)?.Value;

        // Backup would copy the OAuth resume marker, the culture preference and the WebView's localStorage to
        // another device. allowBackup covers Android ≤ 11; Android 12+ ignores it for device-to-device
        // transfer, which the data-extraction rules exclude.
        Assert.Equal("false", Attr("allowBackup"));
        Assert.Equal("@xml/data_extraction_rules", Attr("dataExtractionRules"));
        var rules = XDocument.Load(Path.Combine(root, "src", "Maui", "Platforms", "Android", "Resources", "xml", "data_extraction_rules.xml")).Root!;
        foreach (var section in new[] { "cloud-backup", "device-transfer" })
            Assert.Contains(rules.Element(section)?.Elements("exclude").Select(e => e.Attribute("domain")?.Value) ?? [], d => d == "root");

        Assert.Equal("@xml/network_security_config", Attr("networkSecurityConfig"));
        Assert.NotEqual("true", Attr("usesCleartextTraffic"));
        Assert.Null(Attr("debuggable")); // the build decides; a literal here ships a debuggable Release
    }

    [Fact]
    public void NetworkSecurityConfig_PermitsCleartextOnlyToTheDevLoopback()
    {
        // Cleartext is for the emulator reaching the dev API on the host, nothing else. A Release build cannot
        // use it for the API at all: ReleaseGuards.targets requires an https ApiBaseUrl.
        var config = XDocument.Load(Path.Combine(RepoRoot(), "src", "Maui", "Platforms", "Android", "Resources", "xml", "network_security_config.xml")).Root!;
        Assert.NotEqual("true", config.Element("base-config")?.Attribute("cleartextTrafficPermitted")?.Value);
        var cleartextHosts = config.Elements("domain-config")
            .Where(d => d.Attribute("cleartextTrafficPermitted")?.Value == "true")
            .SelectMany(d => d.Elements("domain").Select(e => e.Value.Trim())).Order().ToList();
        Assert.Equal(["10.0.2.2", "localhost"], cleartextHosts);
    }

    [Fact]
    public void Gitignore_CoversSigningMaterial()
    {
        // A keystore IS the app's identity; one created in the repo root must not be one `git add .` from public.
        var ignored = File.ReadAllLines(Path.Combine(RepoRoot(), ".gitignore")).Select(l => l.Trim()).ToHashSet();
        foreach (var pattern in new[] { "*.jks", "*.keystore", "*.p12", "*.pfx" })
            Assert.Contains(pattern, ignored);
    }

    [Fact]
    public void DocsScriptsAndWorkflows_NeverPassASigningPasswordAsALiteral()
    {
        // -p:AndroidSigningKeyPass=<the password> is in the shell history, the process list and any binlog.
        // The .NET Android SDK reads env:NAME (and file:path) itself, so the command line names a variable.
        var root = RepoRoot();
        var files = new[] { "docs", "tools", ".github" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*", SearchOption.AllDirectories))
            .Where(f => Path.GetExtension(f) is ".md" or ".ps1" or ".sh" or ".yml" or ".yaml")
            .Where(f => !f.Replace('\\', '/').Contains("/docs/audits/")) // the audit quotes what it found
            .Append(Path.Combine(root, "src", "Maui", "ReleaseGuards.targets"))
            .ToList();
        Assert.True(files.Count > 20, "probe: the scan found too few files");

        var literals = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                foreach (Match m in Regex.Matches(lines[i], @"-p:AndroidSigning(?:Key|Store)Pass=(?!env:|file:)"))
                    literals.Add($"{Path.GetRelativePath(root, file)}:{i + 1}");
        }
        Assert.True(literals.Count == 0, "a signing password is passed as a -p: literal (use env:NAME): " + string.Join(", ", literals));
    }

    [Fact]
    public void PublishScript_SignsWithAStoreKey_WhosePasswordsComeFromTheEnvironment()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "publish-native.ps1"));
        Assert.Matches(@"\[string\]\s*\$KeyStore", script);
        Assert.Matches(@"\[string\]\s*\$KeyAlias", script);
        Assert.Contains("-p:AndroidSigningStorePass=env:ANDROID_SIGNING_STORE_PASS", script);
        Assert.Contains("-p:AndroidSigningKeyPass=env:ANDROID_SIGNING_KEY_PASS", script);
        Assert.DoesNotMatch(@"\$(?:Store|Key)Pass(?:word)?\b", script); // no password parameter to type one into
    }

    [Fact]
    public void FirstReleaseDocs_VerifyWithApksigner_AndWarnAboutTheReverseUpgrade()
    {
        // jarsigner accepts a v1-only APK that Android 11+ refuses to install; apksigner is the check a phone
        // agrees with. And a store-signed build cannot install over a debug-signed sideload: say so once.
        var root = RepoRoot();
        var guide = File.ReadAllText(Path.Combine(root, "docs", "NEW_APP_GUIDE.md"));
        var deployment = File.ReadAllText(Path.Combine(root, "docs", "DEPLOYMENT.md"));
        Assert.DoesNotContain("jarsigner -verify", guide);
        Assert.Contains("apksigner verify", guide);
        Assert.Contains("uninstall", deployment[deployment.IndexOf("## 9", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void IconGround_IsOneColour_InTheCsprojTheIconAndTheAssetScript()
    {
        // The launcher icon's background is written in four places by hand. A rebrand that changes the token in
        // build_assets.py but not the csproj ships a launcher icon in the old colour.
        var root = RepoRoot();
        var csproj = File.ReadAllText(Directory.EnumerateFiles(Path.Combine(root, "src", "Maui"), "*.csproj").Single());
        var icon = Regex.Match(csproj, @"<MauiIcon [^>]*Color=""(#[0-9a-fA-F]{6})""").Groups[1].Value;
        var splash = Regex.Match(csproj, @"<MauiSplashScreen [^>]*Color=""(#[0-9a-fA-F]{6})""").Groups[1].Value;
        var svg = Regex.Match(File.ReadAllText(Path.Combine(root, "src", "Maui", "Resources", "AppIcon", "appicon.svg")), @"<rect [^>]*fill=""(#[0-9a-fA-F]{6})""").Groups[1].Value;

        // Different from the platform: this repo's brand rasters are not generated by docs/brand/build_assets.py,
        // so the script's token is not a fourth place; the three that exist are held to each other.
        Assert.NotEqual("", icon);
        Assert.True(new[] { splash, svg }.All(c => string.Equals(c, icon, StringComparison.OrdinalIgnoreCase)),
            $"the icon ground differs: MauiIcon {icon}, MauiSplashScreen {splash}, appicon.svg {svg}");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.EnumerateFiles("*.slnx").Any()) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
