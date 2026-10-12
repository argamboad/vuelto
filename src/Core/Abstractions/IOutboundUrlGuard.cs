using System.Net;

namespace Vuelto.Core.Abstractions;

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

    /// <summary>
    /// The addresses a connection to <paramref name="host"/> may dial right now — empty when the host doesn't
    /// resolve or resolves to any address the guard refuses. Called at connect time (v4 audit H8), so the address
    /// the socket dials is the one that was checked: a name that answered "public" to <see cref="IsAllowedAsync"/>
    /// and "loopback" a moment later can't slip through between the two lookups.
    /// </summary>
    ValueTask<IPAddress[]> ResolveAllowedAsync(string host, CancellationToken cancellationToken = default);
}
