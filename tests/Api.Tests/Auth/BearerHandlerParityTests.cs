namespace Vuelto.Api.Tests.Auth;

/// <summary>
/// v4 T32 (LB-UI-15, R126): the two hosts' bearer handlers — the web <c>AuthHeaderHandler</c> and the MAUI
/// <c>NativeAuthHeaderHandler</c> — must behave the same, and neither can be exercised from Ui.Tests (one lives
/// in the WASM host, one in the MAUI host). So the behaviour lives once, in <c>Shared.Ui.Auth.BearerRetry</c>
/// (tested in Ui.Tests), and this holds both handlers to delegating to it rather than carrying their own copy
/// of "attach the token" that the retry-on-401 could silently miss.
/// </summary>
public class BearerHandlerParityTests
{
    [Theory]
    [InlineData("src/Web/Http/AuthHeaderHandler.cs")]
    [InlineData("src/Maui/Auth/NativeAuthHeaderHandler.cs")]
    public void EveryBearerHandler_SendsThroughTheSharedRetry(string handler)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), handler));

        Assert.Contains("BearerRetry.SendAsync(", source);
        Assert.DoesNotContain("GetFreshAccessTokenAsync", source); // the shared core attaches the token; no local copy
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Vuelto.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root (Vuelto.slnx) not found above " + AppContext.BaseDirectory);
    }
}
