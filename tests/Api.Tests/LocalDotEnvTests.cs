using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Vuelto.Api.Tests;

/// <summary>
/// Test hosts never read the developer's .env (argamboad/perezosoft-platform#122). Program.cs loads the repo-root
/// .env for local development, and every WebApplicationFactory&lt;Program&gt; runs that line too: with a real local
/// .env the suite ran against the developer's settings (billing on, real Brevo SMTP) and disagreed with CI.
/// The test assembly now opts out before any host starts, and every .env load goes through LocalDotEnv, which
/// honours the opt-out.
/// </summary>
public class LocalDotEnvTests
{
    [Fact]
    public void TheTestAssembly_OptsOutOfTheDevelopersDotEnv() =>
        Assert.Equal("1", Environment.GetEnvironmentVariable(LocalDotEnv.SkipVariable));

    [Fact]
    public void EveryDotEnvLoad_GoesThroughLocalDotEnv()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var direct = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => Path.GetFileName(f) != "LocalDotEnv.cs")
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"DotNetEnv\.Env\.|\bEnv\.(Load|TraversePath)\("))
            .Select(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'))
            .ToList();
        Assert.True(direct.Count == 0, "Load .env through LocalDotEnv.Load (it honours SKIP_DOTENV): " + string.Join(", ", direct));
    }

    [Fact]
    public void Skipped_LoadsNothing_NotSkipped_LoadsTheNearestDotEnv()
    {
        var key = "LOCALDOTENV_PROBE_" + Guid.NewGuid().ToString("N");
        var root = Directory.CreateTempSubdirectory("localdotenv-");
        var nested = Directory.CreateDirectory(Path.Combine(root.FullName, "a", "b"));
        File.WriteAllText(Path.Combine(root.FullName, ".env"), $"{key}=from-dotenv\n");
        try
        {
            LocalDotEnv.Load(nested.FullName, skip: true);
            Assert.Null(Environment.GetEnvironmentVariable(key));

            LocalDotEnv.Load(nested.FullName, skip: false);   // walks up from a/b to the .env two levels above
            Assert.Equal("from-dotenv", Environment.GetEnvironmentVariable(key));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
            root.Delete(recursive: true);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root.");
    }
}

/// <summary>Runs before any test (and so before any test host builds Program): opt the whole assembly out.</summary>
internal static class SkipDotEnvForTests
{
    [ModuleInitializer]
    internal static void OptOut() => Environment.SetEnvironmentVariable(LocalDotEnv.SkipVariable, "1");
}
