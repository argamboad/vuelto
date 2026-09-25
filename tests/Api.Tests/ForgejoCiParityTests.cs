using System.Text.RegularExpressions;

namespace Vuelto.Api.Tests;

/// <summary>
/// R80 (LOCALCI-4, ADR-028): the Forgejo pipeline (.forgejo/workflows/ci.yml) is a COPY of the GitHub
/// one, and a copy only stays honest if something fails when the two drift. These facts hold the copy to
/// the original everywhere they must agree — the job list, the runner labels, every pinned version, the
/// change classifier — and pin down the handful of places where they must differ (Apple jobs wait for a
/// registered Mac, deploys publish to `deploy/*` branches, prod is a manual dispatch). A change to one
/// workflow without the other fails here, naming what drifted.
/// </summary>
public class ForgejoCiParityTests
{
    private const string GitHubCi = ".github/workflows/ci.yml";
    private const string ForgejoCi = ".forgejo/workflows/ci.yml";
    private const string DeployWorkflow = ".forgejo/workflows/deploy.yml";

    [Fact]
    public void ForgejoCopy_HasTheSameJobs_OnTheSameRunners()
    {
        var github = Jobs(Read(GitHubCi));
        var forgejo = Jobs(Read(ForgejoCi));

        // The copy may add jobs that only exist because the runners are the maintainer's own machines.
        // Each one is listed here with its reason; everything else must match GitHub job for job.
        string[] forgejoOnly = ["mac"]; // is the MacBook awake? — it is a laptop, not a datacenter VM
        Assert.True(github.Keys.ToHashSet().SetEquals(forgejo.Keys.Except(forgejoOnly)),
            "The two CI workflows must define the same jobs — add or remove the job in BOTH files, or "
            + "list a Forgejo-only job in this test with its reason "
            + $"(github-only: [{string.Join(", ", github.Keys.Except(forgejo.Keys))}], "
            + $"forgejo-only: [{string.Join(", ", forgejo.Keys.Except(github.Keys).Except(forgejoOnly))}]).");

        // The Forgejo runners carry the hosted labels on purpose, so `runs-on` is identical line for line —
        // except the jobs that bind fixed host ports, which go to the one-job-at-a-time WSL runner (the
        // WSL runner's jobs share one network namespace). A new port-binding job belongs in this list.
        var hostPortJobs = new Dictionary<string, string>
        {
            ["e2e"] = "ubuntu-host-ports",
            ["native-smoke-android"] = "ubuntu-host-ports",
        };
        Assert.All(forgejoOnly, id => Assert.Equal("ubuntu-latest", RunsOn(forgejo[id]))); // never on the machine it asks about

        var drifted = github.Keys
            .Where(id => (hostPortJobs.GetValueOrDefault(id) ?? RunsOn(github[id])) != RunsOn(forgejo[id]))
            .Select(id => $"{id} ({RunsOn(github[id])} vs {RunsOn(forgejo[id])})")
            .ToList();
        Assert.True(drifted.Count == 0, "runs-on differs between the two workflows: " + string.Join(", ", drifted));
        Assert.All(hostPortJobs.Keys, id => Assert.Equal("ubuntu-latest", RunsOn(github[id])));

        // The list above cannot go stale silently: a Linux job with service containers or a server bound to
        // a localhost port is a port-binding job, and it must be on the host-ports runner.
        var unsafeJobs = forgejo
            .Where(j => RunsOn(j.Value) == "ubuntu-latest"
                && (Regex.IsMatch(j.Value, @"(?m)^    services:") || j.Value.Contains("--urls http://localhost", StringComparison.Ordinal)))
            .Select(j => j.Key)
            .ToList();
        Assert.True(unsafeJobs.Count == 0,
            "These Linux jobs bind fixed ports but run on the shared runner — move them to ubuntu-host-ports "
            + "and add them to hostPortJobs: " + string.Join(", ", unsafeJobs));
    }

