using System.Text.RegularExpressions;
using Vuelto.Core.Billing;

namespace Vuelto.Api.Tests.Billing;

/// <summary>
/// v4 audit T47 (BILL-6 — R88): catalog numbers are read from <see cref="PlanCatalog"/>, never copied. When the
/// Free seat cap moved from 3 to 5, seven places kept saying 3 — among them a manual adversarial drill that no
/// longer reproduced the race it exists to test. Docs state a cap in a handful of recognisable shapes; each one
/// found must be the catalog's number, unless the sentence is plainly telling the history ("raised from 3").
/// </summary>
public class CatalogNumbersTests
{
    private static int Free => PlanCatalog.Get(PlanKeys.Free).SeatLimit!.Value;
    private static int Pro => PlanCatalog.Get(PlanKeys.Pro).SeatLimit!.Value;

    [Fact]
    public void DocsThatStateASeatCap_StateTheCatalogs()
    {
        var root = RepoRoot();
        // The shapes a seat cap is written in. Group "n" is the number claimed; "plan" says whose it is.
        Regex[] claims =
        [
            new(@"(?<plan>Free|Pro) \(cap (?<n>\d+)\)"),
            new(@"(?<plan>Free|Pro) seats=(?<n>\d+)"),
            new(@"`(?<plan>Free|Pro)` is \*\*(?<n>\d+)\*\* seats"),
            new(@"(?<plan>Free|Pro) \((?<n>\d+) seats\)"),
            new(@"PlanCatalog: (?<n>\d+)\)(?<plan>)"),                    // "(PlanCatalog: 5)" — always the Free cap in the stories
            new(@"(?<plan>Free) seats \*\*(?<n>\d+)"),
        ];
        var files = Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories)
            .Where(f => !f.Replace('\\', '/').Contains("/docs/audits/")) // the audit quotes what it found
            .Append(Path.Combine(root, "CLAUDE.md")).Append(Path.Combine(root, "README.md")).ToList();

        var wrong = new List<string>();
        var seen = 0;
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var claim in claims)
                    foreach (Match m in claim.Matches(lines[i]))
                    {
                        seen++;
                        var expected = m.Groups["plan"].Value == "Pro" ? Pro : Free;
                        var telling = Regex.IsMatch(lines[i], @"raised (?:from|to)|\bthen\b|→|until GATES|shipped", RegexOptions.IgnoreCase); // history, said as history
                        if (int.Parse(m.Groups["n"].Value) != expected && !telling)
                            wrong.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: \"{m.Value}\" (the catalog says {expected})");
                    }
            }
        }
        Assert.True(seen >= 4, $"probe: only {seen} seat-cap statements found in the docs — the shapes moved");
        Assert.True(wrong.Count == 0, "docs state a seat cap that is not PlanCatalog's:\n" + string.Join("\n", wrong));
    }

    [Fact]
    public void QaDrills_ExpressSeatCountsRelativeToTheCap()
    {
        // A walkthrough that says "put the tenant at 2 seats used" is wrong the day the cap moves. The seat
        // drills say "one below the cap" / "exactly the cap".
        var plan = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "QA_TEST_PLAN.md")).ReplaceLineEndings("\n");
        var drill = plan[plan.IndexOf("### QA-ADV-15", StringComparison.Ordinal)..];
        drill = drill[..drill.IndexOf("\n### ", 5, StringComparison.Ordinal)];
        Assert.DoesNotMatch(@"\*\*\d+ seats used\*\*|\(cap \d+\)|exactly \d+\b|\(seat \d+\)", drill);
        Assert.Contains("below its cap", drill);
    }

    [Fact]
    public void TheBrowserTests_HoldOneCopyOfTheFreeCap_AndItIsTheCatalogs()
    {
        var root = RepoRoot();
        var copies = Directory.EnumerateFiles(Path.Combine(root, "tests", "E2E.Tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"const int FreePlanSeatLimit = (\d+);").Select(m => (File: Path.GetFileName(f), Value: int.Parse(m.Groups[1].Value))))
            .ToList();

        var (file, value) = Assert.Single(copies);
        Assert.Equal("E2ETestBase.cs", file);
        Assert.Equal(Free, value);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.EnumerateFiles("*.slnx").Any()) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
