namespace Vuelto.Api;

/// <summary>
/// Loads the repo-root <c>.env</c> for local development (ADR-001: the single local source of truth) by walking up
/// from the working directory to the nearest one; a no-op when there is none (CI, production).
/// <para>
/// <c>SKIP_DOTENV=1</c> opts out. The test assembly sets it before any test host starts, because every
/// <c>WebApplicationFactory&lt;Program&gt;</c> runs Program's top-level statements: with a real local <c>.env</c> the
/// suite otherwise ran against the developer's settings (billing on, real SMTP) and disagreed with CI
/// (argamboad/perezosoft-platform#122). Every <c>.env</c> load goes through here, held by
/// <c>LocalDotEnvTests.EveryDotEnvLoad_GoesThroughLocalDotEnv</c>.
/// </para>
/// </summary>
public static class LocalDotEnv
{
    public const string SkipVariable = "SKIP_DOTENV";

    public static void Load() =>
        Load(Directory.GetCurrentDirectory(), Environment.GetEnvironmentVariable(SkipVariable) == "1");

    internal static void Load(string startDirectory, bool skip)
    {
        if (skip) return;
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, ".env");
            if (!File.Exists(file)) continue;
            try { DotNetEnv.Env.Load(file); } catch { /* unreadable .env: behave as if absent, as before */ }
            return;
        }
    }
}
