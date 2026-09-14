using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vuelto.Api.Tests;

/// <summary>
/// v3 audit T60 (Group L): the last three [machine] rules from FOUNDATION_RULES_v2 that had no
/// standing gate — promoted here so every machine rule is enforced, not aspirational.
/// R61: one SDK pin source (global.json / Dockerfile tags / CI all agree — the DEP-4 drift that
/// caused recurring red builds). R68: host parity (both index.html files load the identical RCL
/// script set — a divergence ships a web-only or native-only breakage, NATIVE_PARITY rule).
/// R75: doc-map + QA-count sync (every top-level docs/*.md is in the CLAUDE.md map; the "N cases"
/// figure matches the QA plan — the drift T54 just hand-fixed, now machine-held).
/// </summary>
public class EnforcementGateTests
{
    [Fact]
    public void SdkPin_HasOneSource_AllConsumersAgree() // R61
    {
        var root = RepoRoot();

        // global.json is THE source (v3 DEP-4 / CLAUDE.md bump-together playbook).
        using var globalJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "global.json")));
        var sdk = globalJson.RootElement.GetProperty("sdk").GetProperty("version").GetString()!;

        var dockerfile = File.ReadAllText(Path.Combine(root, "Dockerfile"));
        var buildTag = Regex.Match(dockerfile, @"dotnet/sdk:([0-9.]+)").Groups[1].Value;
        Assert.True(sdk == buildTag,
            $"Dockerfile build image (sdk:{buildTag}) != global.json ({sdk}) — bump them TOGETHER (CLAUDE.md playbook).");

        // The runtime tag must match the ASP.NET package line the app compiles against.
        var packages = File.ReadAllText(Path.Combine(root, "Directory.Packages.props"));
        var aspNetPackage = Regex.Match(packages, @"Microsoft\.AspNetCore\.Authentication\.JwtBearer""\s+Version=""([0-9.]+)""").Groups[1].Value;
        var runtimeTag = Regex.Match(dockerfile, @"dotnet/aspnet:([0-9.]+)").Groups[1].Value;
        Assert.True(aspNetPackage == runtimeTag,
            $"Dockerfile runtime image (aspnet:{runtimeTag}) != the ASP.NET package line ({aspNetPackage}).");

        // CI must read the SDK from global.json, never hardcode one.
        var ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        Assert.Contains("global-json-file: global.json", ci);
        Assert.DoesNotContain("dotnet-version:", ci); // a hardcoded setup-dotnet version would fork the pin
    }

    [Fact]
    public void HostIndexHtml_ReferenceTheIdenticalRclScriptSet() // R68
    {
        // The RCL's js contracts (theme pre-paint, MFA QR) must load in BOTH hosts — a script added
        // to one index.html only ships a host-specific breakage (NATIVE_PARITY maintainer rule:
        // "index.html sync"). Compare the full RCL script sets, not a hardcoded list.
        var root = RepoRoot();
        static HashSet<string> Scripts(string path) =>
            [.. Regex.Matches(File.ReadAllText(path), @"_content/[A-Za-z0-9./_-]+\.js").Select(m => m.Value)];

        var web = Scripts(Path.Combine(root, "src", "Web", "wwwroot", "index.html"));
        var maui = Scripts(Path.Combine(root, "src", "Maui", "wwwroot", "index.html"));
        Assert.NotEmpty(web); // probe alive

        var webOnly = web.Except(maui).ToList();
        var mauiOnly = maui.Except(web).ToList();
        Assert.True(webOnly.Count == 0 && mauiOnly.Count == 0,
            "The two hosts' index.html RCL script sets diverged — add the script to BOTH "
            + $"(web-only: [{string.Join(", ", webOnly)}], maui-only: [{string.Join(", ", mauiOnly)}]).");
    }

    [Fact]
    public void EveryCiJob_EitherGatesOnChanges_OrIsOnTheAlwaysRunList() // LOCALCI-3
    {
        // The point of the paths gate is that a docs-only push stops billing thirty minutes for
        // markdown. A new job added without `needs: changes` silently undoes that for every future
        // change, and nothing else would notice — the build stays green, it just costs again.
        //
        // The ALLOWLIST is the interesting half. These two must never become code-gated:
        //   secret-scan  — a credential pasted into a markdown file is still a leaked credential.
        //   qa-artifacts — editing the plan without regenerating the PDFs is the ONLY way to break
        //                  it, so gating it on code would switch it off for precisely the change it
        //                  exists to catch.
        //   changes      — it is the gate.
        //   deploy-*     — they gate transitively, through the jobs they need.
        string[] alwaysRun = ["changes", "secret-scan", "qa-artifacts", "deploy-staging", "deploy-prod"];

        var ci = File.ReadAllLines(Path.Combine(RepoRoot(), ".github", "workflows", "ci.yml"));

        // Job blocks are the two-space keys under `jobs:`; a block runs to the next such key. Start
        // AFTER `jobs:` — the trigger list above it uses the same indentation, so `push` and
        // `schedule` otherwise read as jobs with no gate.
        var jobsAt = Array.FindIndex(ci, l => l.TrimEnd() == "jobs:");
        Assert.True(jobsAt >= 0, "could not find the `jobs:` key in ci.yml");

        var starts = ci.Select((line, i) => (line, i))
            .Where(x => x.i > jobsAt && Regex.IsMatch(x.line, @"^  [a-z][a-z0-9-]*:\s*$"))
            .ToList();
        Assert.True(starts.Count > 8, "could not find the job list — has ci.yml been restructured?");

        var ungated = new List<string>();
        for (var n = 0; n < starts.Count; n++)
        {
            var name = starts[n].line.Trim().TrimEnd(':');
            if (alwaysRun.Contains(name)) continue;

            var end = n + 1 < starts.Count ? starts[n + 1].i : ci.Length;
            var body = string.Join("\n", ci[starts[n].i..end]);

            if (!body.Contains("needs: changes", StringComparison.Ordinal)
                || !body.Contains("needs.changes.outputs.", StringComparison.Ordinal))
            {
                ungated.Add(name);
            }
        }

        Assert.True(ungated.Count == 0,
            "These CI jobs run on every push regardless of what changed — add `needs: changes` and an "
            + "`if:` on one of its outputs, or add them to the allowlist in this test with a reason: "
            + string.Join(", ", ungated));
    }

    [Fact]
    public void MarkdownAnywhere_IsNeverCodeOrNative() // LOCALCI-3 follow-up
    {
        // A docs-only pull request that touched tests/E2E.Tests/README.md billed the FULL run — build,
        // test, e2e, docker, and both native builds — because the classifier's regexes key on the
        // directory (`^tests/`, `tests/E2E\.Tests/`) and a README lives in one. A markdown file cannot
        // change what the code does or how it builds, wherever it sits. This models the classifier
        // script faithfully: the same regexes, applied after the same markdown exclusion, so the
        // assertion cannot pass while the workflow still bills for a README.
        var ci = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "ci.yml"));

        static string Extract(string ci, string name)
        {
            var m = Regex.Match(ci, name + @"=\$\(match (?:""\$\w+"" )?'([^']+)'\)");
            Assert.True(m.Success, $"could not find the `{name}=` regex in the changes step of ci.yml");
            return m.Groups[1].Value;
        }
        var code = new Regex(Extract(ci, "code"));
        var native = new Regex(Extract(ci, "native"));
        var docs = new Regex(Extract(ci, "docs"));

        // The exclusion the script applies before the code/native match. Absent ⇒ nothing excluded,
        // which is exactly the defect: the test then sees the README classified as code.
        var excl = Regex.Match(ci, @"codefiles=\$\(printf '%s\\n' ""\$files"" \| grep -vE '([^']+)'");
        var exclude = excl.Success ? new Regex(excl.Groups[1].Value) : null;

        bool Code(string p) => (exclude is null || !exclude.IsMatch(p)) && code.IsMatch(p);
        bool Native(string p) => (exclude is null || !exclude.IsMatch(p)) && native.IsMatch(p);

        // Markdown, wherever it lives, is neither.
        Assert.False(Code("tests/E2E.Tests/README.md"), "a README under tests/ must not count as code");
        Assert.False(Native("tests/E2E.Tests/README.md"), "a README under tests/E2E.Tests/ must not trigger the native legs");
        Assert.False(Code("src/Api/Features/Notes/README.md"), "a README under src/ must not count as code");
        Assert.False(Code("docs/QA_TEST_PLAN.md"));

        // And the exclusion must not have eaten anything real.
        Assert.True(Code("tests/Api.Tests/EnforcementGateTests.cs"));
        Assert.True(Code("src/Api/Program.cs"));
        Assert.True(Native("src/Api/Program.cs"));
        Assert.True(Native("tests/E2E.Tests/BillingJourneyTests.cs"));
        Assert.True(Code(".github/workflows/ci.yml"));
        Assert.True(Code("src/Api/packages.lock.json"));
        Assert.True(docs.IsMatch("docs/QA_TEST_PLAN.md"), "docs= is computed on the UNFILTERED list, so markdown under docs/ still counts as docs");
    }

    [Fact]
    public void TheTwoGatesThatCatchDocsMistakes_AreNeverCodeGated() // LOCALCI-3
    {
        // Stated separately from the test above because it is the opposite failure: not "someone
        // forgot the gate" but "someone added it where it does harm". A docs-only change is exactly
        // when these two matter, so gating them on code would blind CI to the one class of mistake
        // a docs commit can make.
        var ci = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "ci.yml"));

        foreach (var job in new[] { "secret-scan", "qa-artifacts" })
        {
            var block = Regex.Match(ci, $@"(?ms)^  {Regex.Escape(job)}:\s*$.*?(?=^  [a-z][a-z0-9-]*:\s*$)");
            Assert.True(block.Success, $"{job} not found in ci.yml");
            Assert.DoesNotContain("needs.changes.outputs.code", block.Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryContainerImage_IsPinned_NotFloating() // v3 DEP-9, widened after the 2026-09-11 outage
    {
        // DEP-9 says pin container images, never `:latest`. It was applied to the CI workflow's service
        // images by hand and never machine-checked, so the Testcontainers fixtures and the dev compose
        // file kept floating tags. On 2026-09-11 MinIO's Docker Hub repository stopped serving pulls
        // entirely and `minio/minio:latest` took build-test down on every branch at once — with no
        // pinned known-good to fall back to, which is the whole cost of a floating tag. This gate covers
        // the surfaces the hand-applied convention missed: test fixtures and compose.
        var root = RepoRoot();
        var offenders = new List<string>();

        // Testcontainers builders: new XxxBuilder("image:tag")
        var builderImage = new Regex(@"new\s+\w*Builder\s*\(\s*""([^""]+)""");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            foreach (Match m in builderImage.Matches(File.ReadAllText(file)))
                Check(m.Groups[1].Value, Path.GetFileName(file));
        }

        // Compose services: `image: repo/name:tag`
        var composeImage = new Regex(@"(?m)^\s*image:\s*([^\s#]+)");
        var compose = Path.Combine(root, "docker-compose.yml");
        if (File.Exists(compose))
            foreach (Match m in composeImage.Matches(File.ReadAllText(compose)))
                Check(m.Groups[1].Value, "docker-compose.yml");

        Assert.True(offenders.Count == 0,
            "Container images must carry an explicit, non-floating tag (v3 DEP-9) — an unpinned image "
            + "turns any upstream registry change into an immediate CI outage with nothing to fall back "
            + $"on: {string.Join(", ", offenders)}");

        void Check(string image, string where)
        {
            // Ignore build-arg/variable references and anything that isn't an image reference.
            if (image.Contains('$') || image.Contains('{')) return;
            // A tag is the part after the LAST colon, provided that colon isn't the registry's port.
            var lastColon = image.LastIndexOf(':');
            var tag = lastColon > 0 && !image[(lastColon + 1)..].Contains('/') ? image[(lastColon + 1)..] : null;
            if (tag is null)
                offenders.Add($"{where}: '{image}' has no tag (implicitly :latest)");
            else if (tag.Equals("latest", StringComparison.OrdinalIgnoreCase))
                offenders.Add($"{where}: '{image}' is pinned to :latest");
        }
    }

    [Fact]
    public void ClaudeMdDocMap_ListsEveryTopLevelDoc() // R75, doc-map half
    {
        var root = RepoRoot();
        var claudeMd = File.ReadAllText(Path.Combine(root, "CLAUDE.md"));

        var missing = Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.TopDirectoryOnly)
            .Select(f => "docs/" + Path.GetFileName(f))
            .Where(rel => !claudeMd.Contains(rel, StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0,
            "Top-level docs missing from the CLAUDE.md doc map — the map is what makes a doc visible "
            + $"to every session (v3 TR-1): {string.Join(", ", missing)}");
    }

    [Fact]
    public void ClaudeMdQaCaseCount_MatchesTheQaPlan() // R75, count half
    {
        var root = RepoRoot();
        var actual = Regex.Matches(File.ReadAllText(Path.Combine(root, "docs", "QA_TEST_PLAN.md")),
            @"(?m)^### QA-").Count;

        var claimMatch = Regex.Match(File.ReadAllText(Path.Combine(root, "CLAUDE.md")), @"\((\d+) cases:");
        Assert.True(claimMatch.Success, "CLAUDE.md no longer states the QA case count as '(N cases:' — update this gate with it.");
        var claimed = int.Parse(claimMatch.Groups[1].Value);

        Assert.True(claimed == actual,
            $"CLAUDE.md claims {claimed} QA cases but QA_TEST_PLAN.md defines {actual} — update the doc-map row "
            + "(the figure drifted twice before this gate existed: v3 TR-2, T54).");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root.");
    }
}
