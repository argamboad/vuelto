using System.Diagnostics;
using System.Text;
using Vuelto.Api.Tests.Architecture;
using Xunit.Abstractions;
using static Vuelto.Api.Tests.Architecture.PlatformOwnership;

namespace Vuelto.Api.Tests;

/// <summary>
/// Arch A2 (#363), R160: the platform/app boundary has a map, a manifest and a stamp. <c>platform-ownership.json</c>
/// classes every tracked file (platform / adapts / app / sample); downstream, <c>tools/port-platform.ps1</c> writes
/// <c>tests/Api.Tests/App/platform-manifest.json</c> (the brand-normalised hash of every platform and adapts file at the
/// stamped platform commit) and <c>platform-stamp.json</c>; this class holds the repo to them. On the platform the stamp's
/// commit is null and only the map gate applies. The logic is <see cref="PlatformOwnership"/>, held on fixtures here,
/// and the tool is run for real against two scratch git repos so the PowerShell hash and the C# hash are pinned equal.
/// </summary>
public class PlatformOwnershipTests(ITestOutputHelper output)
{
    [Fact]
    public void OwnershipMap_ClassifiesEveryTrackedFile()
    {
        // A file nobody classified is a file nobody owns: its author decides at birth, or this fails the platform's own build.
        var root = RepoRoot();
        var rules = ParseRules(File.ReadAllText(Path.Combine(root, "platform-ownership.json")));
        Assert.True(rules.Count >= 40, $"probe: {rules.Count} rules parsed");
        // An app's own top-level folders (jigger-jot's seed/) are classed in its half of the map, after the platform's rules.
        var appMap = Path.Combine(root, "platform-ownership.App.json");
        if (File.Exists(appMap)) rules = [.. rules, .. ParseRules(File.ReadAllText(appMap))];
        var tracked = Git(root, "ls-files").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
        Assert.True(tracked.Count > 500, $"probe: git ls-files returned {tracked.Count} paths");

        var unclassified = tracked.Where(p => Classify(p, rules) is null).Order().ToList();
        Assert.True(unclassified.Count == 0, "tracked files with no ownership class — add a rule to platform-ownership.json (an app: platform-ownership.App.json): " + string.Join(", ", unclassified));

        // The decisions of the Architecture milestone, pinned: the A1 seams are the app's, the composition files the platform's,
        // the UI the app's (#368), the rules file the platform's.
        Assert.Equal(Class.App, Classify("src/Api/AppComposition.cs", rules));
        Assert.Equal(Class.App, Classify("src/Infrastructure/Persistence/AppDbContext.App.cs", rules));
        Assert.Equal(Class.App, Classify("tests/Api.Tests/App/AppAllowlists.cs", rules));
        Assert.Equal(Class.App, Classify("tests/Api.Tests/RulesEnforcement.App.cs", rules));
        Assert.Equal(Class.App, Classify("src/Api/Features/Budget/BudgetHandler.cs", rules));
        Assert.Equal(Class.Platform, Classify("src/Api/Program.cs", rules));
        Assert.Equal(Class.Platform, Classify("src/Infrastructure/Persistence/AppDbContext.cs", rules));
        Assert.Equal(Class.Platform, Classify("tests/Api.Tests/ArchitectureTests.cs", rules));
        Assert.Equal(Class.Platform, Classify("docs/audits/v4-2026-09/FOUNDATION_RULES_v3.md", rules));
        Assert.Equal(Class.Adapts, Classify("src/Shared.Ui/Pages/Household.razor", rules));
        Assert.Equal(Class.Adapts, Classify("tests/E2E.Tests/SessionJourneyTests.cs", rules));
        Assert.Equal(Class.Adapts, Classify(".github/workflows/ci.yml", rules));
        Assert.Equal(Class.Sample, Classify("src/Api/Features/Notes/NotesHandler.cs", rules));
    }

