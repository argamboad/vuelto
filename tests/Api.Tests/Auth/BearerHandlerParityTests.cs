namespace Vuelto.Api.Tests.Auth;

/// <summary>
/// v4 T32 (R126) + T49 (R102): the two hosts share ONE bearer handler, and it is the only place a client
/// builds an <c>Authorization: Bearer</c> header. Source scans over the three client trees — a host that
/// grew its own handler (unscoped, or without the renew-and-resend) is what these hold back.
/// </summary>
public class BearerHandlerParityTests
{
    [Theory]
    [InlineData("src/Web/Program.cs")]
    [InlineData("src/Maui/MauiProgram.cs")]
    public void EveryHost_InstallsTheSharedScopedHandler(string host)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), host));

        Assert.Contains("new BearerScopedHandler(", source);
        Assert.DoesNotContain("AuthHeaderHandler", source); // the per-host handlers are gone, not merely unused
    }

    [Fact]
    public void ThePerHostHandlers_NoLongerExist()
    {
        Assert.False(File.Exists(Path.Combine(RepoRoot(), "src", "Web", "Http", "AuthHeaderHandler.cs")));
        Assert.False(File.Exists(Path.Combine(RepoRoot(), "src", "Maui", "Auth", "NativeAuthHeaderHandler.cs")));
    }

    [Fact]
    public void OnlyTheSharedRetry_BuildsABearerHeader() // R102
    {
        // A Bearer header built anywhere else is a second, unscoped path for the tenant-scoped token to leave
        // the app — exactly how the native export download carried it to the file host (NAT-12).
        var allow = new Dictionary<string, string>
        {
            ["BearerRetry.cs"] = "the shared core: attach, renew once, resend",
            ["AuthService.cs"] = "the staff probe on the handler-less ApiAuth client: a relative API path, the in-memory token, no third party",
        };
        var roots = new[] { "Shared.Ui", "Web", "Maui" }.Select(d => Path.Combine(RepoRoot(), "src", d));
        var offenders = roots.SelectMany(r => Directory.EnumerateFiles(r, "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(r, "*.razor", SearchOption.AllDirectories)))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("\"Bearer\"", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Where(f => !allow.ContainsKey(f!))
            .ToList();
        Assert.True(offenders.Count == 0,
            $"Build the Bearer header only in BearerRetry (through BearerScopedHandler): {string.Join(", ", offenders)}");
    }

    [Fact]
    public void TheNativeDownloadLauncher_UsesAPlainClient()
    {
        // The presigned download must not go through the default (bearer) client: AWS refuses a presigned
        // request with an Authorization header, and the handler would spend a refresh on the file host's behalf.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Maui", "MauiProgram.cs"));
        Assert.Contains("new ShareFileDownloadLauncher(new HttpClient", source);
        Assert.DoesNotContain("new ShareFileDownloadLauncher(sp.GetRequiredService<HttpClient>()", source);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Vuelto.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root (Vuelto.slnx) not found above " + AppContext.BaseDirectory);
    }
}
