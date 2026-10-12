using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace Vuelto.Api.Services;

/// <summary>
/// A short-lived, single-use opaque token backed by <see cref="IMemoryCache"/>: generate a
/// URL-safe random token bound to a payload, then consume it exactly once. The single-use
/// guarantee (consume removes the entry) lives here, in one tested place, instead of being
/// re-implemented per caller — see <c>LinkTokenService</c> and <c>NativeAuthCodeService</c>
/// (CONF-10).
/// </summary>
public sealed class SingleUseCacheToken<T>(IMemoryCache cache, string keyPrefix, TimeSpan lifetime)
{
    /// <summary>Issues a fresh token bound to <paramref name="value"/>, valid for the configured lifetime.</summary>
    public string Issue(T value)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        cache.Set(Key(token), value!, lifetime);
        return token;
    }

    /// <summary>
    /// Consumes the token: on success sets <paramref name="value"/> and removes the entry (so a
    /// replay returns false). Returns false for a blank/unknown/expired/already-used token.
    /// </summary>
    public bool TryConsume(string token, out T value)
    {
        value = default!;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var key = Key(token);
        if (!cache.TryGetValue(key, out T? cached))
            return false;

        cache.Remove(key); // single-use
        value = cached!;
        return true;
    }

    private string Key(string token) => $"{keyPrefix}:{token}";
}
