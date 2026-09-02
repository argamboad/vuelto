using Perezosoft.Api.Configuration;

namespace Perezosoft.Api.Services;

/// <summary>
/// Manages refresh token cookie operations.
/// </summary>
public class CookieService(IRefreshTokenSettings settings, TimeProvider clock) : ICookieService
{
    private const string RefreshTokenCookieName = "refresh_token";

    public void SetRefreshTokenCookie(HttpResponse response, string token, HttpRequest request)
    {
        if (string.IsNullOrEmpty(token))
            throw new ArgumentException("Token cannot be empty", nameof(token));

        // Self-heal legacy orphans before issuing: an older build may have written refresh_token at
        // a broader path (Path=/). The browser keeps that orphan alongside the current Path=/api/auth
        // cookie and sends BOTH on /api/auth/refresh; the server reads only one, so a stale Path=/
        // cookie can silently shadow the live token and wedge sign-in (notably in Firefox, whose
        // cookie send-order surfaces it). Expiring the Path=/ cookie on every issue means the next
        // successful sign-in clears the orphan automatically — no manual cookie-clearing needed.
        ExpireLegacyRefreshTokenCookie(response);

        // SameSite=Lax: the client (https://localhost:7008) and API (https://localhost:7160)
        // are the same site (localhost), so the refresh fetch is a same-site request and the
        // cookie is sent. Lax/Strict require both ends to be HTTPS (schemeful same-site treats
        // http and https localhost as different sites). Do NOT use None here — None demands
        // Secure and is dropped whenever any leg isn't HTTPS, which silently breaks refresh.
        response.Cookies.Append(RefreshTokenCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/api/auth",
            // Injected clock so the cookie lifetime matches the server-side token expiry, which is also
            // computed from TimeProvider (v2 audit LOGIC-B3) — the two must not drift under a virtual clock.
            Expires = clock.GetUtcNow().AddDays(settings.ExpiryDays)
        });
    }

    public void DeleteRefreshTokenCookie(HttpResponse response)
    {
        response.Cookies.Delete(RefreshTokenCookieName, new CookieOptions
        {
            Path = "/api/auth"
        });
        // Note: don't also Delete at Path=/ here — ASP.NET dedupes Set-Cookie headers by name when
        // appending, so a second same-name delete would drop the /api/auth one. The legacy orphan is
        // instead swept by SetRefreshTokenCookie on the next sign-in (see ExpireLegacyRefreshTokenCookie).
    }

    // Expires any refresh_token cookie left at the root path by an older build, so a duplicate can't
    // shadow the real Path=/api/auth cookie. A no-op for clients that never had a legacy cookie.
    // Emitted alongside the new cookie (different path) so both Set-Cookie headers survive.
    private static void ExpireLegacyRefreshTokenCookie(HttpResponse response) =>
        response.Cookies.Delete(RefreshTokenCookieName, new CookieOptions { Path = "/" });

    public string? GetRefreshTokenFromCookies(HttpRequest request)
    {
        request.Cookies.TryGetValue(RefreshTokenCookieName, out var refreshToken);
        return refreshToken;
    }
}
