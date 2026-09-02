using System.Xml.Linq;

namespace Perezosoft.Api.Tests;

/// <summary>
/// Localization resource parity (course lesson 3.5's guard, made real during the 2026-07 comprehensive
/// truth-up): every key in a neutral resx must have a value in each shipped translation, and no
/// translation may carry orphan keys the neutral file dropped. Without this, a key added EN-only
/// renders as the raw resource name (or falls back silently) for Spanish users — the drift is
/// invisible until a bilingual tester happens upon the one affected screen.
/// </summary>
public class ResourceParityTests
{
    public static TheoryData<string, string> ResxPairs => new()
    {
        { "src/Shared.Ui/Resources/AppStrings.resx", "src/Shared.Ui/Resources/AppStrings.es.resx" },
        { "src/Infrastructure/Email/EmailStrings.resx", "src/Infrastructure/Email/EmailStrings.es.resx" },
    };

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
