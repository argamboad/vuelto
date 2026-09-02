namespace Perezosoft.Api.Services;

/// <summary>
/// Builds the two URLs in the native OAuth round-trip, in one testable place. Both carry an optional
/// client-generated CSRF <c>state</c> nonce (v3 NAT-9): the app puts it on the login URL, the API threads
/// it into the provider callback and echoes it back on the redirect to the client, and the app rejects any
/// callback whose state doesn't match. Without it the loopback listener (RFC 8252 desktop pattern) would
/// accept any <c>?code=</c> that reaches its port — a local process or a malicious page could inject an
/// attacker's code and sign the user into the attacker's account. <c>state</c> is optional end-to-end, so a
/// client build predating NAT-9 still works (nothing is echoed, nothing is validated).
/// </summary>
public static class NativeAuthUrls
{
    /// <summary>
    /// The internal callback the provider redirects to after consent — carries the client's loopback/scheme
    /// <paramref name="redirect"/> plus the optional link token and CSRF state, so both survive the round-trip.
    /// </summary>
    public static string Callback(string provider, string redirect, string? linkToken, string? state)
    {
        var url = $"/api/auth/native/callback/{provider}?redirect={Uri.EscapeDataString(redirect)}";
        if (!string.IsNullOrEmpty(linkToken))
            url += $"&link_token={Uri.EscapeDataString(linkToken)}";
        if (!string.IsNullOrEmpty(state))
            url += $"&state={Uri.EscapeDataString(state)}";
        return url;
    }

    /// <summary>
    /// The redirect back to the native client: the outcome (<c>code</c>/<c>error</c>/<c>linked</c>) plus the
    /// echoed CSRF <paramref name="state"/> the client validates. State is omitted when the client sent none.
    /// </summary>
    public static string ClientRedirect(string redirect, string outcomeKey, string outcomeValue, string? state)
    {
        var url = AppendQuery(redirect, outcomeKey, outcomeValue);
        return string.IsNullOrEmpty(state) ? url : AppendQuery(url, "state", state);
    }

    private static string AppendQuery(string url, string key, string value)
    {
        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}{key}={Uri.EscapeDataString(value)}";
    }
}
