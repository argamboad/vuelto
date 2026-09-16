using Android.App;
using AndroidX.Core.View;
using Vuelto.Shared.Ui;

namespace Vuelto.Maui;

/// <summary>
/// The colours behind Android's status and navigation bars, so the status bar reads as part of whatever
/// sits under it: the header on the app's screens, the page itself (app.css <c>--app-bg</c>) on the
/// sign-in screens and the boot state, which have no header. The header is the brand indigo
/// (<c>--bs-primary</c>) in BOTH themes (AppHeader.razor.css), so <see cref="Light"/> and
/// <see cref="Dark"/> are the same colour and its icons are always light. Change them with those tokens —
/// REBRANDING.md lists both places, and NativeChromeGateTests holds them to app.css. The platform
/// template shipped a sage green here.
/// </summary>
public static class SystemBarColors
{
    public const string Light = "#5A67D8";       // app.css :root --bs-primary (the header, both themes)
    public const string Dark = "#5A67D8";        // app.css :root --bs-primary — the dark block keeps the indigo
    public const string LightGround = "#F7F7FC"; // app.css :root --app-bg
    public const string DarkGround = "#14162A";  // app.css [data-bs-theme=dark] --app-bg

    /// <summary>Paints the strip behind the status bar and picks icons that read on it.</summary>
    public static void Paint(Activity? activity, bool dark, bool ground)
    {
        if (activity?.Window is not { } window) return;

        var colour = (dark, ground) switch
        {
            (true, true) => DarkGround,
            (true, false) => Dark,
            (false, true) => LightGround,
            _ => Light,
        };
        activity.FindViewById(Android.Resource.Id.Content)?.SetBackgroundColor(Android.Graphics.Color.ParseColor(colour));

        var controller = WindowCompat.GetInsetsController(window, window.DecorView);
        if (controller is null) return;
        // Dark icons only on the light page ground; the indigo header takes light icons in both themes.
        controller.AppearanceLightStatusBars = ground && !dark;
        // The navigation bar sits over the page in every case (this app has no bottom tab bar).
        controller.AppearanceLightNavigationBars = !dark;
    }
}

/// <summary>Follows the page's theme (<see cref="Shared.Ui.Components.SystemBarThemeSync"/>).</summary>
public sealed class AndroidSystemBarTheme : ISystemBarTheme
{
    public Task ApplyAsync(string resolvedTheme, bool ground) =>
        MainThread.InvokeOnMainThreadAsync(() =>
            SystemBarColors.Paint(Platform.CurrentActivity, resolvedTheme == "dark", ground));
}
