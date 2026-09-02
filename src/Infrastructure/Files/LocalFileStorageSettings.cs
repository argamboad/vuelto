namespace Vuelto.Infrastructure.Files;

/// <summary>
/// Settings for <see cref="LocalDiskFileStorage"/> (ADR-010), bound from <c>Storage:Local</c>. When
/// <see cref="RootPath"/> is empty the impl falls back to a <c>storage/</c> directory under the app
/// base directory, so the dev/test path works with zero configuration.
/// </summary>
public sealed class LocalFileStorageSettings
{
    /// <summary>Filesystem root under which tenant-scoped objects are stored.</summary>
    public string RootPath { get; set; } = "";

    /// <summary>
    /// Absolute base URL of the API that serves <c>/api/files/{token}</c> (the API's own public
    /// origin, e.g. <c>https://localhost:7160</c>). When empty, <c>GetDownloadUrlAsync</c> returns a
    /// relative URL the caller resolves against the API origin.
    /// </summary>
    public string DownloadBaseUrl { get; set; } = "";
}
