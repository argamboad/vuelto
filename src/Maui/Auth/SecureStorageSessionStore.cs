using Microsoft.Maui.Storage;
using Perezosoft.Shared.Ui.Auth;

namespace Perezosoft.Maui.Auth;

/// <summary>
/// Native <see cref="ISessionStore"/>: persists the refresh token in the OS secure
/// store (DPAPI-backed on Windows, Keychain/Keystore on Apple/Android). This is the
/// native equivalent of the web's HttpOnly cookie — it's what lets the desktop app
/// stay signed in across restarts. <see cref="UsesBodyTransport"/> is true, so the
/// refresh call carries the token in the body.
/// </summary>
public sealed class SecureStorageSessionStore : ISessionStore
{
    private const string RefreshTokenKey = "refresh_token";

    public bool UsesBodyTransport => true;

    public async Task<string?> GetRefreshTokenAsync()
    {
        try
        {
            return await SecureStorage.Default.GetAsync(RefreshTokenKey);
        }
        catch
        {
            // A corrupted/locked secure store reads as "no session" rather than crashing.
            return null;
        }
    }

    public Task SaveRefreshTokenAsync(string refreshToken) =>
        SecureStorage.Default.SetAsync(RefreshTokenKey, refreshToken);

    public Task ClearAsync()
    {
        SecureStorage.Default.Remove(RefreshTokenKey);
        return Task.CompletedTask;
    }
}
