using System.Xml.Linq;

namespace Vuelto.Api.Tests;

/// <summary>
/// Localization resource parity (course lesson 3.5's guard, made real during the 2026-07 comprehensive
/// truth-up): every key in a neutral resx must have a value in each shipped translation, and no
/// translation may carry orphan keys the neutral file dropped. Without this, a key added EN-only
/// renders as the raw resource name (or falls back silently) for Spanish users — the drift is
/// invisible until a bilingual tester happens upon the one affected screen.
/// </summary>
public class ResourceParityTests
{
    public static TheoryData<string, string> ResxPairs()
    {
        var data = new TheoryData<string, string>
        {
            { "src/Shared.Ui/Resources/AppStrings.resx", "src/Shared.Ui/Resources/AppStrings.es.resx" },
            { "src/Infrastructure/Email/EmailStrings.resx", "src/Infrastructure/Email/EmailStrings.es.resx" },
        };
        foreach (var (neutral, translated) in App.AppAllowlists.ResxPairs) data.Add(neutral, translated); // the app's (Arch A1)
        return data;
    }

    [Theory]
    [MemberData(nameof(ResxPairs))]
    public void EveryNeutralKey_HasATranslation_AndNoOrphans(string neutralPath, string translatedPath)
    {
        var root = RepoRoot();
        var neutral = Keys(Path.Combine(root, neutralPath));
        var translated = Keys(Path.Combine(root, translatedPath));
        Assert.NotEmpty(neutral); // probe alive

        var missing = neutral.Except(translated).OrderBy(k => k).ToList();
        Assert.True(missing.Count == 0,
            $"Keys in {Path.GetFileName(neutralPath)} with no {Path.GetFileName(translatedPath)} value "
            + $"(Spanish users see raw names/fallbacks): {string.Join(", ", missing)}");

        var orphans = translated.Except(neutral).OrderBy(k => k).ToList();
        Assert.True(orphans.Count == 0,
            $"Orphan keys in {Path.GetFileName(translatedPath)} absent from the neutral file "
            + $"(dead translations — remove or restore the neutral key): {string.Join(", ", orphans)}");
    }

    public static TheoryData<string> AllResx()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.resx", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                              && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
            data.Add(Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'));
        return data;
    }

    [Theory]
    [MemberData(nameof(AllResx))]
    public void NoResx_DeclaresAKeyTwice(string path)
    {
        // v4 T9 (ADV-P4-11, R152): with two <data> entries of one name the build only warns (MSB3568) and the first
        // value wins, so a slice shows another slice's text in both languages. The set comparison above can't see
        // it (the duplicate collapses), so count the raw entries.
        var dupes = XDocument.Load(Path.Combine(RepoRoot(), path)).Root!
            .Elements("data")
            .GroupBy(d => d.Attribute("name")!.Value, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} ×{g.Count()}")
            .ToList();
        Assert.True(dupes.Count == 0, $"{path} declares these keys more than once (the first value silently wins): {string.Join(", ", dupes)}");
    }

    [Theory]
    [MemberData(nameof(AllResx))]
    public void EveryKey_IsNamespacedByItsFeature(string path)
    {
        // WAYS_OF_WORKING: "namespace your keys per feature" (Notes_Title, Notes_Empty). Two slices can only collide
        // on a key if one of them skipped the prefix, so the prefix is the rule and this holds it.
        var bare = XDocument.Load(Path.Combine(RepoRoot(), path)).Root!
            .Elements("data")
            .Select(d => d.Attribute("name")!.Value)
            .Where(k => !System.Text.RegularExpressions.Regex.IsMatch(k, @"^[A-Z][A-Za-z0-9]*_[A-Za-z0-9_]+$"))
            .ToList();
        Assert.True(bare.Count == 0, $"{path} has keys without a <Feature>_ prefix: {string.Join(", ", bare)}");
    }

    [Fact]
    public void DuplicateResourceNames_FailTheBuild()
    {
        // MSB3568 ("Duplicate resource name ... ignored") is an MSBuild warning, which TreatWarningsAsErrors does not
        // cover. Promote exactly that one: a blanket switch would also fail builds on the MAUI adb-reverse MSB3073.
        var props = XDocument.Load(Path.Combine(RepoRoot(), "Directory.Build.props"));
        var promoted = props.Descendants("MSBuildWarningsAsErrors").SelectMany(e => e.Value.Split(';', StringSplitOptions.TrimEntries)).ToList();
        Assert.Contains("MSB3568", promoted);
        Assert.DoesNotContain("MSB3073", promoted);
        Assert.Empty(props.Descendants("MSBuildTreatWarningsAsErrors"));
    }

    private static HashSet<string> Keys(string path) =>
        [.. XDocument.Load(path).Root!
            .Elements("data")
            .Select(d => d.Attribute("name")!.Value)];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Shared.Ui")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root.");
    }
}
