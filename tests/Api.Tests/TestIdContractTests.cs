using System.Text.RegularExpressions;

namespace Vuelto.Api.Tests;

/// <summary>
/// v4 audit T59 (TB-DOC-5, ADV-P4-14 — R149, and the ban half of R154). Two contracts between the product and
/// the things that check it, both of which used to hold by habit:
/// <list type="bullet">
/// <item>The <b>test-id contract</b>: a <c>data-testid</c> in <c>src/Shared.Ui</c> is an interface — component
/// tests, browser journeys, the Android smoke and the QA plan all address the UI through it, and the planned
/// stack-neutral spec (FLAVORS SPEC-4) is built on the same list. An id nothing references can be renamed
/// without a failure; a referenced id that no longer exists is a test that cannot find its control.
/// <b>Amended by Arch A12 (#371, 2026-10-06):</b> a reusable component may take its id as a parameter
/// (<c>[Parameter] string TestId</c>) and derive suffixed ids from it (<c>@($"{TestId}-input")</c>); every caller
/// passes a literal, and the ids that exist are the callers' literals with the component's suffixes — those are
/// what a test or a QA case must use. Any other computed id is refused: it cannot be listed, searched for or put
/// in a spec. The analysis is <see cref="TestIdContract"/>, proven on fixtures below; the platform's own UI has no
/// parameterised component today, so the fixtures are where the amendment is held.</item>
/// <item><b>No process-wide switches in tests</b>: a <c>[ModuleInitializer]</c> that sets an environment
/// variable changes every test in the run. A test that needs a config gate on asks
/// <c>IntegrationTestFactory.WithGates</c> for a host of its own.</item>
/// </list>
/// </summary>
public class TestIdContractTests
{
    [Fact(Skip = "vuelto#198: the R149 sweep of this app's UI (computed ids, unused and missing references) is its own issue")]
    public void EveryTestId_IsUsedByATestOrAQaCase_AndEveryUsedIdExists()
    {
        var root = RepoRoot();
        var razor = SourceFiles(Path.Combine(root, "src", "Shared.Ui"), "*.razor")
            .ToDictionary(f => Path.GetFileName(f), f => File.ReadAllText(f) + (File.Exists(f + ".cs") ? "\n" + File.ReadAllText(f + ".cs") : ""));
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

        Assert.True(a.Computed.Count == 0, "computed data-testid values (a literal id, or a [Parameter] TestId with a literal suffix — the variable part goes in a data- attribute): " + string.Join(", ", a.Computed));
        Assert.True(a.NonLiteralCallers.Count == 0, "callers that pass a computed TestId to a parameterised component (pass a literal, so the id can be listed and searched): " + string.Join(", ", a.NonLiteralCallers));
        Assert.True(a.UncalledParameterised.Count == 0, "parameterised components no caller gives a TestId to (their ids never exist, so nothing can test them): " + string.Join(", ", a.UncalledParameterised));
        Assert.True(a.Unused.Count == 0, "data-testid values no test or QA case references (drive the control from a test, or drop the id): " + string.Join(", ", a.Unused));
        Assert.True(a.Missing.Count == 0, "test ids referenced by a test or the QA plan that no longer exist in src/Shared.Ui: " + string.Join(", ", a.Missing));
    }

    // ── the amendment, held on fixtures (Arch A12) ────────────────────────────────────────────────────────────

    private const string AmountField = """
        <div class="amount-field" data-testid="@TestId">
            <input data-testid="@($"{TestId}-input")" />
            <fieldset data-testid="@($"{TestId}-currency")"></fieldset>
        </div>
        @code {
            [Parameter] public string TestId { get; set; } = "";
            [Parameter] public decimal Value { get; set; }
        }
        """;

    [Fact]
    public void AParameterisedComponent_DeclaresItsCallersLiterals_WithItsSuffixes()
    {
        var razor = new Dictionary<string, string>
        {
            ["AmountField.razor"] = AmountField,
            ["Expense.razor"] = """<AmountField TestId="expense-amount" Value="@_amount" /> <button data-testid="expense-save">Save</button>""",
            ["Income.razor"] = """<AmountField Value="1" TestId="income-amount"></AmountField>""",
        };
        var a = TestIdContract.Analyze(razor, ["""
            cut.Find("[data-testid=expense-amount-input]"); Page.GetByTestId("expense-amount-currency"); GetByTestId("expense-save");
            [data-testid=income-amount] [data-testid=income-amount-input] [data-testid=income-amount-currency] GetByTestId("expense-amount")
            """]);

        Assert.Equal(["expense-amount", "expense-amount-currency", "expense-amount-input", "expense-save",
                      "income-amount", "income-amount-currency", "income-amount-input"], a.Declared.Order());
        Assert.Empty(a.Computed); Assert.Empty(a.NonLiteralCallers); Assert.Empty(a.UncalledParameterised);
        Assert.Empty(a.Unused); Assert.Empty(a.Missing);
    }

    [Fact]
    public void AComputedId_IsRefused_EvenInsideAParameterisedComponent()
    {
        var razor = new Dictionary<string, string>
        {
            ["AmountField.razor"] = AmountField.Replace("""<fieldset data-testid="@($"{TestId}-currency")"></fieldset>""",
                """@foreach (var c in Currencies) { <input data-testid="@($"{TestId}-currency-{c}")" /> }"""),
            ["Row.razor"] = """<tr data-testid="row-@Id"></tr>""",
            ["Expense.razor"] = """<AmountField TestId="expense-amount" />""",
        };
        var a = TestIdContract.Analyze(razor, ["""GetByTestId("expense-amount") GetByTestId("expense-amount-input")"""]);

        Assert.Equal(["AmountField.razor: @($\"{TestId}-currency-{c}\")", "Row.razor: row-@Id"], a.Computed.Order());
    }

