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

        // v4 T67: the prose that states the pin (the bump-together playbook's step ④) agrees with it. A doc that
        // names an SDK or ASP.NET version names THE one; the Dockerfile tags quoted in prose are the real tags.
        foreach (var doc in new[] { "CLAUDE.md", Path.Combine("docs", "TECH_STACK.md"), Path.Combine("docs", "DEPLOYMENT.md") })
        {
            var text = File.ReadAllText(Path.Combine(root, doc));
            foreach (Match m in Regex.Matches(text, @"(?:\.NET SDK|SDK) \*{0,2}(10\.0\.\d{3})"))
                Assert.True(m.Groups[1].Value == sdk, $"{doc} says SDK {m.Groups[1].Value}; global.json pins {sdk} (playbook step ④)");
            foreach (Match m in Regex.Matches(text, @"sdk:(10\.0\.\d{3})"))
                Assert.True(m.Groups[1].Value == sdk, $"{doc} quotes sdk:{m.Groups[1].Value}; the Dockerfile builds on sdk:{sdk}");
            foreach (Match m in Regex.Matches(text, @"aspnet:(10\.0\.\d+)"))
                Assert.True(m.Groups[1].Value == runtimeTag, $"{doc} quotes aspnet:{m.Groups[1].Value}; the Dockerfile runs on aspnet:{runtimeTag}");
        }
    }

    [Fact]
    public void Dockerfile_GivesTheAppUserAWritableStorageDir()
    {
        // Local-disk file storage (ADR-010) defaults to ./storage under the app base dir = /app/storage. The runtime
        // runs as the non-root `app` user, and WORKDIR /app is created by root, so without this the first file write
        // (CSV export, report PDF, household export) fails with "Access to the path '/app/storage' is denied" —
        // found on a downstream app's staging, 2026-09-17. The directory must exist, owned by `app`, BEFORE `USER app`.
        var dockerfile = File.ReadAllText(Path.Combine(RepoRoot(), "Dockerfile"));
        var runtime = dockerfile[dockerfile.IndexOf("AS runtime", StringComparison.Ordinal)..];
        var userAt = runtime.IndexOf("USER app", StringComparison.Ordinal);
        Assert.True(userAt > 0, "The runtime stage must switch to the non-root `app` user.");

        var beforeUser = runtime[..userAt];
        Assert.Matches(@"mkdir -p [^\n]*/app/storage", beforeUser);
        Assert.Matches(@"chown [^\n]*app:app [^\n]*/app/storage", beforeUser);

        // CI proves it on the built image.
        Assert.Contains("/app/storage/.write-probe", File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "ci.yml")));
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

        // v4 T67: the ORDER too — theme.js must run before the framework script in both hosts, and a script that
        // depends on another cannot be ahead of it in one host only — and the two vendored wwwroot/lib trees
        // (Bootstrap) are byte-identical: a bump applied to one host ships two Bootstraps.
        static List<string> ScriptOrder(string path) =>
            [.. Regex.Matches(File.ReadAllText(path), @"<script[^>]+src=""([^""]+)""").Select(m => m.Groups[1].Value)];
        var webOrder = ScriptOrder(Path.Combine(root, "src", "Web", "wwwroot", "index.html"));
        var mauiOrder = ScriptOrder(Path.Combine(root, "src", "Maui", "wwwroot", "index.html"));
        var shared = webOrder.Where(mauiOrder.Contains).ToList();
        Assert.True(shared.SequenceEqual(mauiOrder.Where(webOrder.Contains)),
            $"The two hosts load their shared scripts in a different order — web: [{string.Join(", ", shared)}], maui: [{string.Join(", ", mauiOrder.Where(webOrder.Contains))}]");

        static Dictionary<string, string> Tree(string dir) => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/'), f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));
        var webLib = Tree(Path.Combine(root, "src", "Web", "wwwroot", "lib"));
        var mauiLib = Tree(Path.Combine(root, "src", "Maui", "wwwroot", "lib"));
        Assert.NotEmpty(webLib); // probe alive
        var libDiff = webLib.Keys.Union(mauiLib.Keys).Where(k => !webLib.TryGetValue(k, out var a) || !mauiLib.TryGetValue(k, out var b) || a != b).ToList();
        Assert.True(libDiff.Count == 0, "src/Web/wwwroot/lib and src/Maui/wwwroot/lib differ — vendor the same files into both: " + string.Join(", ", libDiff));
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
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
        //   deploy-*     — they gate transitively, through the jobs they need (ADR-031: dispatch only).
        // (Since ADR-031 secret-scan and qa-artifacts do read `changes` — its run plan — but never its `code`.)
        string[] alwaysRun = ["changes", "secret-scan", "qa-artifacts", "deploy-staging", "deploy-prod"];

        var ci = File.ReadAllLines(Path.Combine(RepoRoot(), workflow));

        // Job blocks are the two-space keys under `jobs:`; a block runs to the next such key. Start
        // AFTER `jobs:` — the trigger list above it uses the same indentation, so `pull_request` and
        // `workflow_dispatch` otherwise read as jobs with no gate.
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
    public void Classifier_CountsEveryFileAGateReads_AsCode(string workflow) // v4 DEP-15 (R97)
    {
        // One positive per file class the old regex missed: a PR touching only one of these skipped build-test (and
        // the test guarding that very file), license-scan and docker-build, and merged green.
        var (Code, _, _) = ClassifierOf(workflow);
        foreach (var path in new[]
                 {
                     ".github/workflows/postman-sync.yml", ".github/scripts/deploy-smoke.sh", ".github/forbidden-licenses.json", ".dockerignore",
                     "docs/DEPLOYMENT.md", ".env.example", "Vuelto.slnx", "tools/e2e.ps1", "docs/postman/Vuelto.postman_collection.json",
                     "docs/ARCHITECTURE.md", "docs/FLOWS.md", // R121: the diagram-currency gate reads both
                 })
            Assert.True(Code(path), $"{workflow}: {path} is read by a gate, so a change to it must run the gates");
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    public void NativeClassifier_CoversEveryNativeInput(string workflow) // v4 NAT-16 / S0-G6 (R106)
    {
        var (_, Native, _) = ClassifierOf(workflow);
        foreach (var path in new[] { "Directory.Build.props", "Directory.Packages.props", "global.json", "tools/publish-native.ps1",
                                     "src/Maui/MauiProgram.cs", ".github/workflows/ci.yml" })
            Assert.True(Native(path), $"{workflow}: {path} can break a native build, so it must run the native legs");
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    public void Classifier_FailsOpen_OnEveryOutput(string workflow) // v4 LB-DEP-9 (R143)
    {
        // The unknown-diff-base branch exists to run everything; an output it leaves false is a job it quietly skips.
        var ci = File.ReadAllText(Path.Combine(RepoRoot(), workflow));
        // The branch that writes the outputs when the base is unknown: from its `if` to its `exit 0`.
        var at = ci.IndexOf("echo \"code=true\"", StringComparison.Ordinal);
        Assert.True(at > 0, "no fail-open branch writing code=true");
        var from = ci.LastIndexOf("if [ -z \"$BASE_SHA\" ]", at, StringComparison.Ordinal);
        var failOpen = ci[from..ci.IndexOf("exit 0", at, StringComparison.Ordinal)];
        foreach (var output in new[] { "code", "native", "maui", "docs" })
            Assert.Contains($"echo \"{output}=true\"", failOpen);
        Assert.DoesNotContain("=false\"", failOpen);
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    public void EveryRepoFileTheTestsRead_ClassifiesAsCode(string workflow) // v4 DEP-15 / TB-DOC-7 (R97)
    {
        // Reflective: every string literal (and every Path.Combine join) in this test project that names an existing
        // repo FILE is something a test reads, so a change to it must run the tests. Directories are not asserted:
        // src/, tests/ and tools/ (less the editor tooling in devtools=) are code wholesale, and the one scan of a docs folder (the CLAUDE.md doc map lists
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
    public void ReleaseLeg_BuildsRelease_VerifiesV2OrV3_AndProvesTheGuardFires(string workflow)
    {
        // The only MAUI Release build CI runs: a device leg, only when a "Run workflow" asks for android (ADR-031).
        var ci = File.ReadAllText(Path.Combine(RepoRoot(), workflow));
        var job = ci[ci.IndexOf("\n  native-release-android:", StringComparison.Ordinal)..];
        job = job[..(job.IndexOf("\n  # ", StringComparison.Ordinal) is var n and > 0 ? n : job.Length)];
        Assert.Contains("-c Release", job);
        Assert.Contains("apksigner", job);
        Assert.Contains("Verified using v[23]", job);
        Assert.Contains("ApiBaseUrl-less Release build must fail", job);
        Assert.DoesNotContain("github.event_name == 'push'", job);
        Assert.Contains("if: needs.changes.outputs.android == 'true'", job);
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
                    && !c.Split('/').Contains("..") // a traversal payload ("../../etc/passwd") names nothing in the repo, wherever it is checked out
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

        var devtoolsDecl = Regex.Match(ci, @"devtools='([^']+)'");
        Assert.True(devtoolsDecl.Success, "the changes step must declare devtools='…', the editor tooling that is not code (Env L22)");
        var devtools = new Regex(devtoolsDecl.Groups[1].Value);

        Assert.Contains(@"native=$(match ""$nativefiles""", ci);
        return (p => !devtools.IsMatch(p) && (!stripped.IsMatch(p) || testdocs.IsMatch(p)) && code.IsMatch(p),
                p => !devtools.IsMatch(p) && !stripped.IsMatch(p) && native.IsMatch(p),
                p => docs.IsMatch(p));
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
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
            Assert.DoesNotContain("needs.changes.outputs.web", block.Value, StringComparison.Ordinal);
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
        // the surfaces the hand-applied convention missed: test fixtures and compose — and, since v4 T16
        // (DEP-18), the workflow's `services:` images and `docker run` lines and the Dockerfile's FROM lines,
        // the four places an image is named. A bare major tag (`postgres:17`) counts as floating too: it moves
        // with every minor release, so a new Postgres reaches CI and the harness with no change in the repo.
        var root = RepoRoot();
        var offenders = new List<string>();

        // Workflow services: `image: repo/name:tag`, and `docker run … <image>` steps (the image is the first
        // bare word after the options; `--entrypoint sh` takes a value, so skip an option's argument too).
        var workflow = Path.Combine(root, ".github", "workflows", "ci.yml");
        var workflowText = File.ReadAllText(workflow);
        foreach (Match m in new Regex(@"(?m)^\s*image:\s*([^\s#]+)").Matches(workflowText))
            Check(m.Groups[1].Value, "ci.yml services");
        foreach (Match m in new Regex(@"docker run\s+(.*)").Matches(workflowText))
        {
            var words = m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < words.Length; i++)
            {
                if (words[i].StartsWith("--", StringComparison.Ordinal))
                {
                    if (!words[i].Contains('=') && words[i] is "--entrypoint" or "--name" or "--network" or "-e" or "--env" or "-p" or "--publish" or "-v" or "--volume" or "-w" or "--workdir") i++;
                    continue;
                }
                if (words[i].StartsWith('-')) { i++; continue; }
                Check(words[i], "ci.yml docker run");
                break;
            }
        }

        // Dockerfile stages: `FROM image:tag [AS name]`
        foreach (Match m in new Regex(@"(?m)^FROM\s+(?:--platform=\S+\s+)?(\S+)").Matches(File.ReadAllText(Path.Combine(root, "Dockerfile"))))
            Check(m.Groups[1].Value, "Dockerfile");

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
            else if (Regex.IsMatch(tag, @"^v?\d+$"))
                offenders.Add($"{where}: '{image}' is a bare major tag, which floats with every minor release — pin at least major.minor");
        }
    }

    [Fact]
    public void NoPulledImage_ComesFromDockerHub() // 2026-10-09: GitHub's runners hit Docker Hub's anonymous pull limit
    {
        // A hosted runner pulls anonymously, and Docker Hub caps anonymous pulls per IP — shared by every job on that
        // runner pool. On 2026-10-09 `docker pull postgres:17.11` answered `toomanyrequests` three times and the e2e
        // job died before checkout, with nothing in the change to blame. Every image a job PULLS therefore names a
        // registry that does not rate-limit it: Postgres from Amazon's mirror of the official images
        // (public.ecr.aws/docker/library), Mailpit from its own GitHub registry, MinIO from our copy on ghcr.io.
        // A Docker Hub reference is one with no registry host: `postgres:17.11`, `axllent/mailpit:v1`. Dockerfile
        // stages (mcr.microsoft.com, and their own stage names) and `docker run` of the image the job just built
        // are not pulls from a registry, so only the three pulled surfaces are read.
        var root = RepoRoot();
        var offenders = new List<string>();

        var image = new Regex(@"(?m)^\s*image:\s*([^\s#]+)");
        foreach (Match m in image.Matches(File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"))))
            Check(m.Groups[1].Value, "ci.yml services");
        var compose = Path.Combine(root, "docker-compose.yml");
        if (File.Exists(compose))
            foreach (Match m in image.Matches(File.ReadAllText(compose)))
                Check(m.Groups[1].Value, "docker-compose.yml");
        var builder = new Regex(@"new\s+\w*Builder\s*\(\s*""([^""]+)""");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || Path.GetFileName(file) == $"{nameof(EnforcementGateTests)}.cs") continue; // this file's comments name the shape
            foreach (Match m in builder.Matches(File.ReadAllText(file)))
                Check(m.Groups[1].Value, Path.GetFileName(file));
        }

        Assert.True(offenders.Count == 0,
            "Pull every image from a registry that does not rate-limit anonymous pulls — public.ecr.aws/docker/library/… "
            + $"for an official image, the project's own registry otherwise: {string.Join(", ", offenders)}");

        // A Dockerfile `# syntax=docker/dockerfile:…` line makes BuildKit fetch its frontend from Docker Hub before the
        // first FROM — the docker-build job died on Docker Hub's 504 that way (jigger-jot, 2026-10-09). The frontend
        // built into the runner's Docker reads this Dockerfile as it is, so there is no syntax line to pull.
        Assert.DoesNotMatch(@"(?im)^\s*#\s*syntax\s*=", File.ReadAllText(Path.Combine(root, "Dockerfile")));

        // Testcontainers' own Ryuk reaper is a Docker Hub image too; CI turns it off (a runner is thrown away anyway).
        var ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        Assert.Matches(@"TESTCONTAINERS_RYUK_DISABLED:\s*""true""", ci);

        // And the mirror has a limit of its own: sixty fixtures pulling at once answered `toomanyrequests: Rate
        // exceeded` (2026-10-09). The job pulls each test image once, before the tests, so the fixtures find it
        // locally; the images named here are the ones the fixtures use.
        foreach (var pulled in new[] { "public.ecr.aws/docker/library/postgres:17.11", "ghcr.io/argamboad/minio:RELEASE.2025-09-07T16-13-09Z" })
            Assert.Contains(pulled, ci.Split("- name: Test (Core + Api + Ui)")[0]);

        void Check(string reference, string where)
        {
            if (reference.Contains('$') || reference.Contains('{')) return;
            var first = reference.Split('/')[0];
            var namesARegistry = reference.Contains('/') && (first.Contains('.') || first.Contains(':') || first == "localhost");
            if (!namesARegistry || first is "docker.io" or "index.docker.io" or "registry-1.docker.io")
                offenders.Add($"{where}: '{reference}' comes from Docker Hub");
        }
    }

    [Fact]
    public void ImagePinGate_SeesAllFourSurfaces_AndRefusesABareMajor() // v4 T16: the gate's own reach, held
    {
        // The gate above reads the live files, so a passing run cannot show that it looks at every surface.
        // This holds the reach: the four places an image is named each have at least one image today, and the
        // tag rule refuses exactly the floating shapes.
        var root = RepoRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        Assert.Matches(@"(?m)^\s*image:\s*\S+", workflow);          // services: at least one
        Assert.Matches(@"docker run\s", workflow);                   // docker run: at least one
        Assert.Matches(@"(?m)^FROM\s+\S+", File.ReadAllText(Path.Combine(root, "Dockerfile")));
        Assert.Contains("new PostgreSqlBuilder(\"", File.ReadAllText(Path.Combine(root, "tests", "Api.Tests", "Infrastructure", "PostgresFixture.cs")));

        static bool Floats(string tag) => tag.Equals("latest", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(tag, @"^v?\d+$");
        Assert.True(Floats("latest"));
        Assert.True(Floats("17"));
        Assert.True(Floats("v1"));
        Assert.False(Floats("17.11"));
        Assert.False(Floats("v1.30.4"));
        Assert.False(Floats("10.0.401"));
        Assert.False(Floats("ci"));
    }

    [Fact]
    public void ClaudeMdDocMap_ListsEveryDoc() // R75 (doc-map half), widened to docs/** by R118
    {
        // The map is what makes a doc visible to every session (v3 TR-1). It used to check docs/*.md only, and
        // what sat one folder down went unmapped: a live story file, the QA run logs, the entire course (v4 TR-24).
        var root = RepoRoot();
        var claudeMd = File.ReadAllText(Path.Combine(root, "CLAUDE.md"));

        // Folders mapped as ONE row: their files come and go (a new run log, a new lesson) without a map edit.
        string[] folderRows = [.. new[] { "docs/tutorial/", "docs/qa-runs/", "docs/postman/" }.Where(row => Directory.Exists(Path.Combine(root, row)))]; // a downstream app has no course
        var missingRows = folderRows.Where(row => !claudeMd.Contains($"| `{row}` |", StringComparison.Ordinal)).ToList();
        Assert.True(missingRows.Count == 0, $"CLAUDE.md doc map has no row for: {string.Join(", ", missingRows)}");

        // Not mapped, on purpose: audit runs are reached through AUDIT_SUITE.md and the rules pointer; the
        // example epic is a template, named by WAYS_OF_WORKING.md.
        string[] unmapped = ["docs/audits/", "docs/stories/_EXAMPLE_"];

        var docs = Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToList();
        Assert.True(docs.Count(d => d.Count(c => c == '/') > 1) >= 20, "probe: the walk found too few docs below docs/ — did the tree move?");

        var missing = docs
            .Where(rel => !unmapped.Any(u => rel.StartsWith(u, StringComparison.Ordinal)))
            .Where(rel => !folderRows.Any(row => rel.StartsWith(row, StringComparison.Ordinal)))
            .Where(rel => !claudeMd.Contains(rel, StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0,
            "Docs missing from the CLAUDE.md doc map — add a row (or, for a folder whose files come and go, a "
            + $"folder row in this gate): {string.Join(", ", missing)}");
    }

    [Fact]
    public void ClaudeMd_CarriesTheCourseReconcileRule() // R118
    {
        // The course lives in perezosoft-platform; a downstream app has no docs/tutorial (its R114/R115/R118 say NotHere).
        if (!Directory.Exists(Path.Combine(RepoRoot(), "docs", "tutorial"))) return;
        // The habit that keeps the course true — a lesson is reconciled in the PR that changes the code it
        // quotes — lived only in the maintainer's memory, so a clone lost it.
        var claudeMd = File.ReadAllText(Path.Combine(RepoRoot(), "CLAUDE.md")).ReplaceLineEndings("\n");
        var start = claudeMd.IndexOf("## Read before you act", StringComparison.Ordinal);
        Assert.True(start >= 0, "CLAUDE.md no longer has a 'Read before you act' section");
        var section = claudeMd[start..claudeMd.IndexOf("\n## ", start + 1, StringComparison.Ordinal)];
        Assert.True(section.Contains("docs/tutorial/", StringComparison.Ordinal) && section.Contains("--check-quotes", StringComparison.Ordinal),
            "CLAUDE.md 'Read before you act' must carry the course-reconcile rule: name docs/tutorial/ and the "
            + "quote check (gen_coverage.py --check-quotes).");
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
    public void EveryMachineRule_NamesAStandingCheck() // v4 audit T67, R116
    {
        // A rule labelled [machine] that nothing enforces is a rule on paper; the audit found v3 fixes marked done
        // that never landed. RulesEnforcement.Manifest names, per machine rule, the test or CI step that holds it,
        // or the tracker issue that still owes it. This gate holds the manifest to the rules file (every machine
        // rule listed, nothing listed that is not a machine rule) and to the tree (every named check exists).
        var root = RepoRoot();
        var rules = File.ReadAllText(Path.Combine(root, "docs", "audits", "v4-2026-09", "FOUNDATION_RULES_v3.md")).ReplaceLineEndings("\n");
        var machine = Regex.Matches(rules, @"^- \*\*(R\d+) \[(?:machine|review→machine)\]\*\*", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToHashSet();
        Assert.True(machine.Count >= 60, $"probe: {machine.Count} machine rules parsed from v3.0 — the format changed");

        var listed = RulesEnforcement.Manifest.Select(e => e.Rule).ToList();
        Assert.True(listed.Count == listed.Distinct().Count(), "a rule appears twice in RulesEnforcement.Manifest");
        var missing = machine.Except(listed).OrderBy(r => int.Parse(r[1..])).ToList();
        Assert.True(missing.Count == 0, "machine rules with no manifest entry (name the check, or the issue that owes it): " + string.Join(", ", missing));
        var stale = listed.Except(machine).ToList();
        Assert.True(stale.Count == 0, "manifest entries for rules that are not [machine] in v3.0: " + string.Join(", ", stale));

        // Every named check resolves: a test method or class under tests/, or a step in ci.yml.
        var tests = Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText).ToList();
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in tests)
        {
            foreach (Match m in Regex.Matches(text, @"\b(?:Task|void)(?:<[^>]+>)?\s+([A-Za-z0-9_]+)\s*\(")) symbols.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(text, @"\bclass\s+([A-Za-z0-9_]+)")) symbols.Add(m.Groups[1].Value);
        }
        var steps = Regex.Matches(File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml")), @"- name: ([^\r\n]+)")
            .Select(m => m.Groups[1].Value.Trim()).ToHashSet();
        var unresolved = new List<string>();
        foreach (var entry in RulesEnforcement.Manifest)
        {
            // The app's half (RulesEnforcement.App.cs, Arch A1) may name its own pending issue, or say the rule's subject is not here.
            RulesEnforcement.AppOverrides.TryGetValue(entry.Rule, out var over);
            var pending = over?.Pending ?? entry.Pending;
            var notHere = over?.NotHere ?? entry.NotHere;
            Assert.True(entry.Checks.Length > 0 || pending is not null || !string.IsNullOrWhiteSpace(notHere),
                $"{entry.Rule}: no check, no pending issue, and no reason it does not apply here");
            if (pending is { } p) Assert.Matches(@"[\w.-]+#\d+", p); // the issue that owes it (repo#n), so it is visible
            if (!string.IsNullOrWhiteSpace(over?.NotHere)) continue; // its subject is not in this repo, nor need its checks be
            foreach (var check in entry.Checks)
            {
                var ok = check.StartsWith("ci:", StringComparison.Ordinal) ? steps.Contains(check[3..]) : symbols.Contains(check);
                if (!ok) unresolved.Add($"{entry.Rule} -> {check}");
            }
        }
        Assert.True(unresolved.Count == 0, "manifest names checks that do not exist (renamed? not in ci.yml?):\n" + string.Join("\n", unresolved));
        var orphans = RulesEnforcement.AppOverrides.Keys.Except(listed).Order().ToList();
        Assert.True(orphans.Count == 0, "RulesEnforcement.App.cs overrides rules the manifest does not list: " + string.Join(", ", orphans));
    }

    [Fact]
    public void PrTemplate_CarriesTheRuleCheckboxes() // v4 audit T67: R144's PR line, R85's exception line
    {
        // Two rules enforce at review time through the PR template: a coupled client+server change ships one
        // joint-invariant test reading both constants (R144), and a deliberate exception to a numbered rule is
        // written into the rules file in the same PR as the ADR that argues it (R85). The template is the one place
        // those are asked; a reformat that drops a line would drop the rule.
        var template = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "pull_request_template.md")).ReplaceLineEndings(" ");
        Assert.Contains("joint-invariant test", template);
        Assert.Contains("FOUNDATION_RULES", template);
    }

    [Fact]
    public void RuleIds_CitedInTests_AreFinalRules() // v4 audit TR-15/C8 (T11), R116
    {
        // A test comment is where a reader starts when a gate fires; two of them cited Phase-1 candidate ids
        // (R82/R86, R83) for what became R43 and R44, and nothing stopped the next test from citing a number that
        // was renumbered, retired or never existed. The final range is what FOUNDATION_RULES_v3.md's header
        // states — parsed from the file, so a future consolidation moves the gate with it — and candidate ids
        // (`-cand`, a letter suffix) never appear outside the audit folder (R116).
        var root = RepoRoot();
        var header = File.ReadAllText(Path.Combine(root, "docs", "audits", "v4-2026-09", "FOUNDATION_RULES_v3.md")).ReplaceLineEndings("\n");
        header = header[..header.IndexOf("\n## 1.", StringComparison.Ordinal)];
        var final = new HashSet<int>();
        foreach (Match m in Regex.Matches(header, @"R(\d+)–R(\d+)")) // every range the header states as carried or added
            for (var i = int.Parse(m.Groups[1].Value); i <= int.Parse(m.Groups[2].Value); i++) final.Add(i);
        foreach (Match m in Regex.Matches(header, @"R(\d+) \(v2\.0")) final.Add(int.Parse(m.Groups[1].Value)); // "+ R80 (v2.0, …)"
        var retired = Regex.Match(header, @"with \*\*((?:R\d+, )*R\d+ and R\d+) retired\*\*").Groups[1].Value;
        foreach (Match m in Regex.Matches(retired, @"R(\d+)")) final.Remove(int.Parse(m.Groups[1].Value));
        Assert.True(final.Count > 100 && !final.Contains(77) && !final.Contains(94) && final.Contains(80) && final.Contains(158),
            "the v3 header's range did not parse as expected — update the gate with the file");

        var cited = new Dictionary<int, List<string>>();
        var candidates = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var rel = Path.GetRelativePath(root, file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in Regex.Matches(lines[i], @"\bR(\d{1,3})(-cand|[a-z])?\b"))
                {
                    if (m.Groups[2].Success) { candidates.Add($"{rel}:{i + 1}: {m.Value}"); continue; }
                    cited.TryAdd(int.Parse(m.Groups[1].Value), []);
                    cited[int.Parse(m.Groups[1].Value)].Add($"{rel}:{i + 1}");
                }
            }
        }
        Assert.True(cited.Count >= 50, $"probe: only {cited.Count} distinct rule ids cited under tests/ — the scan broke");
        Assert.True(candidates.Count == 0, "candidate rule ids (-cand / letter suffix) belong to the audit folder only:\n" + string.Join("\n", candidates));
        var unknown = cited.Where(kv => !final.Contains(kv.Key)).Select(kv => $"R{kv.Key} at {string.Join(", ", kv.Value.Take(3))}").ToList();
        Assert.True(unknown.Count == 0, "tests cite rule ids outside FOUNDATION_RULES v3.0's final range (renumbered or retired?):\n" + string.Join("\n", unknown));
    }

    [Fact]
    public void AddASliceChecklist_NamesEveryArtifactAGateForces() // v4 audit ADV-P4-17 (T64), R158
    {
        // ADR-004 said "~6 touchpoints"; the v4 re-drill measured seven forced edits, two of them (the tenant-axis
        // canary, the Postman folder) in neither the ADR nor the checklist, whose fixture-reset step had been dead
        // since v2 TR-3. A slice author following the checklist hit gate failures it never warned about. So the
        // checklist is held to the gates: every artifact a gate forces is a step that names its gate, each named
        // gate exists, and the dead step is gone. ADR-004's amendment carries the same list.
        var root = RepoRoot();
        var wow = File.ReadAllText(Path.Combine(root, "docs", "WAYS_OF_WORKING.md")).ReplaceLineEndings("\n");
        var start = wow.IndexOf("**Add-a-slice mechanical checklist**", StringComparison.Ordinal);
        Assert.True(start >= 0, "WAYS_OF_WORKING.md lost its add-a-slice checklist");
        var checklist = wow[start..wow.IndexOf("\n## ", start, StringComparison.Ordinal)];

        // (artifact the step must name, the gate that forces it)
        var forced = new (string Artifact, string Gate)[]
        {
            ("RlsDdl.StatementsFor", "RlsMigrationGateTests"),
            ("docs/DATA_MODEL.md", "EveryEntity_IsDocumentedInDataModel"),
            ("AppAllowlists", "EveryTenantOwnedEntity_IsWiredIntoTenantDissolution"),
            ("AppComposition.cs", "OnlyAppComposition_ReferencesFeatureNamespaces_FromOutsideFeatures"),
            ("EntityWriters", "EveryEntity_HasOneWritingSlice"),
            ("docs/postman/Vuelto.postman_collection.json", "PostmanParityTests"),
            (".env.example", "ConfigKeys_ReadInCode_AreDocumented"),
            (".resx", "ResourceParityTests"),
        };
        var tests = Directory.EnumerateFiles(Path.Combine(root, "tests", "Api.Tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText).ToList();
        foreach (var (artifact, gate) in forced)
        {
            Assert.True(checklist.Contains(artifact, StringComparison.Ordinal), $"the add-a-slice checklist must name {artifact}");
            Assert.True(checklist.Contains(gate, StringComparison.Ordinal), $"the add-a-slice checklist step for {artifact} must name its gate {gate}");
            Assert.True(tests.Any(t => t.Contains(gate, StringComparison.Ordinal)), $"the checklist names a gate that does not exist: {gate}");
        }
        Assert.DoesNotContain("Fixture reset", checklist); // dead since v2 TR-3: the fixture derives its tables from the model
        Assert.DoesNotContain("in `Program.cs`", checklist); // Arch A1: Program.cs is the platform's; a slice never edits it
        Assert.Contains("cannot declare\n   its own `Permission`", checklist); // ADV-P4-18: inherent, so the author is told

        // ADR-004's amendment states the list, and the same gates, not a count.
        var adr = File.ReadAllText(Path.Combine(root, "docs", "DECISIONS.md")).ReplaceLineEndings("\n");
        var amendment = adr[adr.IndexOf("the contract is the measured list, not a\ncount", StringComparison.Ordinal)..];
        amendment = amendment[..amendment.IndexOf("\n**ADR-005", StringComparison.Ordinal)];
        foreach (var gate in new[] { "EveryEntity_IsDocumentedInDataModel", "EveryTenantOwnedEntity_IsWiredIntoTenantDissolution", "PostmanParityTests", "AddASliceChecklist_NamesEveryArtifactAGateForces" })
            Assert.Contains(gate, amendment);
    }

    [Fact]
    public void PlatformMigrations_DoNotNameTheSample() // Arch A6 (#366), R9 as amended
    {
        // The sample's table used to be created by one platform migration and named by another (RlsTenancyBackstop's
        // frozen list), so removing the sample meant editing platform history — vuelto did, and its platform migrations
        // diverged from upstream. Now the sample's unit is its own two migrations (class `sample` in the ownership map),
        // and every other migration is held free of it. The snapshot is the app's (class `adapts`) and is not scanned.
        var root = RepoRoot();
        var rules = Architecture.PlatformOwnership.ParseRules(File.ReadAllText(Path.Combine(root, "platform-ownership.json")));
        // Designer files are the model snapshot at that point in the chain (generated, and the snapshot is the app's), so only
        // the migrations' own operations are read, with comment lines dropped: the question is what the SQL names.
        // Downstream, the platform's migrations are the ones its manifest lists; the app's own may name a Notes column.
        var manifestPath = Path.Combine(root, "tests", "Api.Tests", "App", "platform-manifest.json");
        var platformPaths = File.Exists(manifestPath)
            ? Architecture.PlatformOwnership.ParseManifest(File.ReadAllText(manifestPath)).Entries.Select(e => e.Path).ToHashSet(StringComparer.Ordinal)
            : null;
        var migrations = Directory.EnumerateFiles(Path.Combine(root, "src", "Infrastructure", "Persistence", "Migrations"), "*.cs")
            .Where(f => !f.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal) && !f.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .Select(f => (Rel: Path.GetRelativePath(root, f).Replace('\\', '/'),
                          Text: string.Join('\n', File.ReadLines(f).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)))))
            .Where(m => platformPaths is null || platformPaths.Contains(m.Rel))
            .ToList();
        var sample = migrations.Where(m => Architecture.PlatformOwnership.Classify(m.Rel, rules) == Architecture.PlatformOwnership.Class.Sample).Select(m => m.Rel).ToList();
        if (Directory.Exists(Path.Combine(root, "src", "Api", "Features", "Notes"))) // an app that removed the sample keeps none of its migrations
            Assert.True(sample.Count >= 2, "probe: the sample's own migrations (AddNotesSample, NotesSampleRlsPolicy) are classed `sample`");
        var offenders = migrations
            .Where(m => Architecture.PlatformOwnership.Classify(m.Rel, rules) != Architecture.PlatformOwnership.Class.Sample)
            .Where(m => Regex.IsMatch(m.Text, @"""\bNotes\b"""))
            .Select(m => m.Rel).ToList();
        Assert.True(offenders.Count == 0, "a platform migration names the sample's table — the sample must stay removable without editing platform history (Arch A6): " + string.Join(", ", offenders));
    }

    [Fact]
    public void CompositionFiles_AreFreeOfTheSampleSlice() // Arch A1 (#362), R159
    {
        // The seam, proven on the one app the platform carries: the Notes sample. Before A1 the sample was registered in
        // Program.cs, had its DbSet in AppDbContext and its entity in the canary's handled set — exactly the edits every
        // app made for its own slices, in the same platform-owned files. Now the sample lives in the app-owned halves
        // (AppComposition.cs, AppDbContext.App.cs, tests/Api.Tests/App/AppAllowlists.cs), and none of the platform's
        // composition files names it. Downstream, A2's manifest gate holds the same files identical to the platform's.
        var root = RepoRoot();
        string[] composition =
        [
            "src/Api/Program.cs", "src/Infrastructure/Persistence/AppDbContext.cs", "src/Infrastructure/ServiceCollectionExtensions.cs",
            "tests/Api.Tests/ArchitectureTests.cs", "tests/Api.Tests/DataProtectionIdentityTests.cs", "tests/Api.Tests/RulesEnforcement.cs",
            "tests/Api.Tests/Infrastructure/ServiceHarness.cs", "tests/Api.Tests/Infrastructure/IntegrationTestFactory.cs",
            "tests/Ui.Tests/Infrastructure/TestHttpHandler.cs",
        ];
        // How the sample would be named from a composition file; the ban-list literals in ArchitectureTests' Notes-independence
        // gate are strings, not references, and are not among these.
        string[] sample = ["NotesHandler", "NotesDataContributor", "MapNotes", "nameof(Note)", "Set<Note>()", "Features.Notes;"];
        var offenders = composition
            .Select(f => (f, text: File.ReadAllText(Path.Combine(root, f))))
            .SelectMany(x => sample.Where(s => x.text.Contains(s, StringComparison.Ordinal)).Select(s => $"{x.f}: {s}"))
            .ToList();
        Assert.True(offenders.Count == 0, "a platform composition file names the sample slice — the seam leaks (Arch A1): " + string.Join(", ", offenders));
        foreach (var f in composition) Assert.True(File.Exists(Path.Combine(root, f)), $"composition file moved: {f}");
        Assert.DoesNotContain("Features.", File.ReadAllText(Path.Combine(root, "src", "Api", "Program.cs")).Replace("MapTenantFeatureGroup", ""));
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    public void CourseCoverageAndQuotes_AreCheckedBesideTheQaArtifacts(string workflow) // v4 audit TR-19/TR-12 (T61), R114/R115
    {
        // The course lives in perezosoft-platform; a downstream app has no docs/tutorial (its R114/R115/R118 say NotHere).
        if (!Directory.Exists(Path.Combine(RepoRoot(), "docs", "tutorial"))) return;
        // gen_coverage.py only ran when someone remembered to; the map claimed 901 files and 0 unmapped while the
        // generator found 903 and 1, and lessons quoted code the repo no longer had. The qa-artifacts job — which
        // never gates on the changes classifier, so a docs-only push runs it — checks both on every push.
        var ci = File.ReadAllText(Path.Combine(RepoRoot(), workflow)).ReplaceLineEndings("\n");
        var job = ci[ci.IndexOf("\n  qa-artifacts:", StringComparison.Ordinal)..];
        job = job[..job.IndexOf("\n  license-scan:", StringComparison.Ordinal)];
        Assert.Contains("python docs/tutorial/gen_coverage.py --check\n", job);
        Assert.Contains("python docs/tutorial/gen_coverage.py --check-quotes\n", job);
        // ...and the generator honours those flags (a renamed flag would silently check nothing).
        var generator = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "tutorial", "gen_coverage.py"));
        Assert.Contains("\"--check\" in sys.argv", generator);
        Assert.Contains("\"--check-quotes\" in sys.argv", generator);
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
    public void DeployTriggerWording_PostmanSyncNamesTheForge() // v4 audit TR-14 (H9), R117; ADR-030
    {
        // The per-merge postman-sync runs on the forge where develop moves on every merge. Under ADR-028 that
        // was Forgejo, and the README sent operators to GitHub only, which left the sync skipping forever.
        // Since ADR-030 GitHub is the forge again: the one-time setup leads with GitHub (the secret and the
        // variable live there), and the operating manual names GitHub's develop as what drives the sync.
        var root = RepoRoot();
        var readme = File.ReadAllText(Path.Combine(root, "docs", "postman", "README.md")).ReplaceLineEndings("\n");
        var start = readme.IndexOf("**One-time setup**", StringComparison.Ordinal);
        Assert.True(start >= 0, "docs/postman/README.md lost its One-time setup section");
        var setup = readme[start..readme.IndexOf("**Direction is one-way.**", start, StringComparison.Ordinal)];
        var github = setup.IndexOf("GitHub", StringComparison.Ordinal);
        Assert.True(github >= 0, "the Postman one-time setup must say where the GitHub secret and variable go");
        var forgejo = setup.IndexOf("Forgejo", StringComparison.Ordinal);
        Assert.True(forgejo < 0 || github < forgejo, "the Postman one-time setup must lead with GitHub, the forge (ADR-030)");

        // The operating manual names the forge whose develop changes drive the sync.
        Assert.Contains("on every GitHub `develop` change", File.ReadAllText(Path.Combine(root, "CLAUDE.md")).ReplaceLineEndings(" "),
            StringComparison.Ordinal);

        // The workflow hardcodes the collection path, so a rebrand that renames the files edits it.
        var rebranding = File.ReadAllText(Path.Combine(root, "docs", "REBRANDING.md"));
        Assert.Contains(".github/workflows/postman-sync.yml", rebranding, StringComparison.Ordinal);
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
        // report, the run plan) is shell, awk and Python that no C# test executes. tests/ci-logic/ runs each of
        // them for real against fixtures. It needs the GNU tools the runners have, so it runs on Linux only — and the
        // build-test job runs on Linux, so it is never skipped where it counts.
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

        var anchored = Directory.EnumerateFiles(Path.Combine(root, ".github"), "*", SearchOption.AllDirectories)
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

    [Fact]
    public void GateLane_RunsEveryDeploymentGate_AgainstTheShippedDefault() // R147 (v4 audit T48, BILL-10)
    {
        // Every other browser journey runs with Billing__Enabled=true and no green list, so until T48 nothing in a
        // browser ever saw the posture a new app launches with. The gates-off lane is one step in the e2e job that
        // restarts the API with the shipped default and runs the journeys tagged Gate; this holds the pieces to
        // each other: the step's env has no billing switch and names the one listed domain, that domain is the
        // literal GateJourneyTests uses, every deployment gate has a tagged journey, and the default lane leaves
        // the gate journeys out (they would fail against billing-on).
        var root = RepoRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        var gates = File.ReadAllText(Path.Combine(root, "tests", "E2E.Tests", "GateJourneyTests.cs"));
        var script = File.ReadAllText(Path.Combine(root, "tools", "e2e.ps1"));

        var listed = Regex.Match(gates, @"ListedDomain\s*=\s*""([^""]+)""").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(listed), "GateJourneyTests.ListedDomain moved");

        var step = Regex.Match(workflow, @"- name: Gates off.*?(?=\n      - name:)", RegexOptions.Singleline).Value;
        Assert.False(step.Length == 0, "the e2e job has no 'Gates off' step");
        Assert.DoesNotContain("Billing__Enabled", step);                               // the shipped default: unset
        Assert.Contains($"Signup__AllowedDomains__0: \"{listed}\"", step);            // the one listed domain
        Assert.Contains("--filter \"TestCategory=Gate\"", step);                       // only the gate journeys
        Assert.Contains("TestCategory!=Gate", workflow);                                // and the default lane excludes them
        Assert.Contains($"--Signup:AllowedDomains:0={listed}", script);                // the local lane agrees,
        Assert.Contains("'--Billing:Enabled=false'", script);                           // and overrides a developer's .env
        Assert.Matches(@"'TestCategory=Gate'.*'TestCategory!=Gate'", script);

        // Every deployment gate (ADR-027) has at least one journey tagged for it in the lane.
        string[] deploymentGates = ["GateBilling", "GateSignup"];
        foreach (var gate in deploymentGates)
            Assert.True(Regex.IsMatch(gates, $@"\[Category\(""{gate}""\)\]\s*\n\s*public async Task \w+"), $"no journey is tagged {gate}");
        Assert.Contains("[Category(\"Gate\")]", gates); // the fixture-level tag the lanes select on
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root.");
    }
}
