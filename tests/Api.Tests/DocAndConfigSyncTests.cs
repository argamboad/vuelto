using System.Text.Json;
using System.Text.RegularExpressions;

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
            .Where(n => n is not "ITenantScoped") // ITenantScoped = marker interface, not an entity
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
        var unnamed = ConfigBound().Where(t => !IsBuiltKeyByKey(t) && SectionNameOf(t) is null).Select(t => t.FullName).ToList();
        Assert.True(unnamed.Count == 0,
            $"Settings classes bound from config must declare `public const string SectionName`: {string.Join(", ", unnamed)}");
    }

    [Fact]
    public void EverySettingsSection_AndItsProperties_AreDocumented()
    {
        var missing = UndocumentedSettings(SettingsClasses(), DocumentedConfigPaths());
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
        var sections = SettingsClasses().Select(SectionNameOf).OfType<string>().ToList();
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
        var gated = SettingsClasses()
            .Where(t => SectionNameOf(t) is not null && t.GetProperty("Enabled")?.PropertyType == typeof(bool))
            .Select(t => SectionNameOf(t)!)
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
            if (SectionNameOf(t) is not { } section) continue;
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

    private static string? SectionNameOf(Type t) =>
        t.GetField("SectionName", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) is { IsLiteral: true } f
            ? (string?)f.GetRawConstantValue()
            : null;

    // The settings classes bound from configuration: those that declare a SectionName, plus any the source uses as
    // options (Configure<T>, AddOptions<T>, IOptions*<T>, .Get<T>()). The name alone is not enough: this app's
    // entities and EF migrations end in "Settings" too (BudgetSettings, AddBudgetSettings), and they are not config.
    private static IEnumerable<Type> ConfigBound()
    {
        var use = new Regex(@"(?:Configure|AddOptions|IOptions|IOptionsMonitor|IOptionsSnapshot|\.Get)<(\w+)>");
        var used = SourceFiles(Path.Combine(RepoRoot(), "src"))
            .SelectMany(f => use.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);
        return SettingsClasses().Where(t => SectionNameOf(t) is not null || used.Contains(t.Name));
    }

    private static bool IsBuiltKeyByKey(Type t) =>
        t.GetInterfaces().Any(i => i.Name.StartsWith('I') && i.Name.EndsWith("Settings", StringComparison.Ordinal));

    private static IEnumerable<Type> SettingsClasses() =>
        new[] { typeof(global::Vuelto.Api.Configuration.BillingSettings).Assembly, typeof(global::Vuelto.Infrastructure.Email.SmtpSettings).Assembly,
                typeof(global::Vuelto.Core.Abstractions.IEmailSender).Assembly }
            .Distinct()
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false, IsPublic: true } && t.Name.EndsWith("Settings", StringComparison.Ordinal));

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
