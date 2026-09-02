namespace Vuelto.Core.Abstractions;

/// <summary>
/// Ambient impersonation attribution (ADMIN-2, ADR-014 / v3 audit LB-ADM-1): when the current request is
/// authenticated with an impersonation token ("sign in as", the JWT carries an <c>impersonated_by</c>
/// claim), this exposes the REAL actor — the staff user who minted the session. Null on a normal request,
/// and in tenant-less system contexts (jobs, outbox) where there is no request at all.
/// <para>
/// The audit log reads this ambiently and stamps <c>AuditEvent.ImpersonatedBy</c> on every event, so a
/// write performed during an impersonation window is durably distinguishable from the user acting alone —
/// no per-call-site parameter to forget. Mirrors the <see cref="ICurrentTenant"/> accessor pattern.
/// </para>
/// </summary>
public interface ICurrentImpersonation
{
    /// <summary>The staff user actually driving this request via impersonation, or null when not impersonating.</summary>
    Guid? ImpersonatedBy { get; }
}
