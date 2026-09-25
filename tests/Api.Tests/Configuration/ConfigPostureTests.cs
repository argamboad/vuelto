using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Vuelto.Api.Configuration;
using Vuelto.Infrastructure.Billing;
using Vuelto.Infrastructure.Files;

namespace Vuelto.Api.Tests.Configuration;

/// <summary>
/// v3 audit S0-G3, made reflective by v4 T7 (S0-G8, AUTH-11, ADV-P4-15; R122 with the R53 exceptions): every
/// config-gated feature is CLOSED unless a deployment opts in (ADR-014/015/016/017/027). Two configurations are
/// checked: an empty one (the settings classes' own defaults) and the shipped <c>appsettings.json</c> (what a
/// deployment gets before it sets anything).
/// <para>
/// The hand list this replaced was complete only while someone remembered to extend it. Now every section-bound
/// <c>*Settings</c> class with a <c>bool Enabled</c> must have a <see cref="Switches"/> entry saying what it gates,
/// or <see cref="EveryEnabledSwitch_HasAPostureEntry"/> fails; the posture of each is then asserted reflectively.
/// Gates that a VALUE switches on (a bucket name, a key, an endpoint) are <see cref="PresenceGates"/>, and a source
/// scan finds new ones. The one gate whose empty state is OPEN on purpose is a named exception, below.
/// </para>
/// </summary>
public class ConfigPostureTests
{
    // Every feature switch, with what it gates. Add the line when you add a *Settings.Enabled.
    private static readonly Dictionary<Type, string> Switches = new()
    {
        [typeof(BillingSettings)] = "GATES-1/ADR-027: billing routes + webhook; off = every tenant is Free",
        [typeof(PublicApiSettings)] = "PUBAPI/ADR-015: public API + tenant API keys",
        [typeof(WebhooksSettings)] = "HOOKS/ADR-016: outbound webhooks",
    };

    // Named exceptions (R53): classes whose closed state is NOT "off/empty", with the reason.
    private static readonly Dictionary<Type, string> NamedExceptions = new()
    {
        [typeof(SignupSettings)] = "GATES-2/ADR-027: an empty green list means signup is OPEN — the right default for a template",
    };

    // Gates switched on by a non-empty value, as config paths. Settings-class properties and raw reads alike.
    private static readonly Dictionary<string, string> PresenceGates = new()
    {
        [$"{S3StorageSettings.SectionName}:Bucket"] = "S3 storage instead of local disk",
        [$"{StripeSettings.SectionName}:SecretKey"] = "real Stripe instead of the dev fake",
        [$"{PlatformAdminSettings.SectionName}:StaffEmails"] = "platform staff (ADR-014)",
        ["OpenTelemetry:Otlp:Endpoint"] = "telemetry leaves the host (DEPLOYMENT §11)",
        ["Authentication:Google:ClientId"] = "Google sign-in",
        ["Authentication:Microsoft:ClientId"] = "Microsoft sign-in",
        ["Proxy:Enabled"] = "trust forwarded headers from any peer (DEPLOY-1)",
    };

    public static TheoryData<string> Configs => ["empty", "shipped"];

    private static IConfiguration Config(string which) => which == "empty"
        ? new ConfigurationBuilder().Build()
        : new ConfigurationBuilder().AddJsonFile(Path.Combine(RepoRoot(), "src", "Api", "appsettings.json")).Build();

    private static object Bind(Type t, IConfiguration config)
    {
        var settings = Activator.CreateInstance(t)!;
        config.GetSection(SettingsCatalog.SectionNameOf(t)!).Bind(settings);
        return settings;
    }

