using System.Text.RegularExpressions;

namespace Vuelto.Api.Tests;

/// <summary>
/// v4 audit T59 (TB-DOC-5, ADV-P4-14 — R149, and the ban half of R154). Two contracts between the product and
/// the things that check it, both of which used to hold by habit:
/// <list type="bullet">
/// <item>The <b>test-id contract</b>: a <c>data-testid</c> in <c>src/Shared.Ui</c> is an interface — component
/// tests, browser journeys, the Android smoke and the QA plan all address the UI through it, and the planned
/// stack-neutral spec (FLAVORS SPEC-4) is built on the same list. An id nothing references can be renamed
/// without a failure; a referenced id that no longer exists is a test that cannot find its control.</item>
/// <item><b>No process-wide switches in tests</b>: a <c>[ModuleInitializer]</c> that sets an environment
/// variable changes every test in the run. A test that needs a config gate on asks
/// <c>IntegrationTestFactory.WithGates</c> for a host of its own.</item>
/// </list>
/// </summary>
public class TestIdContractTests
{
    // Not here yet (vuelto#125): the platform's test-id contract (R149) — every data-testid a literal, used by a
    // test or a QA case, and every id a test names existing. This repo's own components take their id as a
    // parameter (65 computed ids in 24 components, of 591 declared), which the contract's first clause refuses,
    // so it needs a decision and a sweep of the app's UI, not a port. R149 stays pending in the manifest.

    [Fact]
    public void Tests_DoNotSwitchTheEnvironment_FromAModuleInitializer()
    {
        // One exists, and it is not a feature switch: it opts the whole test assembly out of loading the
        // developer's local .env, so a personal SMTP password cannot leak into a test run. Named here with that
        // reason; any other [ModuleInitializer] in tests/ is refused — use IntegrationTestFactory.WithGates.
        var allowed = new Dictionary<string, string>
        {
            ["tests/Api.Tests/LocalDotEnvTests.cs"] = "opts the assembly out of the developer's .env (LocalDotEnv.SkipVariable); not a feature gate",
        };

        var root = RepoRoot();
        var found = SourceFiles(Path.Combine(root, "tests"), "*.cs")
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"^\s*\[(?:System\.Runtime\.CompilerServices\.)?ModuleInitializer\]", RegexOptions.Multiline))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToList();

        Assert.Equal(allowed.Keys.Order(), found.Order());
    }

    private static IEnumerable<string> SourceFiles(string dir, string pattern) =>
        Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.EnumerateFiles("*.slnx").Any()) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
