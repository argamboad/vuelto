namespace Perezosoft.Shared.Ui;

/// <summary>
/// Relays "the app came back to the foreground" to interested pages (NATIVE-4). On native, an
/// external round-trip (billing checkout/portal in the system browser) returns to the app with no
/// navigation — the page a user left is still there, stale — so MAUI wires the window's Resumed
/// lifecycle event to <see cref="Notify"/>. On web the provider redirects back into the app
/// (a fresh page load), so the web host registers this but never calls <see cref="Notify"/>.
/// </summary>
public class AppResumeNotifier
{
    public event Action? Resumed;

    public void Notify() => Resumed?.Invoke();
}
