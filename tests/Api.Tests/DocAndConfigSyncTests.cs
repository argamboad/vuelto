using System.Text.Json;
using System.Text.RegularExpressions;

namespace Perezosoft.Api.Tests;

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
        // where code reads a key nobody declared. Scoped to dotted literals at config-access sites.
        var documented = DocumentedConfigPaths();

        var readKeys = new HashSet<string>(StringComparer.Ordinal);
        var access = new Regex(@"(?:GetSection|GetValue<[^>]*>|GetValue|configuration|config|Configuration)\s*[\(\[]\s*""([A-Za-z][A-Za-z0-9]*(?::[A-Za-z0-9]+)+)""");
        // Keys hoisted into a const are read via the identifier, so the call-site regex above never sees the
        // literal — a blind spot that silently exempted whole files (Proxy:*, Rls:*) from this gate. Capture
        // the declarations too, so naming a key doesn't opt it out of being documented.
        var constKey = new Regex(@"const\s+string\s+\w+\s*=\s*""([A-Za-z][A-Za-z0-9]*(?::[A-Za-z0-9]+)+)""");
        // v3 TR-9 (T47): two more read shapes the gate was blind to. (1) A SINGLE-segment GetSection
        // ("Admin") binds a whole options class without any dotted literal at the call site. (2) Raw
        // Environment.GetEnvironmentVariable reads bypass IConfiguration entirely (deploy-injected
        // values like APP_BUILD_COMMIT) — Section__Sub names map onto config paths; FLAT names must be
        // documented verbatim in .env.example.
        var sectionOnly = new Regex(@"GetSection\s*\(\s*""([A-Za-z][A-Za-z0-9]*)""\s*\)");
        var envRead = new Regex(@"Environment\.GetEnvironmentVariable\s*\(\s*""([A-Za-z_][A-Za-z0-9_]*)""\s*\)");
        foreach (var f in SourceFiles(Path.Combine(RepoRoot(), "src")))
        {
            var text = File.ReadAllText(f);
            foreach (Match m in access.Matches(text))
                readKeys.Add(m.Groups[1].Value);
            foreach (Match m in constKey.Matches(text))
                readKeys.Add(m.Groups[1].Value);
            foreach (Match m in sectionOnly.Matches(text))
                readKeys.Add(m.Groups[1].Value);
            foreach (Match m in envRead.Matches(text))
                readKeys.Add(m.Groups[1].Value.Contains("__") ? m.Groups[1].Value.Replace("__", ":") : m.Groups[1].Value);
        }

        // A read key is OK if it equals a documented path, is a prefix of one (a section), or a documented
        // path is a prefix of it (a leaf under a documented section).
        var undocumented = readKeys
            .Where(k => !documented.Any(d =>
                d.Equals(k, StringComparison.Ordinal)
                || d.StartsWith(k + ":", StringComparison.Ordinal)
                || k.StartsWith(d + ":", StringComparison.Ordinal)))
            .OrderBy(k => k)
            .ToList();

        Assert.True(undocumented.Count == 0,
            $"Config keys read in code but not in appsettings*.json or .env.example: {string.Join(", ", undocumented)}");
    }

    /// <summary>Flattened config paths (Section:Key) declared in appsettings*.json + .env.example.</summary>
    private static HashSet<string> DocumentedConfigPaths()
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var json in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Api"), "appsettings*.json"))
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
