using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Vuelto.Maui.Auth;
using Vuelto.Shared.Ui.Auth;

namespace Vuelto.Maui.Platforms.Android;

/// <summary>
/// Receives the OAuth callback redirect (<c>vuelto://auth?…</c>) and hands it to
/// <see cref="Microsoft.Maui.Authentication.WebAuthenticator"/> to complete the flow.
/// The intent filter must match the scheme used by <c>WebAuthenticatorOAuthInitiator</c>
/// and the API's <c>Auth:Native:CallbackScheme</c>.
/// </summary>
[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = CallbackScheme)]
public class WebAuthenticatorCallbackActivity : Microsoft.Maui.Authentication.WebAuthenticatorCallbackActivity
{
    // Must equal MauiProgram.CallbackScheme and the API's Auth:Native:CallbackScheme.
    private const string CallbackScheme = "vuelto";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Process-death resilience (NATIVE-12): if Android killed the app while the user
        // was on the provider's page, this redirect cold-starts a fresh process where no
        // WebAuthenticator operation is pending — the base handling below would deliver
        // the URI to nobody, this activity would finish, and the one-time code would be
        // lost (the app "flashes open and closes"). Detect that (persisted in-flight
        // marker, but no flow awaiting in THIS process), stash the callback URI, and boot
        // MainActivity so AuthService.TryCompletePendingOAuthAsync can finish the sign-in
        // on startup. In the warm case the in-process flag is set and this is a no-op.
        var callbackUri = Intent?.DataString;
        if (!string.IsNullOrEmpty(callbackUri))
        {
            var auth = IPlatformApplication.Current?.Services.GetService<AuthService>();
            if (auth is { OAuthFlowInFlightInProcess: false })
            {
                var store = new PreferencesOAuthResumeStore();
                if (store.GetInFlight() is not null)
                {
                    store.SetPendingCallback(callbackUri);
                    var launch = new Intent(this, typeof(MainActivity));
                    launch.SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);
                    StartActivity(launch);
                }
            }
        }

        base.OnCreate(savedInstanceState);
    }
}
