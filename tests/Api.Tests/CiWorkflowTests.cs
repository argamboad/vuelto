using System.Text.RegularExpressions;

namespace Vuelto.Api.Tests;

/// <summary>
/// ADR-031: the shape of the one CI workflow. The repo is private, so every Actions minute is billed and the
/// maintainer manages them by hand: a pull request runs the web gates only, a merge runs nothing, and the device
/// legs and the deploys run only from "Run workflow". These facts hold that shape, so a job that quietly starts
/// running on every PR again (or a deploy that fires on a push) fails here, naming it. The Forgejo-era parity
/// checks that also guarded this file (R98, R137, R139, Env L29/L30) live here too since `.forgejo/` was removed.
/// </summary>
public class CiWorkflowTests
{
    private const string Ci = ".github/workflows/ci.yml";

    private static readonly string[] WebGates = ["build-test", "license-scan", "docker-build", "e2e"];
    private static readonly string[] DocsGates = ["secret-scan", "qa-artifacts"];
    private static readonly string[] DeviceLegs =
        ["native-build", "native-release-android", "native-build-apple", "native-smoke-apple", "native-smoke-windows", "native-smoke-android"];

    [Fact]
    public void Ci_RunsOnPullRequestsAndOnDemand_NeverOnAPushOrASchedule() // ADR-031
    {
        var header = Read(Ci).Split("\njobs:\n")[0];
        var on = header[header.IndexOf("\non:\n", StringComparison.Ordinal)..];
        Assert.Contains("  pull_request:\n    branches: [main, develop]", on, StringComparison.Ordinal);
        Assert.Contains("  workflow_dispatch:", on, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?m)^  push:", on);
        Assert.DoesNotMatch(@"(?m)^  schedule:", on);

        // The two manual choices, with "none" as the default so a bare "Run workflow" does nothing costly.
        Assert.Contains("options: [none, staging, prod]", on, StringComparison.Ordinal);
        Assert.Contains("options: [none, android, windows, apple, all]", on, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(on, @"default: none").Count);
    }

    [Fact]
    public void EveryJob_GatesOnTheRunPlan_ForWhatItIs() // ADR-031
    {
        var jobs = Jobs(Read(Ci));
        foreach (var job in WebGates)
            Assert.Contains("if: needs.changes.outputs.web == 'true'", jobs[job], StringComparison.Ordinal);
        foreach (var job in DocsGates)
            Assert.Contains("if: needs.changes.outputs.checks == 'true'", jobs[job], StringComparison.Ordinal);

        var deviceOutputs = new Regex(@"if: needs\.changes\.outputs\.(android|windows|apple) == 'true'|if: needs\.changes\.outputs\.buildmatrix != '\[\]'");
        foreach (var job in DeviceLegs)
            Assert.True(deviceOutputs.IsMatch(jobs[job]), $"{job} is a device leg: it must run only when the run plan asks for its platform");

        // Every job is accounted for: a new one has to be placed in one of the groups above.
        var known = WebGates.Concat(DocsGates).Concat(DeviceLegs).Concat(["changes", "deploy-staging", "deploy-prod"]).ToHashSet();
        var unplaced = jobs.Keys.Where(k => !known.Contains(k)).ToList();
        Assert.True(unplaced.Count == 0, "jobs in no group (web gate, docs gate, device leg, deploy): " + string.Join(", ", unplaced));
    }

