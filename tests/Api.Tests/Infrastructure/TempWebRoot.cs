namespace Perezosoft.Api.Tests.Infrastructure;

/// <summary>
/// A throwaway web root for the single-origin hosting tests (DEPLOY-1): a temp directory holding a
/// sentinel <c>index.html</c> (the SPA shell) and a <c>_framework/</c> asset, so the harness can prove the
/// API serves the WASM client + SPA fallback without depending on a real published bundle. Disposing
/// deletes the directory.
/// </summary>
public sealed class TempWebRoot : IDisposable
{
    /// <summary>Marker text embedded in the fake index.html so tests can assert "this is the SPA shell".</summary>
    public const string Sentinel = "<!--spa-shell-sentinel-->";

    public string Path { get; }

    private TempWebRoot(string path) => Path = path;

    public static TempWebRoot Create()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deploy1-webroot-" + Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(root, "_framework"));
        File.WriteAllText(System.IO.Path.Combine(root, "index.html"), $"<!DOCTYPE html><html><body>{Sentinel}</body></html>");
        File.WriteAllText(System.IO.Path.Combine(root, "_framework", "dotnet.js"), "// fake framework asset");
        return new TempWebRoot(root);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best-effort cleanup */ }
    }
}
