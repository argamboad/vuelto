using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Vuelto.Core.Abstractions;

namespace Vuelto.Infrastructure.Http;

/// <summary>
/// Default <see cref="IOutboundUrlGuard"/>. In Development it is permissive (any http/https URL, so
/// local endpoints and loopback test servers work). Outside Development it enforces https-only and
/// resolves the host, rejecting the request if <em>any</em> resolved address is loopback, link-local,
/// private (RFC-1918 / IPv6 ULA), carrier-grade-NAT, or the cloud metadata endpoint — resolving at call
/// time so DNS rebinding to an internal host is caught (v2 audit GAP-2). The webhook client also asks it at
/// connect time (<see cref="ResolveAllowedAsync"/>, v4 audit H8), so the address dialed is the address checked.
/// </summary>
/// <param name="resolve">DNS lookup; the system resolver unless a test substitutes one.</param>
public sealed class OutboundUrlGuard(
    IHostEnvironment environment,
    Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null) : IOutboundUrlGuard
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve = resolve ?? Dns.GetHostAddressesAsync;

    public async ValueTask<bool> IsAllowedAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (environment.IsDevelopment())
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps; // permissive locally

        if (uri.Scheme != Uri.UriSchemeHttps)
            return false; // https-only outside Development

        return (await ResolveAllowedAsync(uri.Host, cancellationToken)).Length > 0;
    }

    public async ValueTask<IPAddress[]> ResolveAllowedAsync(string host, CancellationToken cancellationToken = default)
    {
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
                ? [literal]
                : await _resolve(host, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return []; // unresolvable host — refuse rather than guess
        }

        if (environment.IsDevelopment())
            return addresses; // permissive locally (loopback test servers)

        // Every answer must be public: one internal address among public ones is still a way in.
        return addresses.Length > 0 && Array.TrueForAll(addresses, IsPublic) ? addresses : [];
    }

    private static bool IsPublic(IPAddress address)
    {
        var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            return false;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 10                                   // 10.0.0.0/8
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)      // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 168)                   // 192.168.0.0/16
                || (b[0] == 169 && b[1] == 254)                   // 169.254.0.0/16 (link-local incl. metadata)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)     // 100.64.0.0/10 (CGNAT)
                || b[0] == 0);                                    // 0.0.0.0/8
        }

        // IPv6: reject link-local (fe80::/10), unique-local (fc00::/7), and unspecified.
        return !(ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || ip.Equals(IPAddress.IPv6None));
    }
}