    [Fact]
    public void ForgejoCopy_KeepsEveryPinOfTheGitHubWorkflow()
    {
        var github = Read(GitHubCi);
        var forgejo = Read(ForgejoCi);

        // Each pattern captures a version literal; both files must hold the same SET of values for it.
        (string Name, string Pattern)[] pins =
        [
            ("gitleaks version", @"GITLEAKS_VERSION:\s*""([^""]+)"""),
            ("gitleaks checksum", @"GITLEAKS_SHA256:\s*""([^""]+)"""),
            ("mailpit image", @"axllent/mailpit:(v[\d.]+)"),
            ("postgres image", @"image:\s*postgres:(\S+)"),
            ("MAUI workload set", @"workload restore \S+ --version (\S+)"),
            ("Xcode", @"DEVELOPER_DIR:\s*(\S+)"),
            // upload-artifact is the one action whose version legitimately differs: v4+ refuses to run
            // off github.com, so the Forgejo copy pins v3 (asserted below).
            ("action versions", @"uses:\s*(actions/(?!upload-artifact)[\w-]+@v\d+)"),
            ("Java", @"java-version:\s*""([^""]+)"""),
            ("Python", @"python-version:\s*""([^""]+)"""),
            ("Android system image", @"""(system-images;[^""]+)"""),
        ];

        var drifted = new List<string>();
        foreach (var (name, pattern) in pins)
        {
            var a = Values(github, pattern);
            var b = Values(forgejo, pattern);
            Assert.True(a.Count > 0, $"the '{name}' pin was not found in {GitHubCi} — update this test with the workflow");
            if (!a.SetEquals(b))
                drifted.Add($"{name}: github [{string.Join(", ", a)}] vs forgejo [{string.Join(", ", b)}]");
        }
        Assert.True(drifted.Count == 0, "Bump pins in BOTH workflows together: " + string.Join("; ", drifted));

        // upload-artifact: GitHub on the current major, Forgejo on v3 (the last one that works off
        // github.com), and never as the verdict of the job that uses it.
        Assert.Matches(@"uses:\s*actions/upload-artifact@v[4-9]\d*", github);
        Assert.Matches(@"(?m)^\s+continue-on-error: true\n\s+uses: actions/upload-artifact@v3$", forgejo);
        Assert.DoesNotMatch(@"uses:\s*actions/upload-artifact@v(?!3\b)\d+", forgejo);

        // R61 applies to the copy too: the SDK comes from global.json, never from the workflow.
        Assert.Contains("global-json-file: global.json", forgejo);
        Assert.DoesNotContain("dotnet-version:", forgejo);

        // GitHub keeps least privilege on the job token (DEP-8); Forgejo ignores the key and warns on every
        // run, so the copy leaves it out — and must keep the note saying why.
        Assert.Matches(@"(?m)^permissions:\n  contents: read$", github);
        Assert.DoesNotMatch(@"(?m)^permissions:", forgejo);
        Assert.Contains("Forgejo does not support that key", forgejo, StringComparison.Ordinal);
    }

    [Fact]
    public void ForgejoCopy_ClassifiesChangesLikeGitHub()
    {
        // Character for character: each classifier regex, the markdown strip and the testdocs list. The copy used to
        // add only its own workflow file; since v4 T2 (R97) both count every file a test reads — Forgejo's workflows
        // and scripts included, which the tests read whichever forge runs them — so there is nothing left to differ.
        var github = Read(GitHubCi);
        var forgejo = Read(ForgejoCi);

        foreach (var name in new[] { "code", "native", "docs" })
        {
            var g = Classifier(github, name);
            var f = Classifier(forgejo, name);
            Assert.True(g == f, $"the `{name}=` regex drifted:\n  github:  {g}\n  forgejo: {f}");
        }
        foreach (var shape in new[] { @"grep -viE '([^']+)'", @"testdocs='([^']+)'" })
            Assert.Equal(Regex.Match(github, shape).Groups[1].Value, Regex.Match(forgejo, shape).Groups[1].Value);
    }

