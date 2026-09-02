using Microsoft.JSInterop;

namespace Perezosoft.Shared.Ui;

/// <summary>
/// Web implementation: a same-tab navigation. Because signed files are served with
/// <c>Content-Disposition: attachment</c>, the browser downloads (naming the file from the header)
/// and the page stays put — no <c>target="_blank"</c>, so no popup-blocker exposure and no blank
/// tab flash. The server names the file, so <c>fallbackFileName</c> is unused here.
/// </summary>
public class BrowserFileDownloadLauncher(IJSRuntime js) : IFileDownloadLauncher
{
    public async Task LaunchAsync(string url, string fallbackFileName) =>
        await js.InvokeVoidAsync("window.location.assign", url);
}
