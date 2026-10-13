using System.Text.RegularExpressions;

namespace Vuelto.Api.Tests;

/// <summary>
/// Two contracts between the product and the things that check it.
/// <list type="bullet">
/// <item>The <b>test-id contract</b>, one direction (Pkg 1.3, #391): a <c>data-testid</c> in <c>src/Shared.Ui</c> is an
/// interface — component tests, browser journeys, the Android smoke and the QA plan all address the UI through it — so
/// an id a test or a QA case references must exist, or that test cannot find its control. The other direction (every
/// declared id must be used) and the analysis of parameterised components were retired: an id nobody references harms
/// nothing, and the analyzer was a gate about the UI's structure rather than about a behaviour. An id a reusable
/// component derives from a caller's literal <c>TestId="…"</c> (with a suffix) counts as existing.</item>
/// <item><b>No process-wide switches in tests</b>: a <c>[ModuleInitializer]</c> that sets an environment
/// variable changes every test in the run. A test that needs a config gate on asks
/// <c>IntegrationTestFactory.WithGates</c> for a host of its own.</item>
/// </list>
/// </summary>
public class TestIdContractTests
{
    [Fact]
    public void EveryTestId_ReferencedByATestOrAQaCase_Exists()
    {
        var root = RepoRoot();
        var razor = SourceFiles(Path.Combine(root, "src", "Shared.Ui"), "*.razor")
            .Select(f => File.ReadAllText(f) + (File.Exists(f + ".cs") ? "\n" + File.ReadAllText(f + ".cs") : ""))
            .ToList();
        Assert.True(razor.Count >= 15, $"probe: only {razor.Count} .razor files found under src/Shared.Ui");

        // Where the UI is addressed from: C# tests (bUnit selectors, Playwright page objects), the Node smoke
        // and JS tests, and the QA plan.
        var consumers = SourceFiles(Path.Combine(root, "tests"), "*.cs")
            .Concat(SourceFiles(Path.Combine(root, "tests"), "*.js").Where(f => !f.Contains("node_modules")))
            .Where(f => Path.GetFileName(f) != "TestIdContractTests.cs") // this file's own examples are not references
            .Append(Path.Combine(root, "docs", "QA_TEST_PLAN.md"))
            .Select(File.ReadAllText).ToList();

        var a = TestIdContract.Analyze(razor, consumers);
        Assert.True(a.Declared.Count >= 90, $"probe: only {a.Declared.Count} data-testid values found under src/Shared.Ui");
        Assert.True(a.Used.Count >= 80, $"probe: only {a.Used.Count} test-id references found under tests/ and the QA plan");
        Assert.True(a.Missing.Count == 0, "test ids referenced by a test or the QA plan that no longer exist in src/Shared.Ui: " + string.Join(", ", a.Missing));
    }

    [Fact]
    public void AReferencedId_ThatDoesNotExist_Fails_AndADeclaredUnusedOneDoesNot()
    {
        var razor = new[]
        {
            """<button data-testid="save">Save</button> <button data-testid="never-tested">x</button>""",
            """<AmountField TestId="expense-amount" />""",
        };
        var a = TestIdContract.Analyze(razor, ["""GetByTestId("save") [data-testid=expense-amount-input] [data-testid=renamed-away]"""]);

        Assert.Equal(["renamed-away"], a.Missing); // a referenced id with no control behind it still fails
        Assert.Empty(TestIdContract.Analyze(razor, ["""GetByTestId("save")"""]).Missing); // a declared id nothing references does not
    }

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

/// <summary>
/// The test-id contract as a pure function over sources, so the rule can be held on fixtures and read the same way
/// by every repo. Input: each <c>.razor</c> file's text (with its code-behind appended, if any) and the texts that
/// address the UI (tests, the QA plan). Output: the ids that exist, the ids referenced, and the referenced ones that do not exist.
/// </summary>
internal static class TestIdContract
{
    public sealed record Analysis(IReadOnlySet<string> Declared, IReadOnlySet<string> Used, IReadOnlyList<string> Missing);

    private static readonly Regex Literal = new(@"data-testid=""([A-Za-z0-9_-]+)""", RegexOptions.Compiled);
    // A caller's literal id for a component that derives its own (TestId="x" -> x, x-input, ...), in markup or in a component test.
    private static readonly Regex CallerLiteral = new(@"\bTestId=""([A-Za-z0-9_-]+)""", RegexOptions.Compiled);
    private static readonly Regex TestCallerLiteral = new(@"\.TestId\s*,\s*""([A-Za-z0-9_-]+)""", RegexOptions.Compiled);

    public static Analysis Analyze(IEnumerable<string> razorTexts, IEnumerable<string> consumerTexts)
    {
        var consumers = consumerTexts.ToList();
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var bases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in razorTexts)
        {
            foreach (Match m in Literal.Matches(text)) declared.Add(m.Groups[1].Value);
            foreach (Match m in CallerLiteral.Matches(text)) bases.Add(m.Groups[1].Value);
        }
        foreach (var text in consumers)
            foreach (Match m in TestCallerLiteral.Matches(text)) bases.Add(m.Groups[1].Value);

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in consumers)
        {
            foreach (Match m in Regex.Matches(text, @"data-testid=\\?['""]?([A-Za-z0-9_-]+)")) used.Add(m.Groups[1].Value);      // [data-testid=x] / ='x' / =\"x\"
            foreach (Match m in Regex.Matches(text, @"(?i:GetByTestId|TestId)\(\s*\$?['""]([A-Za-z0-9_-]+)['""]")) used.Add(m.Groups[1].Value); // GetByTestId("x") / getByTestId('x')
        }

        var missing = used.Where(id => !declared.Contains(id) && !bases.Any(b => id == b || id.StartsWith(b + "-", StringComparison.Ordinal))).Order().ToList();
        return new Analysis(declared, used, missing);
    }
}