    [Fact]
    public void PlatformFiles_MatchTheStampedManifest_OrAreAllowlisted()
    {
        var root = RepoRoot();
        var (stampCommit, brand) = ParseStamp(File.ReadAllText(Path.Combine(root, "platform-stamp.json")));
        var manifestPath = Path.Combine(root, "tests", "Api.Tests", "App", "platform-manifest.json");
        if (stampCommit is null)
        {
            // This IS the platform: nothing to compare against, and a manifest here would be a stale artifact.
            Assert.False(File.Exists(manifestPath), "the platform carries no platform-manifest.json; it is written downstream by the port");
            output.WriteLine("platform-stamp.json: commit null — this is the platform; the downstream gate does not apply.");
            return;
        }
        Assert.True(File.Exists(manifestPath), "platform-stamp.json names a platform commit but tests/Api.Tests/App/platform-manifest.json is missing — run tools/port-platform.ps1");
        var (manifestCommit, entries) = ParseManifest(File.ReadAllText(manifestPath));
        var divergences = ParseDivergences(File.ReadAllText(Path.Combine(root, "tests", "Api.Tests", "App", "PlatformDivergences.json")));

        var verdict = Evaluate(entries, manifestCommit, stampCommit, divergences, brand,
            local => File.Exists(Path.Combine(root, local)) ? File.ReadAllBytes(Path.Combine(root, local)) : null);

        output.WriteLine($"platform {manifestCommit[..7]}: {verdict.PlatformFilesChecked} platform files checked, {divergences.Count} allowlisted divergence(s), {verdict.AdaptsDrift.Count} adapts file(s) differ:");
        foreach (var d in verdict.AdaptsDrift) output.WriteLine("  adapts: " + d);
        Assert.True(verdict.Failures.Count == 0, "platform files out of step with the stamped platform commit:\n  " + string.Join("\n  ", verdict.Failures));
    }

    [Fact]
    public void PlatformStamp_MatchesTheManifest()
    {
        var root = RepoRoot();
        var (stampCommit, _) = ParseStamp(File.ReadAllText(Path.Combine(root, "platform-stamp.json")));
        var manifestPath = Path.Combine(root, "tests", "Api.Tests", "App", "platform-manifest.json");
        if (stampCommit is null) { Assert.False(File.Exists(manifestPath)); return; }
        var (manifestCommit, _) = ParseManifest(File.ReadAllText(manifestPath));
        Assert.Equal(stampCommit, manifestCommit);
    }

    // ── the logic, on fixtures ──

    [Theory]
    [InlineData("src/Core/**", "src/Core/Entities/User.cs", true)]
    [InlineData("src/Core/**", "src/Core/User.cs", true)]
    [InlineData("src/Core/**", "src/Corex/User.cs", false)]
    [InlineData("**/packages.lock.json", "packages.lock.json", true)]
    [InlineData("**/packages.lock.json", "src/Api/packages.lock.json", true)]
    [InlineData("src/Api/*.csproj", "src/Api/Vuelto.Api.csproj", true)]
    [InlineData("src/Api/*.csproj", "src/Api/Sub/Vuelto.Api.csproj", false)]
    [InlineData("docs/audits/**/FOUNDATION_RULES*.md", "docs/audits/v4-2026-09/FOUNDATION_RULES_v3.md", true)]
    [InlineData("README.md", "docs/README.md", false)]
    public void Globs_AreGitStyle(string glob, string path, bool matches) => Assert.Equal(matches, GlobToRegex(glob).IsMatch(path));

