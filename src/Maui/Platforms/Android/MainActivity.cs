using Android.App;
using Android.Content.PM;
using Android.Content.Res;
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

        // Android 15+ enforces edge-to-edge: the WebView is laid out under the transparent status bar,
        // and older Android WebViews resolve env(safe-area-inset-top) to 0 — so the clock and status
        // icons drew over the app header. Restore the classic layout natively: pad the content view down
        // by the status-bar/cutout inset. The bottom stays edge-to-edge (the gesture pill floats over
        // content; 3-button nav draws its own scrim).
        var content = FindViewById(Android.Resource.Id.Content)!;
        ViewCompat.SetOnApplyWindowInsetsListener(content, new TopInsetPaddingListener());

        // The strip behind the status bar matches what sits under it. Until the page reports its theme
        // (SystemBarThemeSync → AndroidSystemBarTheme), start from the phone's own night mode — the page's
        // "system" default reads the same thing — on the page ground, since the boot state has no header.
        var night = (Resources?.Configuration?.UiMode & UiMode.NightMask) == UiMode.NightYes;
        SystemBarColors.Paint(this, night, ground: true);
    }

    private sealed class TopInsetPaddingListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat? OnApplyWindowInsets(Android.Views.View? v, WindowInsetsCompat? insets)
        {
            if (v is null || insets is null)
                return insets;
            var bars = insets.GetInsets(WindowInsetsCompat.Type.StatusBars() | WindowInsetsCompat.Type.DisplayCutout())!;
            v.SetPadding(bars.Left, bars.Top, bars.Right, 0);

            // Spent here, so the WebView is not told about them too. Current Android WebViews DO report
            // the status-bar inset to CSS, and the header's env(safe-area-inset-top) then added the same
            // height again below this padding — an empty band above every screen (2026-09-16). The
            // navigation-bar inset still reaches the page.
            return new WindowInsetsCompat.Builder(insets)
                .SetInsets(WindowInsetsCompat.Type.StatusBars(), AndroidX.Core.Graphics.Insets.None!)!
                .SetInsets(WindowInsetsCompat.Type.DisplayCutout(), AndroidX.Core.Graphics.Insets.None!)!
                .Build();
        }
    }
}
