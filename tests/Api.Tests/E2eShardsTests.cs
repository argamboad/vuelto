using System.Text.RegularExpressions;

namespace Vuelto.Api.Tests;

/// <summary>
/// v4 T12 (UX-15, R113; TB-DOC-1): the E2E shard step lists every [Test] with <c>--list-tests</c>, which ignores
/// <c>--filter</c>, so the [Explicit] native smoke (category NativeSmoke) was being dealt into a browser shard by
/// name and stayed out of the run only through the NUnit adapter's handling of [Explicit]. Both workflow copies now
/// drop it from the list and exclude the category from the run filter. The suite size the docs quote is derived
/// here, not typed by hand.
/// </summary>
public class E2eShardsTests
{
    public static TheoryData<string> Workflows => [".forgejo/workflows/ci.yml", ".github/workflows/ci.yml"];

    [Theory]
    [MemberData(nameof(Workflows))]
    public void E2eShards_KeepTheNativeSmokeOut(string workflow)
    {
        var yml = File.ReadAllText(Path.Combine(RepoRoot(), workflow));
        var shardStep = yml[yml.IndexOf("--list-tests", StringComparison.Ordinal)..];

        // Out of the list (so it never takes a shard slot or inflates the count) ...
        Assert.Matches(@"Category\(""NativeSmoke""\)", shardStep);
        // ... and out of the run, whatever the adapter does with [Explicit].
        Assert.Contains("&TestCategory!=NativeSmoke", shardStep);
    }

    [Fact]
    public void E2eShards_SuiteSizeInTheStory_IsTheCountOfJourneys()
    {
        var journeys = JourneyCount();
        var story = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "stories", "e2e.md"));
        var quoted = int.Parse(Regex.Match(story, @"Current suite size: (\d+) tests").Groups[1].Value);

        Assert.True(quoted == journeys,
            $"docs/stories/e2e.md says {quoted} tests; tests/E2E.Tests has {journeys} non-explicit [Test] methods. Update the line.");
    }

    // [Test] methods, minus those in an [Explicit] class (the native smoke). [TestCase]s would count one per method,
    // which is also how --list-tests names them for sharding.
    private static int JourneyCount() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "tests", "E2E.Tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText)
            .Where(t => !Regex.IsMatch(t, @"^\s*\[Explicit\b", RegexOptions.Multiline))
            .Sum(t => Regex.Matches(t, @"^\s*\[Test\]", RegexOptions.Multiline).Count);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Api", "Features")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root from the test assembly.");
    }
}
