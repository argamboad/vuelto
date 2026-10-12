using Microsoft.Extensions.Caching.Memory;

namespace Vuelto.Api.Services;

/// <summary>
/// Short-lived, single-use tokens that carry "link this OAuth identity to
/// user X" through the provider round-trip. A browser navigation to the OAuth
/// challenge can't carry the JWT, hence the token.
/// </summary>
public interface ILinkTokenService
{
    string Issue(Guid userId);

    /// <summary>Returns the user id and consumes the token; null if unknown/expired/used.</summary>
    Guid? Redeem(string token);
}

public class LinkTokenService(IMemoryCache cache) : ILinkTokenService
{
    private readonly SingleUseCacheToken<Guid> _tokens = new(cache, "link-token", TimeSpan.FromMinutes(5));

    public string Issue(Guid userId) => _tokens.Issue(userId);

    public Guid? Redeem(string token) => _tokens.TryConsume(token, out var userId) ? userId : null;
}
