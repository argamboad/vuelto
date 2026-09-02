namespace Perezosoft.Api.Services;

/// <summary>
/// Manages refresh token cookie configuration.
/// Separates cookie concerns from the controller.
/// </summary>
public interface ICookieService
{
    /// <summary>
    /// Sets the refresh token cookie on the response. The value is the raw token;
    /// only its hash is stored server-side.
    /// </summary>
    void SetRefreshTokenCookie(HttpResponse response, string token, HttpRequest request);

    void DeleteRefreshTokenCookie(HttpResponse response);

    string? GetRefreshTokenFromCookies(HttpRequest request);
}
