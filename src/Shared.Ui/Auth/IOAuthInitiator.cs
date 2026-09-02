namespace Vuelto.Shared.Ui.Auth;

/// <summary>
/// Performs the interactive, platform-specific part of native OAuth: open the system
/// browser at the API's native-login URL and capture the one-time code the API hands
/// back via the loopback (desktop) or custom-scheme (mobile) redirect.
/// <para>
/// Only the native hosts register an implementation. The browser host signs in by
/// full-page navigation instead, so it never resolves this service.
/// </para>
/// </summary>
public interface IOAuthInitiator
{
    /// <summary>
    /// Drives the system-browser round-trip for <paramref name="provider"/> (e.g.
    /// "Google", "Microsoft") and returns the query parameters the API redirected back
    /// with — <c>code</c> for sign-in, or <c>linked</c>/<c>error</c> for account linking
    /// when <paramref name="linkToken"/> is supplied. Null if the user cancelled or the
    /// flow timed out.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>?> RunBrowserFlowAsync(string provider, string? linkToken = null);
}
