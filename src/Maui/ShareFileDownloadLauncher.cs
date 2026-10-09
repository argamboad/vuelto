using Vuelto.Shared.Ui;

namespace Vuelto.Maui;

/// <summary>
/// Native implementation (NATIVE-3): a WebView can't perform a browser download, so fetch the signed URL and hand
/// the bytes to the OS. On a phone (Android, iOS) and on the Mac that is the share sheet — the platform's own
/// save/share affordance. On Windows it is a <b>Save As</b> dialog starting in Downloads (#204, owner 2026-10-09: a
/// desktop saves a file into a folder; the share flyout made a PDF feel like a phone's), and cancelling it saves
/// nothing. Filename comes from the API's Content-Disposition (server-controlled), sanitized to a basename so a
/// header can never path-escape the cache directory or steer the dialog.
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

#if WINDOWS
        await SaveAsAsync(response, name);
#else
        var path = Path.Combine(FileSystem.CacheDirectory, name);
        await using (var file = File.Create(path))
            await response.Content.CopyToAsync(file);

        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = name,
            File = new ShareFile(path),
        });
#endif
    }

#if WINDOWS
    /// <summary>
    /// The WinUI save picker, parented to the app's window (an unpackaged WinUI app must hand the picker its HWND), on
    /// the UI thread. The file type is the download's own extension, so the dialog can't save a PDF as something else.
    /// </summary>
    private static async Task SaveAsAsync(HttpResponseMessage response, string name)
    {
        var target = await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads,
                SuggestedFileName = Path.GetFileNameWithoutExtension(name),
            };
            var extension = Path.GetExtension(name);
            if (string.IsNullOrEmpty(extension)) extension = ".dat";
            picker.FileTypeChoices.Add(extension.TrimStart('.').ToUpperInvariant(), [extension]);

            var window = Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
            if (window is not null)
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            return await picker.PickSaveFileAsync();
        });
        if (target is null) return; // cancelled: nothing saved, nothing to report

        await using var stream = await target.OpenStreamForWriteAsync();
        stream.SetLength(0); // replacing an existing file must not leave its tail behind
        await response.Content.CopyToAsync(stream);
    }
#endif
}
