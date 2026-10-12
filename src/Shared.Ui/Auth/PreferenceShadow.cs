namespace Vuelto.Shared.Ui.Auth;

/// <summary>
/// A preference the user just saved to the account, which the access token in memory predates (v4 audit T57,
/// pulled out of <see cref="AuthService"/>). The reconcile in MainLayout reads Theme/Locale, and the native app
/// keeps this token across a WebView reload (a language change reloads), so without these it re-applied the OLD
/// value (2026-09-16). Remembered rather than fetched: refreshing the session instead rotated
/// the web's refresh cookie while a reload was already under way, which came back signed out. Dropped with the
/// token, and when a different account's token arrives.
/// </summary>
internal sealed class PreferenceShadow
{
    public string? Theme { get; set; }
    public string? Locale { get; set; }

    public void Forget()
    {
        Theme = null;
        Locale = null;
    }
}
