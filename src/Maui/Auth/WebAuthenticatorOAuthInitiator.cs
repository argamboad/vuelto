#if ANDROID || IOS || MACCATALYST
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Authentication;
using Perezosoft.Shared.Ui.Auth;

namespace Perezosoft.Maui.Auth;

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
    public async Task<IReadOnlyDictionary<string, string>?> RunBrowserFlowAsync(string provider, string? linkToken = null)
    {
        var callbackUrl = $"{callbackScheme}://auth";
        var loginUrl = $"{apiBaseUrl}/api/auth/native/login/{provider.ToLowerInvariant()}" +
                       $"?redirect={Uri.EscapeDataString(callbackUrl)}";
        if (!string.IsNullOrEmpty(linkToken))
            loginUrl += $"&link_token={Uri.EscapeDataString(linkToken)}";

        try
        {
            var result = await WebAuthenticator.Default.AuthenticateAsync(
                new Uri(loginUrl), new Uri(callbackUrl));
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
