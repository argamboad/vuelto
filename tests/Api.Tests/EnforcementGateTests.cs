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
    public void Dockerfile_GivesTheAppUserAWritableStorageDir()
    {
        // Synced from perezosoft-platform #236. Local-disk file storage (ADR-010) defaults to ./storage under the app
        // base dir = /app/storage. The runtime runs as the non-root `app` user and WORKDIR /app is created by root, so
        // without this every stored file (CSV export, report PDF, household export) failed on staging with "Access to
        // the path '/app/storage' is denied" (2026-09-17). The directory must exist, owned by `app`, BEFORE `USER app`.
        var dockerfile = File.ReadAllText(Path.Combine(RepoRoot(), "Dockerfile"));
        var runtime = dockerfile[dockerfile.IndexOf("AS runtime", StringComparison.Ordinal)..];
        var userAt = runtime.IndexOf("USER app", StringComparison.Ordinal);
        Assert.True(userAt > 0, "The runtime stage must switch to the non-root `app` user.");

        var beforeUser = runtime[..userAt];
        Assert.Matches(@"mkdir -p [^\n]*/app/storage", beforeUser);
        Assert.Matches(@"chown [^\n]*app:app [^\n]*/app/storage", beforeUser);

        // CI proves it on the built image, in both workflow copies (R80 keeps them together).
        foreach (var workflow in new[] { Path.Combine(".github", "workflows", "ci.yml"), Path.Combine(".forgejo", "workflows", "ci.yml") })
            Assert.Contains("/app/storage/.write-probe", File.ReadAllText(Path.Combine(RepoRoot(), workflow)));
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

    [Theory] // LOCALCI-4: the Forgejo copy (R80) is held to the same gate
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".forgejo/workflows/ci.yml")]
    public void EveryCiJob_EitherGatesOnChanges_OrIsOnTheAlwaysRunList(string workflow) // LOCALCI-3
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
        //   mac         — LOCALCI-4, Forgejo only: a seconds-long probe asking whether the MacBook is
        //                 awake, so the Apple jobs can skip instead of queueing. It gates nothing itself.
        string[] alwaysRun = ["changes", "secret-scan", "qa-artifacts", "deploy-staging", "deploy-prod", "mac"];

        var ci = File.ReadAllLines(Path.Combine(RepoRoot(), workflow));

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

            // `needs: changes` or a list that includes it (`needs: [changes, e2e]`).
            if (!Regex.IsMatch(body, @"(?m)^    needs:\s*(changes\s*$|\[[^\]]*\bchanges\b)")
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

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".forgejo/workflows/ci.yml")]
    public void MarkdownAnywhere_IsNeverCodeOrNative(string workflow) // LOCALCI-3 follow-up
    {
        // A docs-only pull request that touched tests/E2E.Tests/README.md billed the FULL run — build,
        // test, e2e, docker, and both native builds — because the classifier's regexes key on the
        // directory (`^tests/`, `tests/E2E\.Tests/`) and a README lives in one. A markdown file cannot
        // change what the code does or how it builds, wherever it sits. This models the classifier
        // script faithfully: the same regexes, applied after the same markdown exclusion, so the
        // assertion cannot pass while the workflow still bills for a README.
        var (Code, Native, Docs) = ClassifierOf(workflow);

        // Markdown, wherever it lives, is neither — whatever the case of its extension (v4 LB-DEP-10).
        Assert.False(Code("tests/Ui.Tests/README.md"), "a README under tests/ must not count as code");
        Assert.False(Native("tests/E2E.Tests/README.md"), "a README under tests/E2E.Tests/ must not trigger the native legs");
        // ...unless a test reads it: LocalPortsTests checks the E2E README's port table, so editing it runs the tests
        // (testdocs), but never the native legs, whose list gets no markdown back.
        Assert.True(Code("tests/E2E.Tests/README.md"), "the E2E README is read by LocalPortsTests, so it is code");
        Assert.False(Code("src/Api/Features/Notes/README.md"), "a README under src/ must not count as code");
        Assert.False(Code("src/Api/Features/Notes/README.MD"), "the markdown strip is case-insensitive");
        Assert.False(Code("docs/ROADMAP.md"), "a doc no test reads stays free");

        // And the exclusion must not have eaten anything real.
        Assert.True(Code("tests/Api.Tests/EnforcementGateTests.cs"));
        Assert.True(Code("src/Api/Program.cs"));
        Assert.True(Native("src/Api/Program.cs"));
        Assert.True(Native("tests/E2E.Tests/BillingJourneyTests.cs"));
        Assert.True(Code(workflow), "an edit to the workflow itself can break a build no source file touched");
        Assert.True(Code("src/Api/packages.lock.json"));
        Assert.True(Docs("docs/QA_TEST_PLAN.md"), "docs= is computed on the UNFILTERED list, so markdown under docs/ still counts as docs");
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".forgejo/workflows/ci.yml")]
    public void Classifier_CountsEveryFileAGateReads_AsCode(string workflow) // v4 DEP-15 (R97)
    {
        // One positive per file class the old regex missed: a PR touching only one of these skipped build-test (and
        // the test guarding that very file), license-scan and docker-build, and merged green.
        var (Code, _, _) = ClassifierOf(workflow);
        foreach (var path in new[]
                 {
                     ".forgejo/scripts/push-to-github.sh", ".forgejo/workflows/deploy.yml", ".forgejo/workflows/postman-sync.yml",
                     ".github/workflows/postman-sync.yml", ".github/forbidden-licenses.json", ".dockerignore", "docs/DEPLOYMENT.md",
                     ".env.example", "tools/protect-branches.ps1", "Vuelto.slnx", "tools/e2e.ps1", "docs/postman/Vuelto.postman_collection.json",
                 })
            Assert.True(Code(path), $"{workflow}: {path} is read by a gate, so a change to it must run the gates");
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".forgejo/workflows/ci.yml")]
    public void NativeClassifier_CoversEveryNativeInput(string workflow) // v4 NAT-16 / S0-G6 (R106)
    {
        var (_, Native, _) = ClassifierOf(workflow);
        foreach (var path in new[] { "Directory.Build.props", "Directory.Packages.props", "global.json", "tools/publish-native.ps1",
                                     "src/Maui/MauiProgram.cs", ".github/workflows/ci.yml", ".forgejo/workflows/ci.yml" })
            Assert.True(Native(path), $"{workflow}: {path} can break a native build, so it must run the native legs");
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".forgejo/workflows/ci.yml")]
    public void Classifier_FailsOpen_OnEveryOutput(string workflow) // v4 LB-DEP-9 (R143)
    {
        // The unknown-diff-base branch exists to run everything; an output it leaves false is a job it quietly skips.
        var ci = File.ReadAllText(Path.Combine(RepoRoot(), workflow));
        // The branch that writes the outputs when the base is unknown: from its `if` to its `exit 0`.
        var at = ci.IndexOf("echo \"code=true\"", StringComparison.Ordinal);
        Assert.True(at > 0, "no fail-open branch writing code=true");
        var from = ci.LastIndexOf("if [ -z \"$BASE_SHA\" ]", at, StringComparison.Ordinal);
        var failOpen = ci[from..ci.IndexOf("exit 0", at, StringComparison.Ordinal)];
        foreach (var output in new[] { "code", "native", "docs" })
            Assert.Contains($"echo \"{output}=true\"", failOpen);
        Assert.DoesNotContain("=false\"", failOpen);
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".forgejo/workflows/ci.yml")]
    public void EveryRepoFileTheTestsRead_ClassifiesAsCode(string workflow) // v4 DEP-15 / TB-DOC-7 (R97)
    {
        // Reflective: every string literal (and every Path.Combine join) in this test project that names an existing
        // repo FILE is something a test reads, so a change to it must run the tests. Directories are not asserted:
        // src/, tests/ and tools/ are code wholesale, and the one scan of a docs folder (the CLAUDE.md doc map lists
        // every top-level docs/*.md) is the named exception — only an ADDED top-level doc can fail it, and counting
        // every doc edit as code to catch that would bill the full run for every docs PR; the next code run catches it.
        var (Code, _, _) = ClassifierOf(workflow);
        var notCode = TestReadFiles().Where(p => !Code(p)).OrderBy(p => p).ToList();
        Assert.True(notCode.Count == 0,
            $"{workflow}: these files are read by tests but classify as not-code, so a PR touching only them skips the "
            + $"tests that guard them. Add them to code= (and, if markdown, to testdocs=) in BOTH workflows: {string.Join(", ", notCode)}");
    }

    [Fact]
    public void JobTests_UseTheRealOutboxEmailSender() // R133 (v4 T43)
    {
        // A scheduled job that notifies runs the real OutboxEmailSender in its tests: that sender flushes the
        // context mid-way (the email must be durable before the request returns), which is the one write a
        // no-op sender hides — and hiding it is how the lapse sweep's "notification + stamp commit together"
        // stayed a comment for two audits. No *JobTests.cs may substitute the sender.
        var jobTests = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "tests", "Api.Tests"), "*JobTests.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.Contains(jobTests, f => File.ReadAllText(f).Contains("new OutboxEmailSender(")); // probe alive: the lapse sweep

        var offenders = jobTests.Where(f => File.ReadAllText(f).Contains("NoopEmailSender")).Select(Path.GetFileName).ToList();
        Assert.True(offenders.Count == 0,
            $"Job tests run the real OutboxEmailSender (its mid-way SaveChanges is what the transaction has to cover): {string.Join(", ", offenders)}");
    }

    // ── The E2E boot helper (v4 T55, R109) ──

    [Fact]
    public void E2eNavigations_GoThroughBlazorBoot()
    {
        // A retry of a dead boot must RELOAD the landed page, never repeat the navigation (a single-use
        // magic-link URL was spent twice — UX-8), and only BlazorBoot knows how. So no journey or page object
        // navigates or reloads an IPage itself: BlazorBoot.cs is the one caller (and BlazorBootCore.cs, whose page
        // is its own IBootPage seam). The Windows native smoke drives a WebView2 through CDP with no Blazor loader
        // to watch, and is the one deliberate exception.
        var e2e = Path.Combine(RepoRoot(), "tests", "E2E.Tests");
        var receiver = new System.Text.RegularExpressions.Regex(@"\b\w*[pP]age\.(GotoAsync|ReloadAsync)\(");
        var offenders = Directory.EnumerateFiles(e2e, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => Path.GetFileName(f) is not ("BlazorBoot.cs" or "BlazorBootCore.cs" or "NativeSmokeTests.cs"))
            .Where(f => receiver.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(e2e, f))
            .ToList();
        Assert.True(offenders.Count == 0, $"IPage.GotoAsync/ReloadAsync outside BlazorBoot.cs (use BlazorBoot.GotoAsync / ReloadAsync): {string.Join(", ", offenders)}");
        Assert.Matches(receiver, File.ReadAllText(Path.Combine(e2e, "BlazorBoot.cs"))); // probe alive
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".forgejo/workflows/ci.yml")]
    public void BootRetryBudget_IsOneNumber_InTheSuiteAndTheSlowestJourneysStep(string workflow)
    {
        // The suite stops at DeadBootsPerRun dead boots per process; the "Slowest journeys" step reads the same
        // number from the shard's .trx and fails past it, so a shard that retried its way to green is red anyway.
        var ci = File.ReadAllText(Path.Combine(RepoRoot(), workflow));
        var step = ci[ci.IndexOf("- name: Slowest journeys", StringComparison.Ordinal)..];
        step = step[..step.IndexOf("- name: ", 10, StringComparison.Ordinal)];
        Assert.Contains($"budget = {Vuelto.E2E.Tests.BlazorBootCore.DeadBootsPerRun}  #", step);
        Assert.Contains("if len(boots) > budget:", step);
        Assert.Contains("raise SystemExit(1)", step);
        Assert.Contains("sorted({line.strip()", step); // per journey, deduplicated (LB-DEP-6b)
    }

    // ── ReleaseGuards.targets, probed without the MAUI workloads (v4 T50, R103/R141) ──
    // A bare MSBuild project imports the file and runs its two targets with a table of inputs. Every MAUI
    // build in CI is Debug, so nothing else exercises these guards before someone sideloads a phone.

    public static TheoryData<string, string, string, bool, string?> ReleaseGuardCases => new()
    {
        // configuration, tfm, ApiBaseUrl, keystore present, expected error fragment (null = passes)
        { "Release", "net10.0-android", "https://api.example.com", true, null },
        { "Release", "net10.0-android", "https://api.example.com:8443", true, null },
        { "Release", "net10.0-android", "  https://api.example.com  ", true, null },              // trimmed
        { "Staging", "net10.0-android", "https://api.example.com", true, null },                  // any non-Debug configuration
        { "Debug", "net10.0-android", "", false, null },                                           // guards inactive
        { "Release", "net10.0-windows10.0.19041.0", "https://api.example.com", false, null },     // keystore rule is Android's
        { "Release", "net10.0-android", "", true, "require -p:ApiBaseUrl" },
        { "Release", "net10.0-android", "   ", true, "require -p:ApiBaseUrl" },
        { "Staging", "net10.0-android", "", true, "require -p:ApiBaseUrl" },                      // the gap: -c Staging used to pass
        { "Release", "net10.0-android", "http://api.example.com", true, "https:// ORIGIN" },
        { "Release", "net10.0-android", "https://api.example.com/", true, "https:// ORIGIN" },
        { "Release", "net10.0-android", "https://api.example.com/api", true, "https:// ORIGIN" },
        { "Release", "net10.0-android", "https://api.example.com?x=1", true, "https:// ORIGIN" },
        { "Release", "net10.0-android", "example.com", true, "https:// ORIGIN" },
        { "Release", "net10.0-android", "https://api.example.com", false, "No keystore" },
    };

    [Theory]
    [MemberData(nameof(ReleaseGuardCases))]
    public async Task ReleaseGuards_RefuseBadInputs_AndPassGoodOnes(string configuration, string tfm, string apiBaseUrl, bool keystore, string? expectedError)
    {
        var dir = Directory.CreateTempSubdirectory("release-guards-");
        try
        {
            var keystorePath = Path.Combine(dir.FullName, "debug.keystore");
            if (keystore) File.WriteAllText(keystorePath, "not-a-real-keystore");
            var targets = Path.Combine(RepoRoot(), "src", "Maui", "ReleaseGuards.targets");
            File.WriteAllText(Path.Combine(dir.FullName, "probe.proj"), $"""
                <Project>
                  <PropertyGroup><TargetFramework>{tfm}</TargetFramework></PropertyGroup>
                  <Import Project="{targets}" />
                  <Target Name="Probe" DependsOnTargets="ValidateReleaseApiBaseUrl;ResolveReleaseKeystore" />
                </Project>
                """);

            var run = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                WorkingDirectory = dir.FullName, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var arg in new[] { "msbuild", "probe.proj", "-t:Probe", "-nologo", "-v:m",
                         $"-p:Configuration={configuration}", $"-p:ApiBaseUrl={apiBaseUrl}", $"-p:ReleaseGuardsKeystoreCandidates={keystorePath}" })
                run.ArgumentList.Add(arg);
            using var p = System.Diagnostics.Process.Start(run)!;
            var output = await p.StandardOutput.ReadToEndAsync() + await p.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await p.WaitForExitAsync(timeout.Token);

            if (expectedError is null)
                Assert.True(p.ExitCode == 0, $"expected the guards to pass:\n{output}");
            else
            {
                Assert.True(p.ExitCode != 0, $"expected the guards to refuse ({expectedError}):\n{output}");
                Assert.Contains(expectedError, output);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReleaseGuards_LiveInOneFile_UnderOneCondition()
    {
        // The csproj keeps none of the Release logic: three checks under three conditions is how `-c Staging`
        // got the HTTPS-only config but no ApiBaseUrl check.
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Maui", "Vuelto.Maui.csproj"));
        Assert.Contains("<Import Project=\"ReleaseGuards.targets\" />", csproj);
        Assert.DoesNotContain("'$(Configuration)' == 'Release'", csproj);
        Assert.DoesNotContain("$(ApiBaseUrl)", csproj);

        var targets = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Maui", "ReleaseGuards.targets"));
        Assert.DoesNotContain("== 'Release'", targets); // != 'Debug', once
        Assert.Single(Regex.Matches(targets, @"'\$\(Configuration\)' != 'Debug'"));
    }

    [Fact]
    public void PublishScript_ThrowsUnlessTheSignatureIsVerified_AndFindsTheSdkThroughAndroidHome()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "publish-native.ps1"));
        Assert.Contains("$env:ANDROID_HOME", script);
        Assert.Contains("throw \"APK signature NOT verified", script);
        // "Send THIS file" is printed only after the throw-unless-verified line, never before it.
        Assert.True(script.IndexOf("APK signature NOT verified", StringComparison.Ordinal) < script.IndexOf("Send THIS file", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".forgejo/workflows/ci.yml")]
    public void ReleaseLeg_BuildsRelease_VerifiesV2OrV3_AndProvesTheGuardFires(string workflow)
    {
        // The only MAUI Release build CI runs: on demand and on the Monday schedule (never per push).
        var ci = File.ReadAllText(Path.Combine(RepoRoot(), workflow));
        var job = ci[ci.IndexOf("\n  native-release-android:", StringComparison.Ordinal)..];
        job = job[..(job.IndexOf("\n  # ", StringComparison.Ordinal) is var n and > 0 ? n : job.Length)];
        Assert.Contains("-c Release", job);
        Assert.Contains("apksigner", job);
        Assert.Contains("Verified using v[23]", job);
        Assert.Contains("ApiBaseUrl-less Release build must fail", job);
        Assert.DoesNotContain("github.event_name == 'push'", job);
    }

    private static IEnumerable<string> TestReadFiles()
    {
        var root = RepoRoot();
        var literal = new Regex(@"""((?:[^""\\]|\\.)*)""");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "tests", "Api.Tests"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                              && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            // Paths handed to the classifier model (Code("…"), Native("…"), Docs("…")) are examples, not reads.
            var text = Regex.Replace(File.ReadAllText(file), @"\b(?:Code|Native|Docs)\(""[^""]*""\)", "");
            var candidates = literal.Matches(text).Select(m => m.Groups[1].Value).ToList();
            foreach (Match call in Regex.Matches(text, @"Path\.Combine\("))
            {
                var end = text.IndexOf(';', call.Index);
                var parts = literal.Matches(text[call.Index..(end < 0 ? text.Length : end)]).Select(m => m.Groups[1].Value).ToList();
                for (var i = 1; i <= parts.Count; i++) candidates.Add(string.Join('/', parts.Take(i)));
            }
            foreach (var c in candidates.Select(c => c.Replace(@"\\", "/").Replace('\\', '/')))
                if (c.Length > 0 && !c.StartsWith('/') && !c.Contains(' ') && c != ".env" // .env is the developer's, gitignored
                    && File.Exists(Path.Combine(root, c)))
                    found.Add(c);
        }
        Assert.Contains("docs/DEPLOYMENT.md", found); // probe alive
        return found;
    }

    // The changes step, modelled faithfully: markdown (any case) is stripped before the code/native match; the
    // markdown files tests read (testdocs=) are added back for code= only; docs= sees the unfiltered list.
    private static (Func<string, bool> Code, Func<string, bool> Native, Func<string, bool> Docs) ClassifierOf(string workflow)
    {
        var ci = File.ReadAllText(Path.Combine(RepoRoot(), workflow));
        static string Extract(string ci, string name)
        {
            var m = Regex.Match(ci, name + @"=\$\(match (?:""\$\w+"" )?'([^']+)'\)");
            Assert.True(m.Success, $"could not find the `{name}=` regex in the changes step of ci.yml");
            return m.Groups[1].Value;
        }
        var code = new Regex(Extract(ci, "code"));
        var native = new Regex(Extract(ci, "native"));
        var docs = new Regex(Extract(ci, "docs"));

        var strip = Regex.Match(ci, @"grep -viE '([^']+)'");
        Assert.True(strip.Success, "the markdown strip must be case-insensitive (grep -viE)");
        var stripped = new Regex(strip.Groups[1].Value, RegexOptions.IgnoreCase);
        var testdocsDecl = Regex.Match(ci, @"testdocs='([^']+)'");
        Assert.True(testdocsDecl.Success, "the changes step must declare testdocs='…', the markdown files tests read");
        var testdocs = new Regex(testdocsDecl.Groups[1].Value);

        Assert.Contains(@"native=$(match ""$nativefiles""", ci);
        return (p => (!stripped.IsMatch(p) || testdocs.IsMatch(p)) && code.IsMatch(p),
                p => !stripped.IsMatch(p) && native.IsMatch(p),
                p => docs.IsMatch(p));
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".forgejo/workflows/ci.yml")]
    public void TheTwoGatesThatCatchDocsMistakes_AreNeverCodeGated(string workflow) // LOCALCI-3
    {
        // Stated separately from the test above because it is the opposite failure: not "someone
        // forgot the gate" but "someone added it where it does harm". A docs-only change is exactly
        // when these two matter, so gating them on code would blind CI to the one class of mistake
        // a docs commit can make.
        var ci = File.ReadAllText(Path.Combine(RepoRoot(), workflow));

        foreach (var job in new[] { "secret-scan", "qa-artifacts" })
        {
            var block = Regex.Match(ci, $@"(?ms)^  {Regex.Escape(job)}:\s*$.*?(?=^  [a-z][a-z0-9-]*:\s*$)");
            Assert.True(block.Success, $"{job} not found in {workflow}");
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

    [Fact]
    public void DeployTriggerWording_AutoDeploysNamesTheForge() // v4 audit TR-13 (T60), R117
    {
        // Under ADR-028 nothing auto-deploys from this repo: staging deploys when someone runs the Forgejo
        // workflow with deploy=staging (or on the Monday schedule). "Merge to develop auto-deploys staging" was
        // true on GitHub and survived in nine docs after the move, so a tester waited for a staging build that
        // never came. Any mention of auto-deploying must say WHOSE trigger it is — GitHub's pipeline, Forgejo's
        // dispatch, or Render's own Auto-Deploy setting — on the same line, so the sentence cannot read as this
        // repo's behaviour. The audit folder is history and exempt.
        var root = RepoRoot();
        var mention = new System.Text.RegularExpressions.Regex(@"\bauto-?deploy", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var qualified = new System.Text.RegularExpressions.Regex(@"GitHub|Forgejo|Render|ADR-028|autoDeploy|Auto-Deploy");
        var files = Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}audits{Path.DirectorySeparatorChar}"))
            .Append(Path.Combine(root, "CLAUDE.md"))
            .Append(Path.Combine(root, "README.md"));
        var offenders = new List<string>();
        var mentions = 0;
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!mention.IsMatch(lines[i])) continue;
                mentions++;
                if (!qualified.IsMatch(lines[i]))
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(mentions > 0, "probe: DEPLOYMENT.md still mentions Render's Auto-Deploy setting");
        Assert.True(offenders.Count == 0,
            "An auto-deploy that does not say whose (GitHub pipeline / Forgejo dispatch / Render setting) reads as this repo's — it is not (ADR-028):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void DeployTriggerWording_PostmanSyncNamesTheForge() // v4 audit TR-14 (H9), R117
    {
        // Forgejo is the primary forge (ADR-028): develop moves there on every merge, and GitHub's develop moves
        // only when a deploy pushes it. So the postman-sync that runs per merge is the .forgejo copy, reading a
        // secret and a variable set in Forgejo. The README told operators to set them on GitHub only, which leaves
        // the per-merge sync skipping with a notice forever while the docs say it is set up.
        var root = RepoRoot();
        var readme = File.ReadAllText(Path.Combine(root, "docs", "postman", "README.md")).ReplaceLineEndings("\n");
        var start = readme.IndexOf("**One-time setup**", StringComparison.Ordinal);
        Assert.True(start >= 0, "docs/postman/README.md lost its One-time setup section");
        var setup = readme[start..readme.IndexOf("**Direction is one-way.**", start, StringComparison.Ordinal)];
        var forgejo = setup.IndexOf("Forgejo", StringComparison.Ordinal);
        Assert.True(forgejo >= 0, "the Postman one-time setup must say where the Forgejo secret and variable go");
        var github = setup.IndexOf("GitHub", StringComparison.Ordinal);
        Assert.True(github < 0 || forgejo < github, "the Postman one-time setup must lead with Forgejo, the primary forge");

        // The operating manual names the forge whose develop changes drive the sync.
        Assert.Contains("on every Forgejo `develop` change", File.ReadAllText(Path.Combine(root, "CLAUDE.md")).ReplaceLineEndings(" "),
            StringComparison.Ordinal);

        // Both workflow copies hardcode the collection path, so a rebrand that renames the files edits both.
        var rebranding = File.ReadAllText(Path.Combine(root, "docs", "REBRANDING.md"));
        Assert.Contains(".github/workflows/postman-sync.yml", rebranding, StringComparison.Ordinal);
        Assert.Contains(".forgejo/workflows/postman-sync.yml", rebranding, StringComparison.Ordinal);
    }

    [Fact]
    public void LogTemplates_NeverCarryAnEmailAddress() // v4 audit OBS-1 (T41), R93
    {
        // Logs leave the machine: with ParseStateValues every template placeholder is exported as an indexed
        // attribute, and staging ships its logs to a third-party store. An email in a template (or interpolated
        // into one) is personal data sent there on every call, so the log names the user by id instead.
        var root = RepoRoot();
        var logCall = new Regex(@"\.Log(?:Trace|Debug|Information|Warning|Error|Critical)\s*\(|\[LoggerMessage\s*\(");
        var literal = new Regex(@"(\$?)@?""((?:[^""\\]|\\.)*)""");
        var piiPlaceholder = new Regex(@"\{\s*(?:\w*e-?mail\w*|recipient\w*)\s*(?:[,:][^}]*)?\}", RegexOptions.IgnoreCase);
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var text = File.ReadAllText(file).ReplaceLineEndings("\n");
            foreach (Match call in logCall.Matches(text))
            {
                var end = text.IndexOf(';', call.Index);
                var args = text[call.Index..(end < 0 ? text.Length : end)];
                foreach (Match lit in literal.Matches(args))
                {
                    if (!piiPlaceholder.IsMatch(lit.Groups[2].Value)) continue;
                    var line = text[..call.Index].Count(c => c == '\n') + 1;
                    offenders.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{line}  \"{lit.Groups[2].Value}\"");
                }
            }
        }
        Assert.True(offenders.Count == 0,
            "Log templates carry an email address; log {UserId} (or another id) instead:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public async Task CiShellLogic_PassesItsFixtures() // v4 T8 (R136)
    {
        // CI's own pass/fail logic (the change classifier, the QA run-log guard, the e2e sharding, the slowest-journeys
        // report, the mirror push) is shell, awk and Python that no C# test executes. tests/ci-logic/ runs each of
        // them for real against fixtures. It needs the GNU tools the runners have, so it runs on Linux only — and the
        // build-test job runs on Linux on both forges, so it is never skipped where it counts.
        if (!OperatingSystem.IsLinux()) return;

        var run = new System.Diagnostics.ProcessStartInfo("bash", "tests/ci-logic/run.sh")
        {
            WorkingDirectory = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = System.Diagnostics.Process.Start(run)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await p.WaitForExitAsync(timeout.Token);
        Assert.True(p.ExitCode == 0, $"tests/ci-logic failed:\n{await stdout}\n{await stderr}");
    }

    [Fact]
    public void CiShellLogic_EveryAnchoredBlockIsATarget_WithCases() // v4 T8 (R136)
    {
        // Runs everywhere (no bash needed): an anchored block nobody runs, a target whose anchor is gone, or a target
        // with no cases would each let the harness pass while testing nothing.
        var root = RepoRoot();
        var targets = File.ReadAllLines(Path.Combine(root, "tests", "ci-logic", "targets"))
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("block ", StringComparison.Ordinal) || l.StartsWith("script ", StringComparison.Ordinal))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        Assert.NotEmpty(targets);

        var anchored = new[] { ".github", ".forgejo" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*", SearchOption.AllDirectories))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"# ci-logic begin: ([\w-]+)")
                .Select(m => (Block: m.Groups[1].Value, File: Path.GetRelativePath(root, f).Replace('\\', '/'))))
            .ToList();
        Assert.NotEmpty(anchored);

        foreach (var (block, file) in anchored)
            Assert.True(targets.Any(t => t[0] == "block" && t[1] == block && t.Skip(2).Contains(file)),
                $"{file} anchors `{block}` but tests/ci-logic/targets does not run it there");

        foreach (var t in targets)
        {
            var cases = Path.Combine(root, "tests", "ci-logic", "cases", t[1]);
            Assert.True(Directory.Exists(cases) && Directory.EnumerateDirectories(cases).Any(), $"target `{t[1]}` has no cases");
            foreach (var file in t.Skip(2))
            {
                var text = File.ReadAllText(Path.Combine(root, file));
                Assert.True(t[0] == "script" || (text.Contains($"# ci-logic begin: {t[1]}") && text.Contains($"# ci-logic end: {t[1]}")),
                    $"target `{t[1]}` expects its anchors in {file}");
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root.");
    }
}
