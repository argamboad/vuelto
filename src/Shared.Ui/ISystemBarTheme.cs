namespace Vuelto.Shared.Ui;

/// <summary>
/// A host whose system bars are drawn by the OS rather than the page — the Android app's status and
/// navigation bars — and should match the theme the page is showing. The web host registers none.
/// </summary>
public interface ISystemBarTheme
{
    /// <summary>Paint the bars for <paramref name="resolvedTheme"/>, which is <c>"light"</c> or <c>"dark"</c>.</summary>
    /// <param name="ground">True when no header sits under the status bar (the sign-in screens, the boot
    /// state): the bar takes the page's ground colour rather than the header's surface.</param>
    Task ApplyAsync(string resolvedTheme, bool ground);
}
