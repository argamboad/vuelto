namespace Vuelto.Shared.Ui;

/// <summary>
/// Hands a signed download URL (FILES-2) to the host's download affordance. A browser downloads it
/// natively (the API serves signed files as <c>Content-Disposition: attachment</c>, so a same-tab
/// navigation never replaces the page); a WebView can't — MAUI fetches the bytes and opens the OS
/// share sheet instead (NATIVE-3). <paramref name="fallbackFileName"/> is used only when the
/// response carries no filename.
/// </summary>
public interface IFileDownloadLauncher
{
    Task LaunchAsync(string url, string fallbackFileName);
}
