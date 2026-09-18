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
        // The only addition the copy may make is its own workflow file — the rest of each classifier
        // regex, and the markdown exclusion before it, must be character-for-character GitHub's.
        const string ownFile = @"|\.forgejo/workflows/ci\.yml";
        var github = Read(GitHubCi);
        var forgejo = Read(ForgejoCi);

        foreach (var name in new[] { "code", "native", "docs" })
        {
            var g = Classifier(github, name);
            var f = Classifier(forgejo, name);
            var stripped = f.Replace(ownFile + ")", ")", StringComparison.Ordinal);
            Assert.True(g == stripped, $"the `{name}=` regex drifted:\n  github:  {g}\n  forgejo: {f}");
        }
        Assert.Contains(ownFile, Classifier(forgejo, "code"), StringComparison.Ordinal);
        Assert.Contains(ownFile, Classifier(forgejo, "native"), StringComparison.Ordinal);

        const string exclusion = @"codefiles=\$\(printf '%s\\n' ""\$files"" \| grep -vE '([^']+)'";
        Assert.Equal(Regex.Match(github, exclusion).Groups[1].Value, Regex.Match(forgejo, exclusion).Groups[1].Value);
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
    public void ForgejoE2e_WaitsForTheHeavyBuilds()
    {
        // One machine: the test browser's Blazor boot failed 5 times in 14 runs, always while build-test
        // and the native builds were compiling beside it. e2e therefore runs after them on Forgejo. GitHub
        // keeps them parallel (one VM per job). Only additions are allowed — `changes` must stay.
        var github = NeedsList(Jobs(Read(GitHubCi))["e2e"]);
        var forgejo = NeedsList(Jobs(Read(ForgejoCi))["e2e"]);
        Assert.Equal(new HashSet<string> { "changes" }, github);
        Assert.True(forgejo.IsSupersetOf(["changes", "build-test", "native-build"]),
            $"Forgejo's e2e must wait for the heavy builds (needs: [{string.Join(", ", forgejo)}])");
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

        static string Body(string yml) => yml[yml.IndexOf("name: postman-sync", StringComparison.Ordinal)..];
        var expected = Body(github).Replace(".github/workflows/postman-sync.yml", ".forgejo/workflows/postman-sync.yml", StringComparison.Ordinal);
        Assert.Equal(expected, Body(forgejo));
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
