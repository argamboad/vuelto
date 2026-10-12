using System.Text.RegularExpressions;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// P11 localization sweep, made permanent: every resource key a shared component or page references
/// literally — <c>Localizer["Key"]</c>, <c>L["Key"]</c>, and the <c>…Key="Key"</c> component parameters
/// (CatalogPage / ExpenseLinesSection) — must exist in <c>AppStrings.resx</c>, so a typo renders as a red
/// test instead of a raw key in the chrome. (EN ↔ ES parity is the platform's <c>ResourceParityTests</c>.)
/// Dynamic lookups (<c>$"Plan_{key}"</c>) are out of scope by construction and fall back to the raw value.
/// </summary>
public class LocalizationKeyCoverageTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Vuelto.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Vuelto.slnx not found above the test bin folder");
    }

    private static IEnumerable<string> SourceFiles(string dir) =>
        Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    [Fact]
    public void EveryLiteralResourceKey_ReferencedByTheSharedUi_ExistsInTheResx()
    {
        var root = RepoRoot();
        var resx = File.ReadAllText(Path.Combine(root, "src", "Shared.Ui", "Resources", "AppStrings.resx"));
        var keys = Regex.Matches(resx, "<data name=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(keys);

        var indexer = new Regex("\\b(?:Localizer|L)\\[\\s*\"([A-Za-z][A-Za-z0-9_]*)\"", RegexOptions.Compiled);
        var parameter = new Regex("\\b[A-Za-z]+Key=\"([A-Za-z][A-Za-z0-9_]*)\"", RegexOptions.Compiled);

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceFiles(Path.Combine(root, "src", "Shared.Ui")))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in indexer.Matches(text))
                if (!keys.Contains(m.Groups[1].Value)) missing.Add($"{m.Groups[1].Value} ({Path.GetFileName(file)})");
            foreach (Match m in parameter.Matches(text))
                if (!keys.Contains(m.Groups[1].Value)) missing.Add($"{m.Groups[1].Value} ({Path.GetFileName(file)})");
        }

        Assert.True(missing.Count == 0, $"Resource keys referenced in the shared UI but missing from AppStrings.resx: {string.Join(", ", missing)}");
    }
}
