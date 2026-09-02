using Perezosoft.Api.Configuration;
using Perezosoft.Api.Models;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;

namespace Perezosoft.Api.Services;

/// <summary>
/// An issued access session: the <see cref="TokenResponse"/> for the client plus the raw
/// refresh token. The caller delivers the refresh token by transport — an HttpOnly cookie
/// for the browser, or the response body (already on <see cref="TokenResponse.RefreshToken"/>)
/// for a native client.
/// </summary>
public record AccessSession(TokenResponse Response, string RefreshToken);

/// <summary>
/// Owns session establishment so the controller doesn't: issues the refresh token, resolves
/// the tenant claim, mints the JWT, and assembles the <see cref="TokenResponse"/>. This is the
/// single place the access-token shape is defined (previously duplicated across refresh, OTP,
/// and native-exchange endpoints).
/// </summary>
public interface ISessionService
{
    /// <summary>
    /// Issues a refresh token + JWT access token for an authenticated user.
    /// <paramref name="native"/> selects refresh-token transport: true puts it on the response
    /// body; false leaves the body token null (the caller sets the cookie from
    /// <see cref="AccessSession.RefreshToken"/>).
    /// </summary>
    Task<AccessSession> IssueAsync(User user, string provider, string ipAddress, bool native, CancellationToken cancellationToken = default);

    /// <summary>Resolves the user's tenant id + name via their membership (null when none).</summary>
    Task<(Guid? Id, string? Name)> ResolveTenantAsync(Guid userId, CancellationToken cancellationToken = default);
}

public class SessionService(
    IRefreshTokenService refreshTokenService,
    IJwtTokenService jwtTokenService,
    ITenantRepository tenants,
    IJwtSettings jwtSettings) : ISessionService
{
    public async Task<AccessSession> IssueAsync(User user, string provider, string ipAddress, bool native, CancellationToken cancellationToken = default)
    {
        var issued = await refreshTokenService.IssueRefreshTokenAsync(user.Id, ipAddress, provider, cancellationToken);
        var (tenantId, tenantName) = await ResolveTenantAsync(user.Id, cancellationToken);

        var accessToken = jwtTokenService.IssueAccessToken(
            user.Id, user.Email, provider, user.DisplayName, tenantName, user.Locale, tenantId, user.Theme);

        var response = new TokenResponse
        {
            AccessToken = accessToken,
            ExpiresIn = jwtSettings.ExpiryMinutes * 60,
            UserId = user.Id,
            Email = user.Email,
            RefreshToken = native ? issued.RawToken : null,
        };

        return new AccessSession(response, issued.RawToken);
    }

    public async Task<(Guid? Id, string? Name)> ResolveTenantAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var membership = await tenants.GetMembershipAsync(userId, cancellationToken);
        if (membership is null) return (null, null);
        var tenant = await tenants.GetByIdAsync(membership.TenantId, cancellationToken);
        return (membership.TenantId, tenant?.Name);
    }
}
