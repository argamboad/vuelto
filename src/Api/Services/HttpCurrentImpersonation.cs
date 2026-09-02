using Perezosoft.Api.Authentication;
using Perezosoft.Core.Abstractions;

namespace Perezosoft.Api.Services;

/// <summary>
/// Resolves <see cref="ICurrentImpersonation"/> from the authenticated principal's
/// <c>impersonated_by</c> claim (minted only by <see cref="JwtTokenService.IssueImpersonationToken"/>,
/// ADMIN-2/ADR-014). Null on a normal request and wherever there is no HTTP context (jobs, outbox) —
/// impersonation exists only as a request property. The audit log stamps this ambiently on every event
/// (v3 audit LB-ADM-1), mirroring how <see cref="HttpCurrentTenant"/> feeds the tenant filter.
/// </summary>
public sealed class HttpCurrentImpersonation(IHttpContextAccessor accessor) : ICurrentImpersonation
{
    public Guid? ImpersonatedBy => accessor.HttpContext?.User.GetImpersonatedBy();
}
