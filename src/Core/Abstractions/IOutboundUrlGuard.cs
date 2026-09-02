namespace Perezosoft.Core.Abstractions;

/// <summary>
/// Guards server-initiated HTTP to a client-supplied URL against SSRF (v2 audit GAP-2). Outside
/// Development it allows only <c>https</c> and rejects any URL whose host resolves to a loopback,
/// link-local, private (RFC-1918/ULA), carrier-grade-NAT, or cloud-metadata address — re-resolving
/// at call time so DNS rebinding can't sneak an internal target past a create-time check. In
/// Development it is permissive (http + loopback) so local endpoints and the test suite work.
/// Every outbound call to a tenant-supplied URL (webhook create + every send) must pass through this.
/// </summary>
public interface IOutboundUrlGuard
{
    /// <summary>True if the URL is well-formed and safe to POST to from the server right now.</summary>
    ValueTask<bool> IsAllowedAsync(string? url, CancellationToken cancellationToken = default);
}
