using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Perezosoft.Core.Abstractions;

namespace Perezosoft.Infrastructure.Http;

/// <summary>
/// Default <see cref="IOutboundUrlGuard"/>. In Development it is permissive (any http/https URL, so
/// local endpoints and loopback test servers work). Outside Development it enforces https-only and
/// resolves the host, rejecting the request if <em>any</em> resolved address is loopback, link-local,
/// private (RFC-1918 / IPv6 ULA), carrier-grade-NAT, or the cloud metadata endpoint — resolving at call
/// time so DNS rebinding to an internal host is caught (v2 audit GAP-2).
/// </summary>
public sealed class OutboundUrlGuard(IHostEnvironment environment) : IOutboundUrlGuard
{
    public async ValueTask<bool> IsAllowedAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (environment.IsDevelopment())
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps; // permissive locally

        if (uri.Scheme != Uri.UriSchemeHttps)
            return false; // https-only outside Development

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.Host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
        }
        catch
        {
            return false; // unresolvable host — refuse rather than guess
        }

        return addresses.Length > 0 && Array.TrueForAll(addresses, IsPublic);
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
