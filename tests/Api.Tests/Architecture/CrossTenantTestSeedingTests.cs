using System.Text.RegularExpressions;

namespace Vuelto.Api.Tests.Architecture;

/// <summary>
/// R146 (v4 audit H4/T10): a test whose name promises a cross-tenant proof — <c>OtherTenant</c>,
/// <c>CrossTenant</c>, <c>IsTenantScoped</c> — must arrange a real second tenant, through
/// <see cref="Infrastructure.TwoTenants"/>. <c>Replay_UnknownOrOtherTenant_ReturnsFalse</c> replayed a random
/// id that existed nowhere: "unknown" and "someone else's" both answer false, so the test stayed green whether
/// or not the hand-written tenant filter was there, and every file seeded its tenants its own way. The helper
/// seeds both tenants and refuses a pair that isn't two distinct tenants, so passing through it is the proof
/// the second tenant exists. A class that seeds its pair once for every test (an <c>InitializeAsync</c>)
/// satisfies the rule for each of its tests.
/// </summary>
public class CrossTenantTestSeedingTests
{
    private static readonly Regex Claim = new(@"(OtherTenant|CrossTenant|IsTenantScoped)");
    private static readonly Regex TestMethod = new(@"\[(Fact|Theory)[^\]]*\][\s\S]*?public\s+(?:async\s+)?(?:Task|void)\s+(\w+)\s*\(");

    [Fact]
    public void EveryCrossTenantTest_SeedsARealSecondTenant()
    {
        var offenders = new List<string>();
        var claims = 0;
        foreach (var file in TestSources())
        {
            var source = File.ReadAllText(file).ReplaceLineEndings("\n");
            var classSeeds = Regex.IsMatch(Body(source, "InitializeAsync"), @"TwoTenants\.SeedAsync");
            foreach (Match m in TestMethod.Matches(source))
            {
                var name = m.Groups[2].Value;
                if (!Claim.IsMatch(name)) continue;
                claims++;
                if (!classSeeds && !Body(source, name).Contains("TwoTenants.SeedAsync", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}: {name}");
            }
        }

        Assert.True(claims > 0, "the scan found no cross-tenant test at all — the name pattern or the source root is wrong");
        Assert.True(offenders.Count == 0,
            "These tests claim a cross-tenant proof but never arrange a second tenant through TwoTenants.SeedAsync "
            + "(R146) — a random id that exists nowhere passes whether or not the tenant filter is there: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void TheScan_CatchesARandomIdArrange()
    {
        // The shape of the original Replay_UnknownOrOtherTenant_ReturnsFalse.
        const string vacuous = """
            [Fact]
            public async Task Replay_UnknownOrOtherTenant_ReturnsFalse()
            {
                var tenant = Guid.CreateVersion7();
                Assert.False(await Service(tenant).ReplayAsync(Guid.CreateVersion7(), default));
            }
            """;
        var m = TestMethod.Match(vacuous);
        Assert.True(m.Success && Claim.IsMatch(m.Groups[2].Value));
        Assert.DoesNotContain("TwoTenants.SeedAsync", Body(vacuous, m.Groups[2].Value), StringComparison.Ordinal);
    }

    // A method's text from its name to the next test attribute (or the end of the file): enough to hold its
    // arrange, and never the following test's.
    private static string Body(string source, string method)
    {
        var at = Regex.Match(source, $@"\s{Regex.Escape(method)}\s*\(");
        if (!at.Success) return "";
        var rest = source[at.Index..];
        var next = Regex.Match(rest[1..], @"\n\s*\[(Fact|Theory)");
        return next.Success ? rest[..(next.Index + 1)] : rest;
    }

    private static IEnumerable<string> TestSources()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
            dir = dir.Parent;
        var tests = Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root."), "tests");
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}obj{sep}")
                && !f.EndsWith(nameof(CrossTenantTestSeedingTests) + ".cs", StringComparison.Ordinal));
    }
}
