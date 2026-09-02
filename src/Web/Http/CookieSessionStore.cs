using Vuelto.Shared.Ui.Auth;

namespace Vuelto.Web.Http;

/// <summary>
/// Web <see cref="ISessionStore"/>: the refresh token is an HttpOnly cookie the browser
/// owns and attaches automatically, so there is nothing for the app to store. All
/// operations are no-ops and <see cref="UsesBodyTransport"/> is false, which keeps the
/// refresh call on the cookie path.
/// </summary>
public sealed class CookieSessionStore : ISessionStore
{
    public bool UsesBodyTransport => false;
    public Task<string?> GetRefreshTokenAsync() => Task.FromResult<string?>(null);
    public Task SaveRefreshTokenAsync(string refreshToken) => Task.CompletedTask;
    public Task ClearAsync() => Task.CompletedTask;
}
