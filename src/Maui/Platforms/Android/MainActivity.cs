using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Core.View;

namespace Vuelto.Maui;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Android 15+ enforces edge-to-edge: the WebView is laid out under the transparent status
        // bar, and Android WebView resolves env(safe-area-inset-top) to 0 (the CSS guard in app.css
        // only works on iOS WKWebView) — so the clock/status icons drew over the app header.
        // Restore the classic layout natively: pad the content view down by the status-bar/cutout
        // inset and paint the strip brand green so it reads as the header's backdrop. Bottom stays
        // edge-to-edge (the gesture pill floats over content; 3-button nav draws its own scrim).
        var content = FindViewById(Android.Resource.Id.Content)!;
        content.SetBackgroundColor(Android.Graphics.Color.ParseColor("#6B8A72"));
        ViewCompat.SetOnApplyWindowInsetsListener(content, new TopInsetPaddingListener());
        // Brand green is dark enough for the default light (white) status icons — pin that.
        WindowCompat.GetInsetsController(Window!, Window!.DecorView)!.AppearanceLightStatusBars = false;
    }

    private sealed class TopInsetPaddingListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat? OnApplyWindowInsets(Android.Views.View? v, WindowInsetsCompat? insets)
        {
            if (v is null || insets is null)
                return insets;
            var bars = insets.GetInsets(WindowInsetsCompat.Type.StatusBars() | WindowInsetsCompat.Type.DisplayCutout())!;
            v.SetPadding(bars.Left, bars.Top, bars.Right, 0);
            return insets; // not consumed — MAUI/IME handling below stays intact
        }
    }
}
