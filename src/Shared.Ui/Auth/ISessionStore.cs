namespace Vuelto.Shared.Ui.Auth;

/// <summary>
/// Abstracts where the refresh token lives, the one thing that differs between hosts.
/// <para>
/// Web: the token is an HttpOnly cookie the browser owns and attaches automatically,
/// so the store is a no-op and <see cref="UsesBodyTransport"/> is false — the refresh
/// call carries no body token and relies on the cookie.
/// </para>
/// <para>
/// Native (MAUI desktop/mobile): there is no shared cookie jar, so the token is kept
/// in the OS secure store and <see cref="UsesBodyTransport"/> is true — the refresh
/// call sends the token in the body and persists the rotated one from the response.
/// </para>
/// </summary>
public interface ISessionStore
{
    /// <summary>
    /// True when the client must carry the refresh token in the request body itself
    /// (native), false when the browser's HttpOnly cookie carries it (web).
    /// </summary>
    bool UsesBodyTransport { get; }

    /// <summary>The stored refresh token, or null. Always null for the cookie store.</summary>
    Task<string?> GetRefreshTokenAsync();

    /// <summary>Persists the rotated refresh token. No-op for the cookie store.</summary>
    Task SaveRefreshTokenAsync(string refreshToken);

    /// <summary>Clears the stored token on sign-out. No-op for the cookie store.</summary>
    Task ClearAsync();
}
