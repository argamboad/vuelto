using Perezosoft.Core.Abstractions;

namespace Perezosoft.Api.Services;

/// <summary>
/// Resolves <see cref="ICurrentTenant"/> from the authenticated principal's <c>tenant_id</c> claim
/// (issued in the JWT), and implements <see cref="ITenantContext"/> so system/integration operations
/// without a JWT can enter a <b>trusted</b> tenant context (e.g. a signature-verified webhook). An
/// entered tenant takes precedence over the JWT claim for the duration of its scope, so the entered
/// operation gets the same write-stamping + read-filtering protection an authenticated request gets.
/// Registered request-scoped; returns null when there is no entered tenant and no authenticated tenant
/// claim, which makes tenant-scoped queries return nothing (fail closed).
/// </summary>
public sealed class HttpCurrentTenant(IHttpContextAccessor accessor) : ICurrentTenant, ITenantContext
{
    private Guid? _entered;

    public Guid? TenantId =>
        _entered
        ?? (Guid.TryParse(accessor.HttpContext?.User.FindFirst(JwtClaims.TenantId)?.Value, out var id) ? id : null);

    public IDisposable EnterTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Cannot enter the empty tenant — that is the system/no-tenant context.", nameof(tenantId));

        var previous = _entered;
        _entered = tenantId;
        return new TenantScope(this, previous);
    }

    /// <summary>Restores the previously-current tenant on dispose; supports nesting.</summary>
    private sealed class TenantScope(HttpCurrentTenant owner, Guid? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner._entered = previous;
        }
    }
}
