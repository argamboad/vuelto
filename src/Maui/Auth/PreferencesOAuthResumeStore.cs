using Vuelto.Shared.Ui.Auth;

namespace Vuelto.Maui.Auth;

/// <summary>
/// <see cref="IOAuthResumeStore"/> over MAUI <see cref="Preferences"/>, so the OAuth
/// in-flight marker and a stashed callback URI survive the OS killing the process
/// mid-round-trip (NATIVE-12).
/// <para>
/// Plain Preferences rather than SecureStorage is deliberate: the values are single-use
/// and dead within minutes (the code's server TTL is 5 min and it is consumed on first
/// exchange), the store must be readable/writable synchronously from an Android
/// <c>Activity.OnCreate</c> on the cold-start path (SecureStorage is async-only), and
/// app-private SharedPreferences are already sandboxed per app.
/// </para>
/// </summary>
public sealed class PreferencesOAuthResumeStore : IOAuthResumeStore
{
    private const string ProviderKey = "oauth_resume_provider";
    private const string LinkTokenKey = "oauth_resume_link_token";
    private const string StartedTicksKey = "oauth_resume_started_utc_ticks";
    private const string CallbackKey = "oauth_resume_pending_callback";

    public void SetInFlight(OAuthFlowMarker marker)
    {
        Preferences.Default.Set(ProviderKey, marker.Provider);
        if (string.IsNullOrEmpty(marker.LinkToken))
            Preferences.Default.Remove(LinkTokenKey);
        else
            Preferences.Default.Set(LinkTokenKey, marker.LinkToken);
        Preferences.Default.Set(StartedTicksKey, marker.StartedUtc.UtcTicks);
    }

    public OAuthFlowMarker? GetInFlight()
    {
        var provider = Preferences.Default.Get<string?>(ProviderKey, null);
        if (string.IsNullOrEmpty(provider))
            return null;
        var linkToken = Preferences.Default.Get<string?>(LinkTokenKey, null);
        var startedTicks = Preferences.Default.Get(StartedTicksKey, 0L);
        return new OAuthFlowMarker(provider, linkToken, new DateTimeOffset(startedTicks, TimeSpan.Zero));
    }

    public void ClearInFlight()
    {
        Preferences.Default.Remove(ProviderKey);
        Preferences.Default.Remove(LinkTokenKey);
        Preferences.Default.Remove(StartedTicksKey);
    }

    public void SetPendingCallback(string callbackUri) =>
        Preferences.Default.Set(CallbackKey, callbackUri);

    public string? TakePendingCallback()
    {
        var value = Preferences.Default.Get<string?>(CallbackKey, null);
        Preferences.Default.Remove(CallbackKey);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