    [Fact]
    public void NormalizedHash_IgnoresBrandLineEndingsBomAndTrailingWhitespace_ButNotContent()
    {
        var platform = Encoding.UTF8.GetBytes("namespace Vuelto.Api;\r\nclass Vuelto { } // vuelto  \r\n");
        var app = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("namespace Vuelto.Api;\nclass Vuelto { } // vuelto\n\n")).ToArray();
        Assert.Equal(NormalizedHash(platform, ["Vuelto", "vuelto"]), NormalizedHash(app, ["Vuelto", "vuelto"]));
        var changed = Encoding.UTF8.GetBytes("namespace Vuelto.Api;\nclass Vuelto { int x; } // vuelto\n");
        Assert.NotEqual(NormalizedHash(platform, ["Vuelto", "vuelto"]), NormalizedHash(changed, ["Vuelto", "vuelto"]));
        // A binary hashes raw: the brand map never touches it.
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 13 };
        Assert.Equal(NormalizedHash(png, ["Vuelto"]), NormalizedHash(png, ["Vuelto"]));
    }

    private static readonly Dictionary<string, string> Brand = new() { ["Vuelto"] = "Acme", ["vuelto"] = "acme" };

    private static List<ManifestEntry> Manifest(params (string Path, Class Class, string Content)[] files) =>
        files.Select(f => new ManifestEntry(f.Path, f.Class, NormalizedHash(Encoding.UTF8.GetBytes(f.Content), ["Vuelto", "vuelto"]))).ToList();

    private static Func<string, byte[]?> Repo(params (string Path, string Content)[] files)
    {
        var d = files.ToDictionary(f => f.Path, f => Encoding.UTF8.GetBytes(f.Content), StringComparer.Ordinal);
        return p => d.TryGetValue(p, out var b) ? b : null;
    }

    [Fact]
    public void Verdict_PassesAnUnchangedRepo_AndReportsAdaptsDrift()
    {
        var manifest = Manifest(("src/Core/A.cs", Class.Platform, "class Vuelto {}"), ("src/Shared.Ui/P.razor", Class.Adapts, "<h1>Vuelto</h1>"), ("src/Api/Features/X.cs", Class.App, "x"));
        var repo = Repo(("src/Core/A.cs", "class Acme {}"), ("src/Shared.Ui/P.razor", "<h1>Acme, the budget app</h1>"));
        var v = Evaluate(manifest, "abc", "abc", new Dictionary<string, string>(), Brand, repo);
        Assert.Empty(v.Failures);
        Assert.Equal(["src/Shared.Ui/P.razor"], v.AdaptsDrift);
        Assert.Equal(1, v.PlatformFilesChecked);
    }

    [Fact]
    public void Verdict_FailsAChangedPlatformFile_UnlessAllowlisted_AndFailsAnAllowlistThatRots()
    {
        var manifest = Manifest(("src/Core/Vuelto.Core.csproj", Class.Platform, "<Project/>"), ("src/Core/A.cs", Class.Platform, "class Vuelto {}"));
        var changed = Repo(("src/Core/Acme.Core.csproj", "<Project/>"), ("src/Core/A.cs", "class Acme { int x; }"));
        var none = new Dictionary<string, string>();
        Assert.Contains(Evaluate(manifest, "abc", "abc", none, Brand, changed).Failures, f => f.StartsWith("src/Core/A.cs: differs"));

        var allowlisted = new Dictionary<string, string> { ["src/Core/A.cs"] = "the app adds x (ADR-V001)" };
        Assert.Empty(Evaluate(manifest, "abc", "abc", allowlisted, Brand, changed).Failures);

        var identical = Repo(("src/Core/Acme.Core.csproj", "<Project/>"), ("src/Core/A.cs", "class Acme {}"));
        Assert.Contains(Evaluate(manifest, "abc", "abc", allowlisted, Brand, identical).Failures, f => f.Contains("identical to the platform again"));

        var missing = Repo(("src/Core/Acme.Core.csproj", "<Project/>"));
        Assert.Contains(Evaluate(manifest, "abc", "abc", none, Brand, missing).Failures, f => f.Contains("does not have"));

        var blankReason = new Dictionary<string, string> { ["src/Core/A.cs"] = "" };
        Assert.Contains(Evaluate(manifest, "abc", "abc", blankReason, Brand, changed).Failures, f => f.StartsWith("src/Core/A.cs: differs"));
    }

    [Fact]
    public void Verdict_FailsAStampThatDisagreesWithTheManifest_AndADivergenceForAnUnknownFile()
    {
        var manifest = Manifest(("src/Core/A.cs", Class.Platform, "a"));
        var repo = Repo(("src/Core/A.cs", "a"));
        Assert.Contains(Evaluate(manifest, "abc", "def", new Dictionary<string, string>(), Brand, repo).Failures, f => f.Contains("a port must update both"));
        var orphan = new Dictionary<string, string> { ["src/Api/Features/X.cs"] = "ours" };
        Assert.Contains(Evaluate(manifest, "abc", "abc", orphan, Brand, repo).Failures, f => f.Contains("the manifest does not know"));
    }

    // ── the tool, for real ──

    [Fact]
    public async Task PortTool_MergesPlatformAndAdaptsFiles_AndWritesTheManifestTheGateAccepts()
    {
        // Two scratch repos: a "platform" with two commits and an "app" cloned from the first with the brand renamed and
        // two local edits. The port to the second commit must bring the platform's changes, keep the app's, write the
        // manifest and the stamp — and the gate, computing the hash in C#, must accept what the tool hashed in PowerShell.
        using var scratch = new Scratch();
        var platform = scratch.Dir("platform");
        var app = scratch.Dir("app");
        const string map = """
            { "rules": [
              { "glob": "src/Api/Features/**", "class": "app" },
              { "glob": "platform-stamp.json", "class": "app" },
              { "glob": "src/Core/**", "class": "platform" },
              { "glob": "src/Shared.Ui/**", "class": "adapts" },
              { "glob": "platform-ownership.json", "class": "platform" },
              { "glob": "README.md", "class": "adapts" },
              { "glob": "logo.bin", "class": "adapts" } ] }
            """;
        Write(platform, "platform-ownership.json", map);
        Write(platform, "platform-stamp.json", """{ "platform": "p", "commit": null, "date": null, "brand": { "Vuelto": "Vuelto", "vuelto": "vuelto" } }""");
        Write(platform, "src/Core/Vuelto.Core.csproj", "<Project>v1</Project>\n");
        Write(platform, "src/Core/Thing.cs", "namespace Vuelto.Core;\n\npublic class Thing\n{\n    public int A = 1;\n}\n");
        Write(platform, "src/Shared.Ui/Page.razor", "<h1>Vuelto</h1>\n<p>line two</p>\n<p>line three</p>\n");
        Write(platform, "src/Api/Features/X/X.cs", "// platform sample\n");
        Write(platform, "README.md", "# Vuelto\n");
        File.WriteAllBytes(Path.Combine(platform, "logo.bin"), [0x89, 0x50, 0, 1, 2]);
        Git(platform, "init", "-q", "-b", "main");
        Git(platform, "add", "-A");
        Git(platform, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "v1");
        var v1 = Git(platform, "rev-parse", "HEAD").Trim();

        // The app: v1 with the brand renamed in content and file names, a stamp at v1, and its own edits to an adapts file and an app file.
        foreach (var f in Directory.EnumerateFiles(platform, "*", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}")))
        {
            var rel = Path.GetRelativePath(platform, f).Replace('\\', '/');
            var target = Path.Combine(app, rel.Replace("Vuelto", "Acme").Replace("vuelto", "acme"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (rel == "logo.bin") File.Copy(f, target);
            else File.WriteAllText(target, File.ReadAllText(f).Replace("Vuelto", "Acme").Replace("vuelto", "acme"));
        }
        Write(app, "platform-stamp.json", $$"""{ "platform": "p", "commit": "{{v1}}", "date": "2026-10-01", "brand": { "Vuelto": "Acme", "vuelto": "acme" } }""");
        Write(app, "src/Shared.Ui/Page.razor", "<h1>Acme, the budget app</h1>\n<p>line two</p>\n<p>line three</p>\n");
        Write(app, "src/Api/Features/X/X.cs", "// the app's own slice\n");
        Directory.CreateDirectory(Path.Combine(app, "tools")); // the script locates the repo from tools/ unless -Repo is given

        // Platform v2: a platform file changes, a platform file is added, an adapts file changes elsewhere than the app's edit,
        // the sample-ish app file changes (must not reach the app), a binary changes.
        Write(platform, "src/Core/Thing.cs", "namespace Vuelto.Core;\n\npublic class Thing\n{\n    public int A = 2;\n    public int B = 3;\n}\n");
        Write(platform, "src/Core/Added.cs", "namespace Vuelto.Core;\n\npublic class Added;\n");
        Write(platform, "src/Shared.Ui/Page.razor", "<h1>Vuelto</h1>\n<p>line two</p>\n<p>line three, revised</p>\n");
        Write(platform, "src/Api/Features/X/X.cs", "// platform sample, changed\n");
        File.WriteAllBytes(Path.Combine(platform, "logo.bin"), [0x89, 0x50, 0, 9, 9]);
        Git(platform, "add", "-A");
        Git(platform, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "v2");
        var v2 = Git(platform, "rev-parse", "HEAD").Trim();

        var (exit, log) = await RunToolAsync(platform, v2, app);
        Assert.True(exit == 0, log);

        Assert.Equal("namespace Acme.Core;\n\npublic class Thing\n{\n    public int A = 2;\n    public int B = 3;\n}\n", Read(app, "src/Core/Thing.cs"));
        Assert.Equal("namespace Acme.Core;\n\npublic class Added;\n", Read(app, "src/Core/Added.cs"));
        Assert.Equal("<h1>Acme, the budget app</h1>\n<p>line two</p>\n<p>line three, revised</p>\n", Read(app, "src/Shared.Ui/Page.razor")); // both edits, no markers
        Assert.Equal("// the app's own slice\n", Read(app, "src/Api/Features/X/X.cs"));                                                 // app class: untouched
        Assert.Equal(new byte[] { 0x89, 0x50, 0, 9, 9 }, File.ReadAllBytes(Path.Combine(app, "logo.bin")));                              // binary, unchanged locally: taken

        var (stampCommit, brand) = ParseStamp(Read(app, "platform-stamp.json"));
        Assert.Equal(v2, stampCommit);
        var (manifestCommit, entries) = ParseManifest(Read(app, "tests/Api.Tests/App/platform-manifest.json"));
        Assert.Equal(v2, manifestCommit);
        Assert.Equal(new[] { "README.md", "logo.bin", "platform-ownership.json", "src/Core/Added.cs", "src/Core/Vuelto.Core.csproj", "src/Core/Thing.cs", "src/Shared.Ui/Page.razor" }.Order(StringComparer.Ordinal), // the brand sets the order
            entries.Select(e => e.Path).Order(StringComparer.Ordinal));

        // The gate over the ported app: the PowerShell hashes must match the C# hashes of the renamed files.
        var verdict = Evaluate(entries, manifestCommit, stampCommit, new Dictionary<string, string>(), brand,
            local => File.Exists(Path.Combine(app, local)) ? File.ReadAllBytes(Path.Combine(app, local)) : null);
        Assert.True(verdict.Failures.Count == 0, string.Join("\n", verdict.Failures));
        Assert.Equal(["src/Shared.Ui/Page.razor"], verdict.AdaptsDrift); // the app's own edit, reported not failed

        // An edit to a platform file after the port is caught; allowlisting it with a reason clears it.
        Write(app, "src/Core/Thing.cs", Read(app, "src/Core/Thing.cs").Replace("B = 3", "B = 4"));
        var caught = Evaluate(entries, manifestCommit, stampCommit, new Dictionary<string, string>(), brand, local => File.Exists(Path.Combine(app, local)) ? File.ReadAllBytes(Path.Combine(app, local)) : null);
        Assert.Contains(caught.Failures, f => f.StartsWith("src/Core/Thing.cs: differs"));
        var cleared = Evaluate(entries, manifestCommit, stampCommit, new Dictionary<string, string> { ["src/Core/Thing.cs"] = "B is 4 here (ADR-A001)" }, brand, local => File.Exists(Path.Combine(app, local)) ? File.ReadAllBytes(Path.Combine(app, local)) : null);
        Assert.Empty(cleared.Failures);
    }

    [Fact]
    public async Task PortTool_WithoutApply_WritesNothing_AndRefusesAnUnknownCommit()
    {
        using var scratch = new Scratch();
        var platform = scratch.Dir("platform");
        var app = scratch.Dir("app");
        Write(platform, "platform-ownership.json", """{ "rules": [ { "glob": "platform-stamp.json", "class": "app" }, { "glob": "**", "class": "platform" } ] }""");
        Write(platform, "platform-stamp.json", """{ "platform": "p", "commit": null, "date": null, "brand": { "Vuelto": "Vuelto" } }""");
        Write(platform, "a.txt", "one\n");
        Git(platform, "init", "-q", "-b", "main"); Git(platform, "add", "-A"); Git(platform, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "v1");
        var v1 = Git(platform, "rev-parse", "HEAD").Trim();
        Write(app, "platform-stamp.json", """{ "platform": "p", "commit": null, "date": null, "brand": { "Vuelto": "Acme" } }""");
        Directory.CreateDirectory(Path.Combine(app, "tools"));

        var (dryExit, dryLog) = await RunToolAsync(platform, v1, app, apply: false);
        Assert.True(dryExit == 0, dryLog);
        Assert.Contains("Dry run", dryLog);
        Assert.False(File.Exists(Path.Combine(app, "a.txt")), "a dry run wrote a file");
        Assert.False(File.Exists(Path.Combine(app, "tests", "Api.Tests", "App", "platform-manifest.json")), "a dry run wrote the manifest");

        var (badExit, badLog) = await RunToolAsync(platform, "no-such-commit", app, apply: false);
        Assert.NotEqual(0, badExit);
        Assert.Contains("is not a commit", badLog);
    }

    // ── helpers ──

    private static async Task<(int Exit, string Output)> RunToolAsync(string platform, string to, string repo, bool apply = true)
    {
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-NoProfile", "-File", Path.Combine(RepoRoot(), "tools", "port-platform.ps1"), "-Platform", platform, "-To", to, "-Repo", repo })
            start.ArgumentList.Add(a);
        if (apply) start.ArgumentList.Add("-Apply");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh is not on PATH — the tools/ scripts require PowerShell 7");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout + await stderr);
    }

    private static void Write(string root, string rel, string content)
    {
        var full = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
    }

    private static string Read(string root, string rel) => File.ReadAllText(Path.Combine(root, rel)).Replace("\r\n", "\n");

    private static string Git(string repo, params string[] args)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-C"); start.ArgumentList.Add(repo);
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var o = p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd(); p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {e}");
        return o;
    }

    private sealed class Scratch : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ownership-" + Guid.NewGuid().ToString("N"));
        public string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* a scratch dir; the OS temp sweep gets it */ } catch (UnauthorizedAccessException) { }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.EnumerateFiles("*.slnx").Any()) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