    [Fact]
    public void ACallerPassingAComputedTestId_IsRefused_AndAnUncalledComponentIsReported()
    {
        var razor = new Dictionary<string, string>
        {
            ["AmountField.razor"] = AmountField,
            ["Expense.razor"] = """<AmountField TestId="@($"line-{i}")" />""",
            ["Lonely.razor"] = """<div data-testid="@TestId"></div> @code { [Parameter] public string TestId { get; set; } = ""; }""",
        };
        var a = TestIdContract.Analyze(razor, []);

        Assert.Equal(["Expense.razor → AmountField TestId=\"@($\"line-{i}\")\""], a.NonLiteralCallers);
        Assert.Equal(["AmountField.razor", "Lonely.razor"], a.UncalledParameterised.Order());
    }

    [Fact]
    public void ATestIdUsedWithoutTheParameter_IsComputed_NotParameterised()
    {
        // @TestId with no [Parameter] behind it is a local variable: nothing outside the component chooses it.
        var razor = new Dictionary<string, string> { ["Card.razor"] = """<div data-testid="@TestId"></div> @code { private string TestId => $"card-{Id}"; }""" };
        var a = TestIdContract.Analyze(razor, []);
        Assert.Equal(["Card.razor: @TestId"], a.Computed);
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
/// The test-id contract as a pure function over sources (R149 as amended by Arch A12), so the rule can be held on
/// fixtures and read the same way by every repo. Input: each <c>.razor</c> file's text (with its code-behind
/// appended, if any) keyed by file name, and the texts that address the UI (tests, the QA plan).
/// </summary>
internal static class TestIdContract
{
    public sealed record Analysis(
        IReadOnlySet<string> Declared,
        IReadOnlySet<string> Used,
        IReadOnlyList<string> Computed,
        IReadOnlyList<string> NonLiteralCallers,
        IReadOnlyList<string> UncalledParameterised,
        IReadOnlyList<string> Unused,
        IReadOnlyList<string> Missing);

    // data-testid="…": either the parameterised form @($"{TestId}…") — which carries its own quotes — or anything else.
    private static readonly Regex Attribute = new(@"data-testid=""(@\(\$""[^""]*""\)|[^""]*)""", RegexOptions.Compiled);
    // The shapes a parameterised component may use: the parameter itself, or the parameter with a literal suffix.
    private static readonly Regex Parameterised = new(@"^(?:@TestId|@\(\$""\{TestId\}(-[A-Za-z0-9_-]+)?""\))$", RegexOptions.Compiled);
    private static readonly Regex TestIdParameter = new(@"\[Parameter\][^;{}]*?\bstring\??\s+TestId\b", RegexOptions.Compiled | RegexOptions.Singleline);

    public static Analysis Analyze(IReadOnlyDictionary<string, string> razorByFile, IEnumerable<string> consumerTexts)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var computed = new List<string>();
        var suffixesByComponent = new Dictionary<string, List<string>>(StringComparer.Ordinal); // component name → "" and "-input" …

        foreach (var (file, text) in razorByFile)
        {
            var component = Path.GetFileNameWithoutExtension(file);
            var takesTestId = TestIdParameter.IsMatch(text);
            foreach (Match m in Attribute.Matches(text))
            {
                var id = m.Groups[1].Value;
                if (!id.Contains('@')) { declared.Add(id); continue; }
                var p = Parameterised.Match(id);
                if (!takesTestId || !p.Success) { computed.Add($"{file}: {id}"); continue; }
                if (!suffixesByComponent.TryGetValue(component, out var suffixes)) suffixesByComponent[component] = suffixes = [];
                suffixes.Add(p.Groups[1].Success ? p.Groups[1].Value : "");
            }
        }

        // A parameterised component's ids are its callers' literals, one per suffix.
        var nonLiteralCallers = new List<string>();
        var called = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (component, suffixes) in suffixesByComponent)
        {
            // A caller's value carries its own quotes when it is computed (@($"line-{i}")), so match that shape first.
            var usage = new Regex(@"<" + Regex.Escape(component) + @"\b[^>]*?\sTestId=""(@\(\$""[^""]*""\)|[^""]*)""", RegexOptions.Singleline);
            foreach (var (file, text) in razorByFile)
                foreach (Match m in usage.Matches(text))
                {
                    var literal = m.Groups[1].Value;
                    if (literal.Contains('@')) { nonLiteralCallers.Add($"{file} → {component} TestId=\"{literal}\""); continue; }
                    called.Add(component);
                    foreach (var suffix in suffixes.Distinct()) declared.Add(literal + suffix);
                }
        }
        var uncalled = suffixesByComponent.Keys.Where(c => !called.Contains(c)).Select(c => c + ".razor").ToList();

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in consumerTexts)
        {
            foreach (Match m in Regex.Matches(text, @"data-testid=\\?['""]?([A-Za-z0-9_-]+)")) used.Add(m.Groups[1].Value);      // [data-testid=x] / ='x' / =\"x\"
            foreach (Match m in Regex.Matches(text, @"(?i:GetByTestId|TestId)\(\s*\$?['""]([A-Za-z0-9_-]+)['""]")) used.Add(m.Groups[1].Value); // GetByTestId("x") / getByTestId('x')
        }

        return new Analysis(declared, used, computed, nonLiteralCallers, uncalled,
            declared.Except(used).Order().ToList(), used.Except(declared).Order().ToList());
    }
}
