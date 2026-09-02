namespace Vuelto.Api.Tests;

/// <summary>
/// QA-SEC-03 (2026-08-31 co-pilot finding F3): after sign-out, the browser Back button can restore
/// the last authenticated render from the back/forward cache — inert (refresh 401s, nav is dead),
/// but the stale view is readable on a shared device. The guard is an RCL script that forces a real
/// reload on a bfcache restore (`pageshow` with `persisted`), which re-runs auth and bounces a
/// signed-out visitor to /login. These gates pin the script's contract and its presence in BOTH
/// hosts' index.html (host-parity rule; R68 keeps the sets identical but not any one member).
/// </summary>
public class BfcacheGuardTests
{
    private const string ScriptRef = "_content/Vuelto.Shared.Ui/js/bfcache-guard.js";

    [Fact]
    public void BfcacheGuard_ReloadsPersistedPageshowRestores()
    {
        var js = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Shared.Ui", "wwwroot", "js", "bfcache-guard.js"));
        Assert.Contains("pageshow", js);
        Assert.Contains(".persisted", js);
        Assert.Contains("location.reload", js);
    }

    [Theory]
    [InlineData("Web")]
    [InlineData("Maui")]
    public void BfcacheGuard_IsLoadedByHostIndexHtml(string host)
    {
        var html = File.ReadAllText(Path.Combine(RepoRoot(), "src", host, "wwwroot", "index.html"));
        Assert.Contains(ScriptRef, html);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root.");
    }
}
