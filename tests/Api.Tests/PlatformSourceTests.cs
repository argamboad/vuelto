using System.Text.RegularExpressions;

namespace Vuelto.Api.Tests;

/// <summary>
/// Pkg 2.4 (ADR-034 §7): the local source switch. An app sets <c>PLATFORM_SOURCE=&lt;platform checkout&gt;</c> in its
/// <c>.env</c> (or <c>-p:PlatformSource=…</c>) and every <c>Perezosoft.Platform.*</c> package reference becomes a project
/// reference to that checkout (<c>build/PlatformSource.targets</c>). The switch finds each package's project through
/// <c>platform-packages.props</c>, the map the platform owns; a packable project missing from it would silently stay a
/// package while the developer believes they are building against source.
/// </summary>
public class PlatformSourceTests
{
    [Fact]
    public void EveryPackableProject_IsInThePackageMap_AtItsPath()
    {
        var root = RepoRoot();
        // The map and the packable projects are the publisher's (producer-only, Pkg 2.5): an app's switch reads the map
        // from the platform checkout it points at, never from its own tree, and an app packs nothing.
        if (!Architecture.PlatformOwnership.IsThePlatform(root)) return;
        var packable = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Select(f => (Path: Path.GetRelativePath(root, f).Replace('\\', '/'), File: f))
            .Where(p => !p.Path.Contains("/obj/", StringComparison.Ordinal) && !p.Path.Contains("/bin/", StringComparison.Ordinal)
                        && !p.Path.StartsWith(".claude/", StringComparison.Ordinal)) // other worktrees nested in a checkout
            .Select(p => (p.Path, Text: File.ReadAllText(p.File)))
            .Where(p => Regex.IsMatch(p.Text, @"<IsPackable>\s*true\s*</IsPackable>", RegexOptions.IgnoreCase))
            .Select(p => (p.Path, Id: Regex.Match(p.Text, @"<PackageId>\s*([^<\s]+)\s*</PackageId>").Groups[1].Value))
            .ToList();
        Assert.Contains(packable, p => p.Id == "Perezosoft.Platform.DryRun"); // probe: the release sample is packable

        var map = Regex.Matches(File.ReadAllText(Path.Combine(root, "platform-packages.props")),
                @"<PlatformPackage\s+Include=""([^""]+)""\s+Source=""([^""]+)""")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

        foreach (var (path, id) in packable)
        {
            Assert.False(string.IsNullOrEmpty(id), $"{path} is packable but declares no <PackageId>");
            Assert.StartsWith("Perezosoft.Platform.", id, StringComparison.Ordinal); // the source mapping and the switch key on it
            Assert.True(map.TryGetValue(id, out var mapped), $"{id} ({path}) is packable but missing from platform-packages.props — PlatformSource would leave it a package");
            Assert.Equal(path, mapped);
        }
        var stale = map.Keys.Except(packable.Select(p => p.Id)).ToList();
        Assert.True(stale.Count == 0, "platform-packages.props maps packages no project packs: " + string.Join(", ", stale));
    }

    [Fact]
    public void TheSwitch_ReadsItsPathFromDotEnv_AndNeverTouchesTheCommittedLockfile()
    {
        var root = RepoRoot();
        var props = File.ReadAllText(Path.Combine(root, "build", "PlatformSource.props"));
        Assert.Contains("PLATFORM_SOURCE", props, StringComparison.Ordinal);                 // the value lives in .env (ADR-001)
        Assert.DoesNotContain("<_DotEnv", props, StringComparison.Ordinal);                  // .env text never held in a property (binlogs)

        var targets = File.ReadAllText(Path.Combine(root, "build", "PlatformSource.targets"));
        Assert.Contains("platform-packages.props", targets, StringComparison.Ordinal);
        // Switched restores lock into obj/ and never in locked mode — in the .targets half, because Directory.Build.props
        // runs before the SDK defines obj/ and a lockfile would land in the project folder (found by the scratch-app proof).
        Assert.Contains("<NuGetLockFilePath>$(MSBuildProjectExtensionsPath)", targets, StringComparison.Ordinal);
        Assert.Contains("<RestoreLockedMode>false</RestoreLockedMode>", targets, StringComparison.Ordinal);
        Assert.DoesNotContain("NuGetLockFilePath", props, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Remove=", targets, StringComparison.Ordinal);
        Assert.Contains("<ProjectReference Include=", targets, StringComparison.Ordinal);

        Assert.Contains("build/PlatformSource.props", File.ReadAllText(Path.Combine(root, "Directory.Build.props")).Replace('\\', '/'), StringComparison.Ordinal);
        Assert.Contains("build/PlatformSource.targets", File.ReadAllText(Path.Combine(root, "Directory.Build.targets")).Replace('\\', '/'), StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root (CLAUDE.md) not found.");
    }
}