    [Fact]
    public void ChangedFileLists_AreByteSafe_InBothCopies() // v4 audit LB-DEP-2 (R137)
    {
        // By default git prints a path holding a byte >= 0x80 quoted and escaped: `src/Api/Features/Añadir.cs`
        // comes out as "src/Api/Features/AÃ±adir.cs". That no longer starts with `src/`, so the classifier
        // calls a code change code=false native=false docs=false and the push runs no gate at all. Turning
        // core.quotePath off makes git print the name as it is.
        foreach (var file in new[] { GitHubCi, ForgejoCi })
        {
            var yml = Read(file);
            Assert.Contains("git -c core.quotePath=false diff --name-only", yml, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"git diff --name-only", yml);
        }
    }

    [Fact]
    public void EveryCheckout_LeavesNoTokenBehind() // v4 audit DEP-14 (H2), R98
    {
        // Forgejo gives a job a token that can write to the repo and ignores the `permissions:` key GitHub uses
        // to narrow it. actions/checkout stores that token in the workspace's git config unless told not to,
        // where anything the job runs afterwards (a NuGet restore without a lockfile, npm, brew) could push with
        // it. So no checkout, in any workflow on either forge, keeps it.
        string[] workflows = [GitHubCi, ForgejoCi, DeployWorkflow, ".github/workflows/postman-sync.yml", ".forgejo/workflows/postman-sync.yml"];
        foreach (var file in workflows)
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
        foreach (var file in new[] { GitHubCi, ForgejoCi })
        {
            var qa = Jobs(Read(file))["qa-artifacts"];
            Assert.Contains("TOKEN: ${{ github.token }}", qa, StringComparison.Ordinal);
            Assert.Contains("http.extraheader=AUTHORIZATION: basic", qa, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ForgejoCi_RefusesToRunWhileItsBranchesAreUnprotected() // v4 audit DEP-13 (H1/H2), R98
    {
        // Branch protection is what stops the job token from pushing, and it lives in the forge's settings, not
        // in the repo — so every run checks it, before the classifier decides anything.
        var changes = Jobs(Read(ForgejoCi))["changes"];
        var check = changes.IndexOf("/branches/$branch", StringComparison.Ordinal);
        Assert.True(check >= 0, "the Forgejo `changes` job must read /branches/<name> for develop and main");
        Assert.True(check < changes.IndexOf("id: diff", StringComparison.Ordinal), "check the protection before classifying the change");
        Assert.Contains("for branch in develop main", changes, StringComparison.Ordinal);
        Assert.Contains("\"protected\":true", changes, StringComparison.Ordinal);
        Assert.Contains("::error::", changes, StringComparison.Ordinal);
        Assert.Contains("protect-branches.ps1", changes, StringComparison.Ordinal); // the error says how to fix it
    }

    [Fact]
    public void ForgejoDeploys_RunOnlyFromADispatch_BehindEveryGateAndSelectedSmoke()
    {
        var github = Jobs(Read(GitHubCi));
        var forgejo = Jobs(Read(ForgejoCi));
        string[] smokes = ["native-smoke-windows", "native-smoke-android", "native-smoke-apple"];

        foreach (var (job, target, branch) in new[] { ("deploy-staging", "staging", "develop"), ("deploy-prod", "prod", "main") })
        {
            var body = forgejo[job];

            // Every gate GitHub's deploy waits for, plus the native builds and the three smokes.
            var githubNeeds = NeedsList(github[job]);
            var forgejoNeeds = NeedsList(body);
            Assert.True(githubNeeds.IsSubsetOf(forgejoNeeds), $"{job} dropped a gate GitHub's deploy waits for: {string.Join(", ", githubNeeds.Except(forgejoNeeds))}");
            Assert.True(forgejoNeeds.IsSupersetOf(smokes.Append("native-build")), $"{job} must wait for the native builds and every smoke");

            // Trigger: a dispatch that asked for this target, on the branch Render follows for it. A push
            // deploys only staging, and only through the CI_DEPLOY_ON_PUSH knob (checked in the knobs test).
            Assert.Contains($"github.event_name == 'workflow_dispatch' && github.event.inputs.deploy == '{target}'", body, StringComparison.Ordinal);
            Assert.Contains($"github.ref == 'refs/heads/{branch}'", body, StringComparison.Ordinal);
            if (target == "prod")
                Assert.DoesNotContain("github.event_name == 'push'", body, StringComparison.Ordinal);

            // `!cancelled()` lets it run past a SKIPPED smoke; every gate must then be checked explicitly,
            // and a smoke may be anything but red.
            Assert.Contains("!cancelled()", body, StringComparison.Ordinal);
            foreach (var gate in githubNeeds.Append("native-build"))
                Assert.Contains($"needs.{gate}.result == 'success'", body, StringComparison.Ordinal);
            foreach (var smoke in smokes)
            {
                Assert.Contains($"needs.{smoke}.result != 'failure'", body, StringComparison.Ordinal);
                Assert.Contains($"needs.{smoke}.result != 'cancelled'", body, StringComparison.Ordinal);
            }

            // Render builds from GitHub — the commit goes to the same branch there, through the guarded script.
            Assert.Contains($"push-to-github.sh {branch}", body, StringComparison.Ordinal);
            // No GitHub Environments on Forgejo: an `environment:` key would be silently ignored.
            Assert.DoesNotMatch(@"(?m)^\s+environment:", body);
        }

        // The script only knows the two Render branches, and never overwrites what GitHub has.
        var script = Read(".forgejo/scripts/push-to-github.sh");
        Assert.Contains("develop|main) ;;", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--force", script, StringComparison.Ordinal);
        Assert.DoesNotContain("git push", Read(ForgejoCi), StringComparison.Ordinal); // only through the script
    }

    [Fact]
    public void ForgejoSmokes_RunOnDemandOrOnTheSchedule_AndPerPushOnlyThroughTheKnob()
    {
        // The smokes are the slow legs and the desk is one machine: by default they run when asked for
        // (`smokes` input) or on the Monday schedule. The dispatch offers exactly the targets the jobs
        // answer to, and the only way back to GitHub's per-push behaviour is the CI_SMOKES_ON_PUSH knob.
        var yml = Read(ForgejoCi);
        Assert.Matches(@"(?ms)^      smokes:\n.*?options: \[none, windows, android, apple, all\]", yml);
        Assert.Matches(@"(?ms)^      deploy:\n.*?options: \[none, staging, prod\]", yml);

        var forgejo = Jobs(yml);
        foreach (var (job, target) in new[] { ("native-smoke-windows", "windows"), ("native-smoke-android", "android"), ("native-smoke-apple", "apple") })
        {
            var body = forgejo[job];
            Assert.Contains("github.event_name == 'schedule' && vars.CI_WEEKLY_SMOKES != 'off'", body, StringComparison.Ordinal);
            Assert.Contains($"github.event.inputs.smokes == '{target}' || github.event.inputs.smokes == 'all'", body, StringComparison.Ordinal);
            Assert.Contains($"github.event_name == 'push' && github.ref == 'refs/heads/develop'\n              && (vars.CI_SMOKES_ON_PUSH == '{target}' || vars.CI_SMOKES_ON_PUSH == 'all')", body, StringComparison.Ordinal);
        }
        // The native BUILDS keep running per push — compile rot is caught within one merge (NATIVE-1).
        Assert.DoesNotContain("workflow_dispatch", forgejo["native-build"], StringComparison.Ordinal);
    }

    [Fact]
    public void E2e_RunsBesideTheBuilds_OnBothForges()
    {
        // From 2026-09-17 to 2026-09-22 Forgejo's e2e waited for build-test and native-build: on one machine
        // the test browser's Blazor boot died whenever the builds compiled beside it. That wait was ~4 min of
        // every run. The suite now reloads a dead boot itself (tests/E2E.Tests/BlazorBoot.cs), so the gate is
        // gone on both forges and stays gone — putting it back is the slow fix for a problem the suite absorbs.
        foreach (var file in new[] { GitHubCi, ForgejoCi })
            Assert.Equal(new HashSet<string> { "changes" }, NeedsList(Jobs(Read(file))["e2e"]));
        // And the report that says how often it had to: a silent retry would hide a machine getting worse.
        foreach (var file in new[] { GitHubCi, ForgejoCi })
            Assert.Contains("[blazor-boot] attempt", Read(file), StringComparison.Ordinal);
    }

    [Fact]
    public void ForgejoKnobs_AreTheDocumentedFour_AndProdNeverDeploysOnAPush()
    {
        // Every `vars.CI_*` the workflow reads must be one of the documented knobs (header comment + runbook
        // §10), and the deploy-on-push knob may only ever reach staging.
        var yml = Read(ForgejoCi);
        string[] knobs = ["CI_SMOKES_ON_PUSH", "CI_DEPLOY_ON_PUSH", "CI_WEEKLY_SMOKES", "CI_MACOS_RUNNER", "CI_MACOS_PROBE"];

        var used = Regex.Matches(yml, @"vars\.(CI_[A-Z_]+)").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.True(used.SetEquals(knobs), $"CI_* variables read by the workflow: [{string.Join(", ", used)}] — keep the header's KNOBS list and DEPLOYMENT.md §10 in step");
        foreach (var knob in knobs)
            Assert.Contains($"#   {knob}", yml, StringComparison.Ordinal); // listed in the header

        var forgejo = Jobs(yml);
        Assert.Contains("github.event_name == 'push' && vars.CI_DEPLOY_ON_PUSH == 'staging'", forgejo["deploy-staging"], StringComparison.Ordinal);
        Assert.DoesNotContain("CI_DEPLOY_ON_PUSH", forgejo["deploy-prod"], StringComparison.Ordinal);
        Assert.DoesNotContain("github.event_name == 'push'", forgejo["deploy-prod"], StringComparison.Ordinal);

        // The operator doc names each knob.
        var runbook = Read("docs/DEPLOYMENT.md");
        foreach (var knob in knobs)
            Assert.Contains($"`{knob}`", runbook, StringComparison.Ordinal);
    }

    [Fact]
    public void ForgejoAppleJobs_WaitForARegisteredMacThatIsAwake()
    {
        // A job whose label no runner carries would queue for 24 h on Forgejo, and one whose runner
        // disappears mid-job is killed as a zombie and turns the run red — neither is a defect in the
        // code. The `mac` job answers "is the MacBook there?" on an always-on runner, and the Apple jobs
        // gate on it, so an absent Mac means SKIPPED (neutral) plus a warning, not red and not queued.
        var forgejo = Jobs(Read(ForgejoCi));
        foreach (var job in new[] { "native-build-apple", "native-smoke-apple" })
        {
            Assert.Contains("needs.mac.outputs.online == 'true'", forgejo[job], StringComparison.Ordinal);
            Assert.Contains("mac", NeedsList(forgejo[job]));
        }

        var mac = forgejo["mac"];
        Assert.Contains("vars.CI_MACOS_RUNNER", mac, StringComparison.Ordinal); // the on/off knob still decides
        Assert.Contains("vars.CI_MACOS_PROBE", mac, StringComparison.Ordinal);  // host:port to test
        Assert.Contains("::warning::", mac, StringComparison.Ordinal);          // an absent Mac is a warning
        Assert.Contains("online=true", mac, StringComparison.Ordinal);
        Assert.Contains("online=false", mac, StringComparison.Ordinal);
    }

    [Fact]
    public void ForgejoShells_MatchGitHubsDefaults()
    {
        // GitHub runs an unspecified bash step as `bash -e {0}` (no pipefail); Forgejo adds pipefail, which
        // failed two steps written for GitHub on the first runs. The copy pins GitHub's semantics.
        var yml = Read(ForgejoCi);
        var header = yml[..yml.IndexOf("\njobs:", StringComparison.Ordinal)];
        Assert.Matches(@"(?m)^defaults:\n  run:\n    shell: bash -e \{0\}$", header);

        // Forgejo's default shell is bash on every OS; the Windows steps are PowerShell.
        var forgejo = Jobs(yml);
        Assert.Contains("shell: pwsh", forgejo["native-smoke-windows"], StringComparison.Ordinal);
        Assert.Contains("matrix.os == 'windows-latest' && 'pwsh' || 'bash -e {0}'", forgejo["native-build"], StringComparison.Ordinal);
    }

    [Fact]
    public void ForgejoPostmanSync_IsTheGitHubOneWithItsOwnPath()
    {
        var github = Read(".github/workflows/postman-sync.yml");
        var forgejo = Read(".forgejo/workflows/postman-sync.yml");

        // Identical but for its own path, and the `permissions:` block, which Forgejo ignores: every Forgejo workflow
        // leaves it out with the same note (v4 DEP-27, ForgejoWorkflows_LeaveOutPermissions_AndSayWhy).
        static string Body(string yml) => yml[yml.IndexOf("name: postman-sync", StringComparison.Ordinal)..].Replace("\r\n", "\n");
        var expected = Regex.Replace(
            Body(github).Replace(".github/workflows/postman-sync.yml", ".forgejo/workflows/postman-sync.yml", StringComparison.Ordinal),
            @"(?m)^permissions:\n  contents: read\n", PermissionsNote + "\n");
        Assert.Equal(expected, Body(forgejo));
    }

    // The one way the Forgejo workflows handle GitHub's least-privilege key (v4 audit DEP-27, R63/R98).
    private const string PermissionsNote =
        "# `permissions:` is left out: Forgejo does not support that key (it warns and ignores it), so the job token\n"
        + "# is treated as able to write. Why that is safe here: the note above ci.yml's `defaults:` (v4 audit DEP-14/27).";

    [Fact]
    public void ForgejoWorkflows_LeaveOutPermissions_AndSayWhy() // v4 audit DEP-27 (R98)
    {
        // Forgejo ignores `permissions:`, so the three Forgejo files used to handle it three ways: omitted with a
        // note (ci.yml), kept (postman-sync.yml), omitted without a word (deploy.yml). One way now: leave it out and
        // say why, so the least-privilege rule is visibly argued in every file instead of met, argued or ignored.
        var dir = Path.Combine(RepoRoot(), ".forgejo", "workflows");
        foreach (var file in Directory.EnumerateFiles(dir, "*.yml"))
        {
            var yml = File.ReadAllText(file).Replace("\r\n", "\n");
            Assert.DoesNotMatch(@"(?m)^\s*permissions:", yml);
            Assert.True(yml.Contains("Forgejo does not support that key", StringComparison.Ordinal),
                $"{Path.GetFileName(file)} leaves out `permissions:` and must say why (the PermissionsNote wording)");
        }
    }

    [Fact]
    public void NativeSmokeProviderProbe_MatchesTheStatusField_InAllFourSites() // v4 audit LB-DEP-4 (R139)
    {
        // The Windows and Apple smokes decide "the app booted" from a 200 on GET /api/auth/providers in api.log.
        // `.*200` matched a 200 anywhere later on the line, e.g. the elapsed time of a 404 ("200.1234ms"), or a
        // sibling route. Each of the four patterns (bash + PowerShell, both copies) must read the status field.
        var patterns = new[] { GitHubCi, ForgejoCi }
            .SelectMany(f => Regex.Matches(Read(f), @"(?:grep -cE|-Pattern) '(Request finished[^']*auth/providers[^']*)'").Select(m => (f, m.Groups[1].Value)))
            .ToList();
        Assert.Equal(4, patterns.Count);

        const string real = "info: Microsoft.AspNetCore.Hosting.Diagnostics[2] Request finished HTTP/1.1 GET http://localhost:5238/api/auth/providers - 200 - application/json;+charset=utf-8 14.2031ms";
        string[] impostors =
        [
            "info: Microsoft.AspNetCore.Hosting.Diagnostics[2] Request finished HTTP/1.1 GET http://localhost:5238/api/auth/providers - 404 - application/problem+json 200.1234ms",
            "info: Microsoft.AspNetCore.Hosting.Diagnostics[2] Request finished HTTP/1.1 GET http://localhost:5238/api/auth/providers - 500 0 - 205.0010ms",
            "info: Microsoft.AspNetCore.Hosting.Diagnostics[2] Request finished HTTP/1.1 GET http://localhost:5238/api/auth/providers-beta - 200 - application/json 3.1ms",
            "info: Microsoft.AspNetCore.Hosting.Diagnostics[2] Request finished HTTP/1.1 POST http://localhost:5238/api/auth/providers - 200 - application/json 3.1ms",
        ];
        foreach (var (file, pattern) in patterns)
        {
            Assert.Contains("providers - 200 ", pattern);
            Assert.Matches(pattern, real);
            foreach (var line in impostors)
                Assert.False(Regex.IsMatch(line, pattern), $"{file}: '{pattern}' matched a non-200 or sibling line: {line}");
        }
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

    private static string RunsOn(string job) => Regex.Match(job, @"(?m)^    runs-on:\s*(.+)$").Groups[1].Value.Trim();

    // `needs: a` or `needs: [a, b,\n    c]` — the list may wrap onto following lines.
    [Fact]
    public void AlreadyGreenDeploy_VerifiesTheCommit_AndPublishesThroughTheSameScript() // LOCALCI-4, 2026-09-22
    {
        // `.forgejo/workflows/deploy.yml` deploys a commit whose gates already passed, instead of re-running
        // them (~10 min on a laptop the maintainer is also using). The shortcut is only honest while it
        // VERIFIES that greenness against this instance's own API and refuses otherwise, so that is what is
        // pinned here — plus the two rules it shares with ci.yml's deploy jobs: dispatch only, and the push
        // to GitHub goes through the fast-forward script, never a raw `git push`.
        var yml = Read(DeployWorkflow);

        Assert.Contains("workflow_dispatch", yml, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  push:", yml, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  schedule:", yml, StringComparison.Ordinal);

        // It asks the API about THIS commit and treats anything but a green gate as a refusal.
        Assert.Contains("actions/tasks", yml, StringComparison.Ordinal);
        Assert.Contains("head_sha == env.SHA", yml, StringComparison.Ordinal);
        foreach (var gate in new[] { "changes", "build-test", "secret-scan", "qa-artifacts", "license-scan", "docker-build", "e2e" })
            Assert.Contains(gate, yml, StringComparison.Ordinal);
        Assert.Contains("native-build (", yml, StringComparison.Ordinal); // both matrix legs
        Assert.Matches(@"refusing to deploy", yml);

        // The verification runs BEFORE anything leaves this machine.
        Assert.True(yml.IndexOf("actions/tasks", StringComparison.Ordinal)
                    < yml.IndexOf("push-to-github.sh", StringComparison.Ordinal),
            "deploy.yml must verify the commit before pushing it to GitHub.");

        // Same publishing path as ci.yml: the script (fast-forward, never forced) and the shared smoke.
        Assert.Contains("push-to-github.sh", yml, StringComparison.Ordinal);
        Assert.Contains("deploy-smoke.sh", yml, StringComparison.Ordinal);
        Assert.DoesNotContain("git push", yml, StringComparison.Ordinal);
    }

    private static HashSet<string> NeedsList(string job)
    {
        var m = Regex.Match(job, @"(?ms)^    needs:\s*(\[[^\]]*\]|\S+)");
        Assert.True(m.Success, "no `needs:` in job");
        return [.. Regex.Matches(m.Groups[1].Value, @"[a-z][a-z0-9-]*").Select(x => x.Value)];
    }

    private static string Classifier(string yml, string name)
    {
        var m = Regex.Match(yml, name + @"=\$\(match (?:""\$\w+"" )?'([^']+)'\)");
        Assert.True(m.Success, $"no `{name}=` classifier regex");
        return m.Groups[1].Value;
    }

    private static HashSet<string> Values(string yml, string pattern) =>
        [.. Regex.Matches(yml, pattern).Select(m => m.Groups[1].Value)];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root.");
    }
}
