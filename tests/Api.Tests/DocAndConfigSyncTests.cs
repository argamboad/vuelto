using System.Text.Json;
using System.Text.RegularExpressions;
using Vuelto.Api.Tests.Configuration;

namespace Vuelto.Api.Tests;

/// <summary>
/// v2 audit B11-5: doc/config drift gates (R20, R23) + the MailKit boundary (email hygiene). These are
/// grep/model-level source scans — the CI-enforceable half of "the auto-loaded manuals match the code",
/// so a doc or config can't silently rot away from what the code actually does.
/// </summary>
public class DocAndConfigSyncTests
{
    [Fact]
    public void MailKit_StaysBehindInfrastructureEmail()
    {
        // The IEmailSender abstraction is the only way to send email; the concrete MailKit/MimeKit
        // dependency must never leak outside src/Infrastructure/Email/ (CLAUDE.md hard rule).
        var emailDir = Path.Combine("src", "Infrastructure", "Email");
        var offenders = SourceFiles(Path.Combine(RepoRoot(), "src"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}{Path.Combine("Infrastructure", "Email")}{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f) is var t && (t.Contains("using MailKit") || t.Contains("using MimeKit")
                        || t.Contains("MailKit.") || t.Contains("MimeKit.")))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"MailKit/MimeKit must stay behind IEmailSender in {emailDir}: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void EveryEntity_IsDocumentedInDataModel()
    {
        // R23/DOC-10: every persisted entity type is named in DATA_MODEL.md, so the data-model doc can't
        // fall behind a new table. ITenantScoped is a marker interface, not an entity.
        var dataModel = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "DATA_MODEL.md"));
        var entities = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Core", "Entities"), "*.cs")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not "ITenantScoped" and not "Note") // ITenantScoped = marker; Note = DELETE-ME sample
            .ToList();

