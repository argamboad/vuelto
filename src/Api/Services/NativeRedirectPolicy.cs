namespace Perezosoft.Api.Services;

/// <summary>
/// Decides whether a native client's OAuth redirect target is safe to redirect to.
/// The native callback redirects to a client-supplied URL, so without this guard it
/// would be an open redirect. Only loopback HTTP (the desktop pattern, RFC 8252 §7.3)
/// and a configured custom scheme (mobile) are permitted.
/// </summary>
public static class NativeRedirectPolicy
{
    public static bool IsAllowed(string? redirect, string? customScheme)
    {
        if (string.IsNullOrWhiteSpace(redirect) || !Uri.TryCreate(redirect, UriKind.Absolute, out var uri))
            return false;

        // Desktop: http://127.0.0.1:{port} or http://localhost:{port}. HTTPS is not
        // allowed here — a loopback listener has no trusted cert.
        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
            return true;

        // Mobile: an app-registered custom scheme, only when one is configured.
        return !string.IsNullOrEmpty(customScheme)
            && string.Equals(uri.Scheme, customScheme, StringComparison.OrdinalIgnoreCase);
    }
}
