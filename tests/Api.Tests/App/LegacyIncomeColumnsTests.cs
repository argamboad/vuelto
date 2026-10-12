using System.Text.RegularExpressions;

namespace Vuelto.Api.Tests.App;

/// <summary>
/// Vuelto's own architecture gate (Arch A1: an app's gates live beside its allowlists, not in the platform's
/// <c>ArchitectureTests</c>, which stays identical across repos).
/// </summary>
public sealed class LegacyIncomeColumnsTests
{
    [Fact]
    public void LegacyIncomeColumns_AreReadOrWrittenByNothing()
    {
        // INCOME-1 (ADR-V023, plan §4a): the old two-income columns on BudgetSettings / Months are the rollback baseline
        // for the AddIncomeLines migration. They stay mapped until INCOME-3 drops them, but no code may read or write
        // them — a new reader would silently diverge from the income rows, a writer would corrupt the baseline. Only the
        // entities, their EF configurations, the migrations and the one-time backfill may name them.
        var allowed = new[]
        {
            Path.Combine("Core", "Entities", "BudgetSettings.cs"),
            Path.Combine("Core", "Entities", "Month.cs"),
            Path.Combine("Configurations", "BudgetSettingsConfiguration.cs"),
            Path.Combine("Configurations", "MonthConfiguration.cs"),
            Path.Combine("Persistence", "IncomeBackfill.cs"),
            $"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
        };
        var legacy = new Regex(@"\b(Primary|Secondary)Income(4w|5w|Amount|Currency)\b|\b(primary|secondary)_income_|\bincome_(primary|secondary)\b|\bIncome(Primary|Secondary)\b");
        var offenders = SourceFiles(Path.Combine(RepoRoot(), "src"))
            .Concat(SourceFiles(Path.Combine(RepoRoot(), "src"), "*.razor"))
            .Where(f => !allowed.Any(a => f.Contains(a, StringComparison.Ordinal)))
            .Where(f => legacy.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(RepoRoot(), f))
            .ToList();

        Assert.True(offenders.Count == 0, $"Code still names the legacy income columns: {string.Join(", ", offenders)}");
    }

    private static IEnumerable<string> SourceFiles(string dir, string pattern = "*.cs") =>
        !Directory.Exists(dir)
            ? []
            : Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Api", "Features")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root from the test assembly.");
    }
}