        var missing = entities.Where(e => !Regex.IsMatch(dataModel, $@"\b{Regex.Escape(e!)}\b")).ToList();
        Assert.True(missing.Count == 0, $"Entities missing from DATA_MODEL.md: {string.Join(", ", missing)}");
    }

    [Fact]
    public void ConfigKeys_ReadInCode_AreDocumented()
    {
        // R20/CON-2: every Section:Key literal read via IConfiguration is documented — either in an
        // appsettings*.json (as a nested path) or in .env.example (as Section__Key). Catches config drift
        // where code reads a key nobody declared. Scoped to config-access sites (ReadKeysIn).
        var documented = DocumentedConfigPaths();

        var readKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in SourceFiles(Path.Combine(RepoRoot(), "src")))
            readKeys.UnionWith(ReadKeysIn(File.ReadAllText(f)));

        var undocumented = readKeys.Where(k => !IsDocumented(k, documented)).OrderBy(k => k).ToList();

        Assert.True(undocumented.Count == 0,
            $"Config keys read in code but not in appsettings*.json or .env.example: {string.Join(", ", undocumented)}");
    }

    [Fact]
    public void ReadKeysIn_SeesEveryReadShape()
    {
        // Self-test of the extractor: each shape once blinded the gate (v3 TR-9; v4 OBS-3 for the flat read).
        Assert.Contains("Reports:Limit", ReadKeysIn("""var x = configuration.GetValue<int>("Reports:Limit");"""));
        Assert.Contains("Reports:Limit", ReadKeysIn("""private const string LimitKey = "Reports:Limit";"""));
        Assert.Contains("Reports", ReadKeysIn("""services.Configure<X>(config.GetSection("Reports"));"""));
        Assert.Contains("APP_BUILD_COMMIT", ReadKeysIn("""Environment.GetEnvironmentVariable("APP_BUILD_COMMIT")"""));
        Assert.Contains("OTEL_EXPORTER_OTLP_PROTOCOL", ReadKeysIn("""var p = configuration["OTEL_EXPORTER_OTLP_PROTOCOL"];"""));
    }

    // v4 T6 (ADV-P4-10, R153): a settings class bound through its SectionName constant never shows a literal at
    // the call site, so the literal scan above can't see its section or its keys. Reflect over the classes
    // instead. Classes that implement a Core I*Settings interface are built key by key in SettingsProvider from
    // literals the scan above already reads; every other *Settings class is bound from a section and must name it.

    [Fact]
    public void EverySectionBoundSettingsClass_DeclaresItsSectionName()
    {
        var unnamed = SettingsCatalog.ConfigBound().Where(t => !SettingsCatalog.IsBuiltKeyByKey(t) && SettingsCatalog.SectionNameOf(t) is null).Select(t => t.FullName).ToList();
        Assert.True(unnamed.Count == 0,
            $"Settings classes bound from config must declare `public const string SectionName`: {string.Join(", ", unnamed)}");
    }

    [Fact]
    public void EverySettingsSection_AndItsProperties_AreDocumented()
    {
        var missing = UndocumentedSettings(SettingsCatalog.All(), DocumentedConfigPaths());
        Assert.True(missing.Count == 0,
            $"Settings bound from config but not in appsettings*.json or .env.example: {string.Join(", ", missing)}");
    }

    [Fact]
    public void UndocumentedSettings_CatchesANewSlicesSection()
    {
        // The audit's case: a new slice ships ReportsSettings with a one-word section nobody documented.
        var documented = new HashSet<string>(StringComparer.Ordinal) { "Billing:Enabled" };
        Assert.Equal(["Reports", "Reports:Enabled", "Reports:MaxRows"], UndocumentedSettings([typeof(ReportsSettings)], documented));

        documented.UnionWith(["Reports:Enabled", "Reports:MaxRows"]);
        Assert.Empty(UndocumentedSettings([typeof(ReportsSettings)], documented));
    }

    [Fact]
    public void SettingsClasses_AreBoundThroughTheirSectionName()
    {
        // One binding mechanism: GetSection(XxxSettings.SectionName), never the section repeated as a literal that a
        // rename would miss.
        var sections = SettingsCatalog.All().Select(SettingsCatalog.SectionNameOf).OfType<string>().ToList();
        var offenders = SourceFiles(Path.Combine(RepoRoot(), "src"))
            .SelectMany(f => sections
                .Where(s => Regex.IsMatch(File.ReadAllText(f), $@"GetSection\s*\(\s*""{Regex.Escape(s)}""\s*\)"))
                .Select(s => $"{Path.GetFileName(f)}: GetSection(\"{s}\")"))
            .ToList();
        Assert.True(offenders.Count == 0, $"Bind settings through their SectionName constant: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void AGatedSwitch_IsReadOnlyThroughItsSettingsClass()
    {
        // BILL-4: Program bound BillingSettings while Infrastructure re-read "Billing:Enabled" raw. The two reads must
        // agree (one removes the billing routes, the other relaxes the Stripe startup check), and a section rename
        // would change only one. A *Settings.Enabled switch is read through the class, never as a literal.
        var gated = SettingsCatalog.All()
            .Where(SettingsCatalog.HasEnabledSwitch)
            .Select(t => SettingsCatalog.SectionNameOf(t)!)
            .OfType<string>()
            .ToList();
        Assert.NotEmpty(gated); // probe alive: Billing, PublicApi, Webhooks

        var offenders = SourceFiles(Path.Combine(RepoRoot(), "src"))
            .SelectMany(f => gated
                .Where(s => File.ReadAllText(f) is var text
                            && (text.Contains($"\"{s}:Enabled\"", StringComparison.Ordinal)
                                || text.Contains($"\"{s}__Enabled\"", StringComparison.Ordinal)))
                .Select(s => $"{Path.GetFileName(f)} reads {s}:Enabled raw"))
            .ToList();
        Assert.True(offenders.Count == 0, $"Read gated switches through their settings class: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void CompiledInLimits_AreListedInTheEnvExample() // R120 (v4 TR-25)
    {
        // .env.example documents every key an operator can set, and ends with the limits they cannot: a number
        // compiled into the code is invisible to whoever sizes a deployment unless it is written down there. The
        // 7 MiB attachment cap and the client's keep-alive timings shipped unlisted. A limit is a numeric or
        // TimeSpan constant in Core or the client's auth code whose name says it bounds something.
        var root = RepoRoot();
        var envExample = File.ReadAllText(Path.Combine(root, ".env.example")).ReplaceLineEndings("\n");
        // The platform's block sits in the shared skeleton; an app lists its own limits in a second block below it.
        var blocks = Regex.Matches(envExample, @"# ── Not configurable \(.*?(?=\n\n|\z)", RegexOptions.Singleline);
        Assert.True(blocks.Count >= 1, ".env.example no longer has a 'Not configurable (…)' block");
        var block = string.Join("\n", blocks.Select(m => m.Value));

        var limits = new[] { Path.Combine(root, "src", "Core"), Path.Combine(root, "src", "Shared.Ui", "Auth") }
            .SelectMany(SourceFiles)
            .SelectMany(f => CompiledInLimit.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();
        Assert.True(limits.Count >= 8, $"probe: only {limits.Count} compiled-in limits found ({string.Join(", ", limits)})");

        var unlisted = limits.Where(name => !Regex.IsMatch(block, $@"\b{name}\b")).ToList();
        Assert.True(unlisted.Count == 0,
            "Compiled-in limits missing from .env.example's 'Not configurable' block — name each one there with its "
            + $"value, so the limit is visible to whoever deploys: {string.Join(", ", unlisted)}");
    }

    private static readonly Regex CompiledInLimit = new(
        @"(?:const|static\s+readonly)\s+(?:int|long|double|TimeSpan|IReadOnlyList<TimeSpan>)\s+"
        + @"(Max[A-Za-z0-9]*|[A-Za-z0-9]*(?:Bytes|Lead|Delay|Delays|Wait|Limit|Cap|Timeout|Count|Length))\b");

    [Fact]
    public void ArchitectureAndFlows_NameTheClassesAndTheAuthErrorCodes() // R121 (v4 TR-20)
    {
        // The diagram layer drifted in the PRs that added the features: SignupGate and the whole Observability
        // folder were in no diagram, and FLOWS showed no sign-in path that could answer signup_not_allowed.
        // Floor: ARCHITECTURE.md names every public class in the three folders that hold the API's own logic
        // (a diagram, a sentence or the class index, §12), and FLOWS.md names every error code AuthController returns.
        var root = RepoRoot();
        var architecture = File.ReadAllText(Path.Combine(root, "docs", "ARCHITECTURE.md"));
        var classes = new[] { "Services", "Configuration", "Observability" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, "src", "Api", d), "*.cs"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"public\s+(?:(?:sealed|static|abstract|partial)\s+)*class\s+(\w+)")
                .Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();
        Assert.True(classes.Count >= 60, $"probe: only {classes.Count} public classes found under src/Api");

        var unnamed = classes.Where(c => !Regex.IsMatch(architecture, $@"\b{c}\b")).ToList();
        Assert.True(unnamed.Count == 0,
            "Public classes in src/Api/Services, Configuration or Observability that docs/ARCHITECTURE.md never names — "
            + $"draw the class where it belongs, or add it to the class index (§12): {string.Join(", ", unnamed)}");

        var flows = File.ReadAllText(Path.Combine(root, "docs", "FLOWS.md"));
        var codes = Regex.Matches(File.ReadAllText(Path.Combine(root, "src", "Api", "Controllers", "AuthController.cs")),
                @"ErrorResponse\(\s*""([a-z_]+)""")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.True(codes.Count >= 5, $"probe: only {codes.Count} error codes found in AuthController");

        var undrawn = codes.Where(c => !flows.Contains(c, StringComparison.Ordinal)).ToList();
        Assert.True(undrawn.Count == 0,
            $"Error codes AuthController returns that docs/FLOWS.md never names — add the branch to its flow: {string.Join(", ", undrawn)}");
    }

    /// <summary>Every config key a source text reads, in each read shape the gate knows.</summary>
    internal static HashSet<string> ReadKeysIn(string text)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in DottedAccess.Matches(text)) keys.Add(m.Groups[1].Value);
        foreach (Match m in ConstKey.Matches(text)) keys.Add(m.Groups[1].Value);
        foreach (Match m in SectionOnly.Matches(text)) keys.Add(m.Groups[1].Value);
        foreach (Match m in FlatIndexer.Matches(text)) keys.Add(m.Groups[1].Value);
        foreach (Match m in EnvRead.Matches(text))
            keys.Add(m.Groups[1].Value.Contains("__") ? m.Groups[1].Value.Replace("__", ":") : m.Groups[1].Value);
        return keys;
    }

    private static readonly Regex DottedAccess = new(@"(?:GetSection|GetValue<[^>]*>|GetValue|configuration|config|Configuration)\s*[\(\[]\s*""([A-Za-z][A-Za-z0-9]*(?::[A-Za-z0-9]+)+)""");
    // Keys hoisted into a const are read via the identifier, so the call-site regex never sees the literal — a blind
    // spot that silently exempted whole files (Proxy:*, Rls:*). Capture the declarations too.
    private static readonly Regex ConstKey = new(@"const\s+string\s+\w+\s*=\s*""([A-Za-z][A-Za-z0-9]*(?::[A-Za-z0-9]+)+)""");
    // v3 TR-9 (T47): a SINGLE-segment GetSection("Admin") binds a whole options class without a dotted literal.
    private static readonly Regex SectionOnly = new(@"GetSection\s*\(\s*""([A-Za-z][A-Za-z0-9]*)""\s*\)");
    // v4 T6 (OBS-3): a flat indexer read, e.g. configuration["OTEL_EXPORTER_OTLP_PROTOCOL"] (an SDK variable read
    // through IConfiguration); like a raw env read, it must be documented verbatim.
    private static readonly Regex FlatIndexer = new(@"(?:configuration|config|Configuration)\s*\[\s*""([A-Za-z_][A-Za-z0-9_]*)""\s*\]");
    // v3 TR-9 (T47): raw Environment.GetEnvironmentVariable reads bypass IConfiguration entirely (APP_BUILD_COMMIT).
    private static readonly Regex EnvRead = new(@"Environment\.GetEnvironmentVariable\s*\(\s*""([A-Za-z_][A-Za-z0-9_]*)""\s*\)");

    // A read key is OK if it equals a documented path, is a prefix of one (a section), or a documented path is a prefix
    // of it (a leaf under a documented section).
    private static bool IsDocumented(string key, ISet<string> documented) =>
        documented.Any(d => d.Equals(key, StringComparison.Ordinal)
                            || d.StartsWith(key + ":", StringComparison.Ordinal)
                            || key.StartsWith(d + ":", StringComparison.Ordinal));

    /// <summary>The section and each bindable property of every section-bound settings type, as undocumented paths.</summary>
    internal static List<string> UndocumentedSettings(IEnumerable<Type> settingsTypes, ISet<string> documented)
    {
        var missing = new List<string>();
        foreach (var t in settingsTypes)
        {
            if (SettingsCatalog.SectionNameOf(t) is not { } section) continue;
            // The section must appear; each property must appear as itself or with children (arrays, maps).
            if (!documented.Any(d => d == section || d.StartsWith(section + ":", StringComparison.Ordinal)))
                missing.Add(section);
            foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                         .Where(p => p.SetMethod?.IsPublic == true))
            {
                var path = $"{section}:{p.Name}";
                if (!documented.Any(d => d == path || d.StartsWith(path + ":", StringComparison.Ordinal)))
                    missing.Add(path);
            }
        }
        return missing.OrderBy(m => m, StringComparer.Ordinal).ToList();
    }

    // A stand-in for a downstream slice's settings class, for the self-test above.
    private sealed class ReportsSettings
    {
        public const string SectionName = "Reports";
        public bool Enabled { get; set; }
        public int MaxRows { get; init; }
    }

    /// <summary>Flattened config paths (Section:Key) declared in appsettings*.json + .env.example.</summary>
    private static HashSet<string> DocumentedConfigPaths()
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);

        // The API's appsettings, plus the Web client's (wwwroot) for the client's own reads (ApiBaseUrl).
        foreach (var json in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Api"), "appsettings*.json")
                     .Concat(Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Web", "wwwroot"), "appsettings*.json")))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            Flatten(doc.RootElement, "", paths);
        }

        var envExample = Path.Combine(RepoRoot(), ".env.example");
        if (File.Exists(envExample))
            foreach (var raw in File.ReadAllLines(envExample))
            {
                // Commented keys (e.g. `#Storage__S3__Bucket=`) still DOCUMENT the config surface — strip a
                // leading '#' before parsing so they count. Only KEY=… lines qualify (skip prose comments).
                var line = raw.TrimStart('#', ' ');
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                if (Regex.IsMatch(key, @"^[A-Za-z][A-Za-z0-9]*(?:__[A-Za-z0-9]+)+$"))
                    paths.Add(key.Replace("__", ":"));
                // FLAT env-var names (APP_BUILD_COMMIT, deploy-injected) document raw
                // Environment.GetEnvironmentVariable reads (v3 TR-9, T47).
                else if (Regex.IsMatch(key, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                    paths.Add(key);
            }

        return paths;
    }

    private static void Flatten(JsonElement el, string prefix, HashSet<string> into)
    {
        if (el.ValueKind != JsonValueKind.Object) { if (prefix.Length > 0) into.Add(prefix); return; }
        foreach (var p in el.EnumerateObject())
        {
            if (p.Name.StartsWith("//", StringComparison.Ordinal)) continue; // json comment-keys
            Flatten(p.Value, prefix.Length == 0 ? p.Name : $"{prefix}:{p.Name}", into);
        }
    }

    private static IEnumerable<string> SourceFiles(string dir) =>
        !Directory.Exists(dir) ? [] : Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root.");
    }
}
