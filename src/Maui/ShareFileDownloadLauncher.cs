using Perezosoft.Shared.Ui;

namespace Perezosoft.Maui;

/// <summary>
/// Native implementation (NATIVE-3): a WebView can't perform a browser download, so fetch the
/// signed URL, stage the bytes in the app cache, and open the OS share sheet — the platform's
/// save/share affordance on all four targets (Android sheet, Windows share flyout, iOS/macOS
/// share). Filename comes from the API's Content-Disposition (server-controlled), sanitized to
/// a basename so a header can never path-escape the cache directory.
/// </summary>
public class ShareFileDownloadLauncher(HttpClient http) : IFileDownloadLauncher
{
    public async Task LaunchAsync(string url, string fallbackFileName)
    {
        using var response = await http.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var name = response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        if (string.IsNullOrWhiteSpace(name))
            name = fallbackFileName;
        name = Path.GetFileName(name);

        var path = Path.Combine(FileSystem.CacheDirectory, name);
        await using (var file = File.Create(path))
            await response.Content.CopyToAsync(file);

        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = name,
            File = new ShareFile(path),
        });
    }
}
