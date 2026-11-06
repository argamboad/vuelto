using Android.App;
using Perezosoft.Client.Maui;

namespace Vuelto.Maui;

/// <summary>
/// The colours behind Android's status and navigation bars, so the status bar reads as part of whatever
/// sits under it: the header (app.css <c>--bs-primary</c>, the header's background in both themes) on the
/// app's screens, the page itself (<c>--app-bg</c>) on the sign-in screens and the boot state, which have
/// no header. Change them with those tokens — REBRANDING.md §4 lists this file, and
/// <c>NativeChromeGateTests</c> fails while they disagree with app.css. The platform paints them
/// (Perezosoft.Platform.Maui's <see cref="SystemBars"/>, Pkg 4.3); MauiProgram hands it <see cref="Palette"/>.
/// </summary>
public static class SystemBarColors
{
    public const string Light = "#5A67D8";       // app.css :root --bs-primary (the header, both themes)
    public const string Dark = "#5A67D8";        // app.css :root --bs-primary — the dark block keeps the indigo
    public const string LightGround = "#F7F7FC"; // app.css :root --app-bg
    public const string DarkGround = "#14162A";  // app.css [data-bs-theme=dark] --app-bg

    public static readonly SystemBarPalette Palette = new(Light, Dark, LightGround, DarkGround);

    /// <summary>Paints the strip behind the status bar in this app's colours (MainActivity, before the page reports).</summary>
    public static void Paint(Activity? activity, bool dark, bool ground) => SystemBars.Paint(activity, Palette, dark, ground);
}