    [Fact]
    public void EveryEnabledSwitch_HasAPostureEntry()
    {
        var unlisted = SettingsCatalog.SectionBound().Where(SettingsCatalog.HasEnabledSwitch)
            .Where(t => !Switches.ContainsKey(t) && !NamedExceptions.ContainsKey(t))
            .Select(t => t.Name)
            .ToList();
        Assert.True(unlisted.Count == 0,
            $"New feature switches need a line in ConfigPostureTests.Switches saying what they gate: {string.Join(", ", unlisted)}");
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void EveryEnabledSwitch_IsOff(string config)
    {
        var on = SettingsCatalog.SectionBound().Where(SettingsCatalog.HasEnabledSwitch)
            .Where(t => !NamedExceptions.ContainsKey(t))
            .Where(t => (bool)t.GetProperty("Enabled")!.GetValue(Bind(t, Config(config)))!)
            .Select(t => t.Name)
            .ToList();
        Assert.NotEmpty(Switches); // probe alive
        Assert.True(on.Count == 0, $"Features switched ON under the {config} configuration: {string.Join(", ", on)}");
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void EveryPresenceGate_IsEmpty(string config)
    {
        var configuration = Config(config);
        var set = PresenceGates.Keys
            .Where(k => configuration.GetSection(k) is var s && (!string.IsNullOrEmpty(s.Value) || s.GetChildren().Any(c => !string.IsNullOrEmpty(c.Value))))
            .ToList();
        Assert.True(set.Count == 0, $"Presence gates set under the {config} configuration: {string.Join(", ", set)}");

        // The settings classes' own defaults, for the gates that are properties (a non-empty initializer is a gate
        // switched on in code).
        foreach (var t in SettingsCatalog.SectionBound())
            foreach (var p in t.GetProperties().Where(p => PresenceGates.ContainsKey($"{SettingsCatalog.SectionNameOf(t)}:{p.Name}")))
                Assert.True(p.GetValue(Bind(t, configuration)) switch
                {
                    null => true,
                    string s => s.Length == 0,
                    System.Collections.IEnumerable e => !e.Cast<object>().Any(),
                    _ => false,
                }, $"{t.Name}.{p.Name} is set by default under the {config} configuration");
    }

    [Fact]
    public void EveryPresenceSwitch_InSource_IsListed()
    {
        // A value that switches something on reads like `IsNullOrEmpty(configuration["X"])` or binds a variable from
        // configuration["X"] that the next lines test for emptiness. Every such key is a presence gate and must be
        // listed above, so a new one can't ship switched on by a default nobody reviewed. The scan sees those two local
        // shapes only: a value tested inside a helper (the OTLP endpoint, in TelemetryExtensions.ApplyExporter) or
        // read as a bool (Proxy:Enabled) is listed by hand, and still asserted empty above.
        var direct = new Regex(@"IsNullOr(?:Empty|WhiteSpace)\(\s*(?:configuration|config|Configuration)\s*\[\s*""([^""]+)""\s*\]");
        var viaVariable = new Regex(@"var\s+(\w+)\s*=\s*(?:configuration|config|Configuration)\s*\[\s*""([^""]+)""\s*\]\s*;[\s\S]{0,400}?IsNullOr(?:Empty|WhiteSpace)\(\s*\1\s*\)");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(f);
            foreach (Match m in direct.Matches(text)) found.Add(m.Groups[1].Value);
            foreach (Match m in viaVariable.Matches(text)) found.Add(m.Groups[2].Value);
        }
        Assert.NotEmpty(found); // probe alive: the S3 bucket and OAuth client ids read this way today

        var unlisted = found.Where(k => !PresenceGates.ContainsKey(k)).OrderBy(k => k).ToList();
        Assert.True(unlisted.Count == 0, $"Presence switches missing from ConfigPostureTests.PresenceGates: {string.Join(", ", unlisted)}");
    }

    [Fact]
    public void SignupGreenList_IsEmpty_ByDefault()
    {
        // The named exception. An empty green list means anyone may sign up, which is the right default for a template
        // (a fresh app must not be born locked). What must never drift is a NON-EMPTY default, which would silently
        // restrict every downstream app that never configured it. So the posture pinned here is emptiness, and
        // `IsRestricted` is what code branches on.
        foreach (var config in new[] { "empty", "shipped" })
        {
            var settings = (SignupSettings)Bind(typeof(SignupSettings), Config(config));
            Assert.Empty(settings.AllowedEmails);
            Assert.Empty(settings.AllowedDomains);
            Assert.False(settings.IsRestricted);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Api", "Features")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root from the test assembly.");
    }
}