    [Fact]
    public void RunPlan_ChecksEveryPr_WebOnCodeOrDeploy_DevicesOnlyOnDispatch() // ADR-031
    {
        var changes = Jobs(Read(Ci))["changes"];
        var plan = changes[changes.IndexOf("# ci-logic begin: run-plan", StringComparison.Ordinal)..];
        // A pull request always gets the docs gates, and the web gates when code changed.
        Assert.Contains("if [ \"$EVENT\" = \"pull_request\" ]; then\n            checks=true; web=\"$CODE\"", plan, StringComparison.Ordinal);
        // A deploy runs every web gate whatever the diff.
        Assert.Contains("checks=true; web=true", plan, StringComparison.Ordinal);
        // The device legs are only ever chosen on a dispatch.
        Assert.Contains("if [ \"$EVENT\" = \"workflow_dispatch\" ]; then\n            case \"$DEVICES\" in", plan, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploys_RunOnlyFromADispatch_BehindEveryWebGate_OnTheirOwnBranch() // ADR-031, ADR-017
    {
        var jobs = Jobs(Read(Ci));
        foreach (var (job, target, branch) in new[] { ("deploy-staging", "staging", "develop"), ("deploy-prod", "prod", "main") })
        {
            var body = jobs[job];
            Assert.Contains($"if: github.event_name == 'workflow_dispatch' && inputs.deploy == '{target}'", body, StringComparison.Ordinal);
            Assert.Equal(WebGates.Concat(DocsGates).ToHashSet(), NeedsList(body));
            // The wrong branch fails loudly instead of deploying it.
            Assert.Contains($"if [ \"${{{{ github.ref }}}}\" != \"refs/heads/{branch}\" ]; then", body, StringComparison.Ordinal);
            Assert.DoesNotContain("github.event_name == 'push'", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryJob_HasATimeout() // ADR-031: GitHub's default is six hours, a month of minutes for one hung job
    {
        var missing = Jobs(Read(Ci)).Where(j => !Regex.IsMatch(j.Value, @"(?m)^    timeout-minutes: \d+")).Select(j => j.Key).ToList();
        Assert.True(missing.Count == 0, "jobs without timeout-minutes: " + string.Join(", ", missing));
    }

    [Fact]
    public void ChangedFileLists_AreByteSafe() // v4 audit LB-DEP-2 (R137)
    {
        // By default git prints a path holding a byte >= 0x80 quoted and escaped: `src/Api/Features/Añadir.cs`
        // comes out as "src/Api/Features/AÃ±adir.cs". That no longer starts with `src/`, so the classifier
        // calls a code change code=false native=false docs=false and the run skips every gate. Turning
        // core.quotePath off makes git print the name as it is.
        var yml = Read(Ci);
        Assert.Contains("git -c core.quotePath=false diff --name-only", yml, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"git diff --name-only", yml);
    }

    [Fact]
    public void EveryCheckout_LeavesNoTokenBehind() // v4 audit DEP-14 (H2), R98
    {
        // actions/checkout stores the job token in the workspace's git config unless told not to, where anything the
        // job runs afterwards (a NuGet restore without a lockfile, npm, brew) could push with it. So no checkout, in
        // any workflow, keeps it.
        foreach (var file in new[] { Ci, ".github/workflows/postman-sync.yml" })
        {
            var lines = Read(file).Split('\n');
            var checkouts = lines.Select((line, i) => (line, i)).Where(x => x.line.Contains("uses: actions/checkout@", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(checkouts);
            foreach (var (line, i) in checkouts)
            {
                // The step runs until the next line indented no deeper than its `- uses:`.
                var indent = line.IndexOf('-', StringComparison.Ordinal);
                var body = lines.Skip(i + 1).TakeWhile(l => l.Trim().Length == 0 || l.Length - l.TrimStart().Length > indent);
                Assert.True(body.Any(l => l.TrimStart().StartsWith("persist-credentials: false", StringComparison.Ordinal)),
                    $"{file}:{i + 1} checks out without `persist-credentials: false` — the job token would stay in .git/config.");
            }
        }

        // The one step that talks to the remote after checkout authenticates for that command only.
        var qa = Jobs(Read(Ci))["qa-artifacts"];
        Assert.Contains("TOKEN: ${{ github.token }}", qa, StringComparison.Ordinal);
        Assert.Contains("http.extraheader=AUTHORIZATION: basic", qa, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeSmokeProviderProbe_MatchesTheStatusField_InBothSites() // v4 audit LB-DEP-4 (R139)
    {
        // The Windows and Apple smokes decide "the app booted" from a 200 on GET /api/auth/providers in api.log.
        // `.*200` matched a 200 anywhere later on the line, e.g. the elapsed time of a 404 ("200.1234ms"), or a
        // sibling route. Both patterns (bash + PowerShell) must read the status field.
        var patterns = Regex.Matches(Read(Ci), @"(?:grep -cE|-Pattern) '(Request finished[^']*auth/providers[^']*)'").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(2, patterns.Count);

        const string real = "info: Microsoft.AspNetCore.Hosting.Diagnostics[2] Request finished HTTP/1.1 GET http://api.test/api/auth/providers - 200 - application/json;+charset=utf-8 14.2031ms";
        string[] impostors =
        [
            "info: Microsoft.AspNetCore.Hosting.Diagnostics[2] Request finished HTTP/1.1 GET http://api.test/api/auth/providers - 404 - application/problem+json 200.1234ms",
            "info: Microsoft.AspNetCore.Hosting.Diagnostics[2] Request finished HTTP/1.1 GET http://api.test/api/auth/providers - 500 0 - 205.0010ms",
            "info: Microsoft.AspNetCore.Hosting.Diagnostics[2] Request finished HTTP/1.1 GET http://api.test/api/auth/providers-beta - 200 - application/json 3.1ms",
            "info: Microsoft.AspNetCore.Hosting.Diagnostics[2] Request finished HTTP/1.1 POST http://api.test/api/auth/providers - 200 - application/json 3.1ms",
        ];
        foreach (var pattern in patterns)
        {
            Assert.Contains("providers - 200 ", pattern);
            Assert.Matches(pattern, real);
            foreach (var line in impostors)
                Assert.False(Regex.IsMatch(line, pattern), $"'{pattern}' matched a non-200 or sibling line: {line}");
        }
    }

    [Fact]
    public void E2eBrowser_ComesWithItsDeps_OnTheBareHostedRunner() // Env L24
    {
        // A hosted runner starts bare, so e2e installs Chromium's system packages with the browser; the journeys run
        // headless.
        Assert.Contains("playwright.ps1 install --with-deps chromium", Jobs(Read(Ci))["e2e"], StringComparison.Ordinal);
        Assert.Contains("<Headless>true</Headless>", Read("tests/E2E.Tests/playwright.runsettings"), StringComparison.Ordinal);
    }

    [Fact]
    public void MauiClassifier_CoversWhatTheAppIsBuiltFrom_AndNothingElse() // Env L29
    {
        // src/Maui references only src/Shared.Ui, which references nothing. Reported by the `changes` job for the
        // record (the device legs run on request since ADR-031), and kept exact so it can gate them again.
        var m = Regex.Match(Read(Ci), @"maui=\$\(match ""\$nativefiles"" '([^']+)'\)");
        Assert.True(m.Success, "no `maui=` classifier");
        var maui = new Regex(m.Groups[1].Value);
        foreach (var path in new[] { "src/Maui/MauiProgram.cs", "src/Shared.Ui/Pages/Login.razor", "Directory.Build.props",
                                     "Directory.Packages.props", "global.json", ".github/workflows/ci.yml",
                                     ".config/dotnet-tools.json", ".github/forbidden-licenses.json" })
            Assert.True(maui.IsMatch(path), $"{path} can break the MAUI build, so it must count as maui");
        foreach (var path in new[] { "src/Api/Program.cs", "src/Core/Entities/Tenant.cs", "src/Infrastructure/Email/BrandedEmail.cs",
                                     "src/Web/Program.cs", "tests/Api.Tests/AuthTests.cs", "tests/E2E.Tests/Journeys.cs", "docs/notes/not-a-real-doc.md" })
            Assert.False(maui.IsMatch(path), $"{path} cannot affect the MAUI app (it references only Shared.Ui)");
    }

    [Fact]
    public void ANewPushToAPullRequest_CancelsItsPreviousRun_AndNothingElse() // Env L30
    {
        // Grouped per pull request, so a superseded PR run stops billing. A dispatch groups by its own run id and is
        // never cancelled by another run.
        var header = Read(Ci).Split("\njobs:\n")[0];
        Assert.Contains("concurrency:\n  group: ${{ github.event_name == 'pull_request' && format('ci-pr-{0}', github.event.pull_request.number) || format('ci-run-{0}', github.run_id) }}\n  cancel-in-progress: true", header, StringComparison.Ordinal);
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relative)).ReplaceLineEndings("\n");

    // Job blocks are the two-space keys under `jobs:`; each runs to the next such key.
    private static Dictionary<string, string> Jobs(string yml)
    {
        var lines = yml.Split('\n');
        var jobsAt = Array.FindIndex(lines, l => l.TrimEnd() == "jobs:");
        Assert.True(jobsAt >= 0, "no `jobs:` key");
        var starts = lines.Select((line, i) => (line, i))
            .Where(x => x.i > jobsAt && Regex.IsMatch(x.line, @"^  [a-z][a-z0-9-]*:\s*$"))
            .ToList();
        var jobs = new Dictionary<string, string>();
        for (var n = 0; n < starts.Count; n++)
        {
            var end = n + 1 < starts.Count ? starts[n + 1].i : lines.Length;
            jobs[starts[n].line.Trim().TrimEnd(':')] = string.Join("\n", lines[starts[n].i..end]);
        }
        return jobs;
    }

    // `needs: a` or `needs: [a, b,\n    c]` — the list may wrap onto following lines.
    private static HashSet<string> NeedsList(string job)
    {
        var m = Regex.Match(job, @"(?ms)^    needs:\s*(\[[^\]]*\]|\S+)");
        Assert.True(m.Success, "no `needs:` in job");
        return [.. Regex.Matches(m.Groups[1].Value, @"[a-z][a-z0-9-]*").Select(x => x.Value)];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root (CLAUDE.md) not found.");
    }
}
