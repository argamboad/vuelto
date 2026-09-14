#if ANDROID || IOS || MACCATALYST
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Authentication;
using Vuelto.Shared.Ui.Auth;

namespace Vuelto.Maui.Auth;

/// <summary>
/// OAuth via a custom-scheme <see cref="WebAuthenticator"/> flow — Android, iOS, and macCatalyst.
/// A loopback HTTP listener (the Windows approach) can't work on Android — 127.0.0.1 is the phone
/// itself — so the API instead redirects to <c>{scheme}://auth?…</c>, which the OS routes back to
/// the app: on Android through the registered intent filter
/// (<c>WebAuthenticatorCallbackActivity</c>), on iOS/macCatalyst through the
/// <c>CFBundleURLTypes</c> scheme in Info.plist (WebAuthenticator drives
/// ASWebAuthenticationSession there). <see cref="WebAuthenticator"/> opens the platform browser
/// session, follows the provider round-trip, and surfaces the callback query.
/// </summary>
public sealed class WebAuthenticatorOAuthInitiator(
    string apiBaseUrl,
    string callbackScheme,
    ILogger<WebAuthenticatorOAuthInitiator> logger) : IOAuthInitiator
{
    public async Task<IReadOnlyDictionary<string, string>?> RunBrowserFlowAsync(string provider, string? linkToken = null, CancellationToken cancellationToken = default)
    {
        var callbackUrl = $"{callbackScheme}://auth";
        var loginUrl = $"{apiBaseUrl.TrimEnd('/')}/api/auth/native/login/{provider.ToLowerInvariant()}" +
                       $"?redirect={Uri.EscapeDataString(callbackUrl)}";
        if (!string.IsNullOrEmpty(linkToken))
            loginUrl += $"&link_token={Uri.EscapeDataString(linkToken)}";

        try
        {
            // WebAuthenticator has no cancellation of its own; dismissing the browser sheet already returns.
            // Cancel from the login page stops waiting on it (2026-09-14) — a late callback is then ignored.
            var authTask = WebAuthenticator.Default.AuthenticateAsync(new Uri(loginUrl), new Uri(callbackUrl));
            if (await Task.WhenAny(authTask, Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken)) != authTask)
            {
                logger.LogInformation("WebAuthenticator flow cancelled by the user");
                return null;
            }
            var result = await authTask;
            // result.Properties is the parsed callback query (code, or linked/error).
            return result.Properties;
        }
        catch (TaskCanceledException)
        {
            // User dismissed the browser session.
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WebAuthenticator flow failed for {Provider}", provider);
            return null;
        }
    }
}
#endif
