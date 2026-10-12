using Microsoft.AspNetCore.HttpOverrides;

namespace Vuelto.Api.Configuration;

/// <summary>
/// DEPLOY-1 (ADR-017): reverse-proxy correctness, config-gated. When the app runs behind a
/// TLS-terminating proxy (Render, nginx, …) it must honor the proxy's <c>X-Forwarded-For</c> /
/// <c>X-Forwarded-Proto</c> so the real client IP drives the per-IP passwordless rate limiter and the
/// <c>https</c> scheme drives OAuth redirect-URI generation. It is <b>off by default</b>: trusting these
/// headers when there is <i>no</i> proxy in front lets any client spoof its source IP (defeating the
/// rate limiter), so the gate must be turned on explicitly (<c>Proxy:Enabled</c>) only where a proxy
/// actually sets them.
/// <para>
/// <b>SOLE-INGRESS ASSUMPTION (v3 audit DEP-1/ADM-10) — read before enabling.</b> With
/// <c>Proxy:Enabled=true</c> and no <c>Proxy:KnownNetworks</c>, this trusts <i>any</i> peer's
/// <c>X-Forwarded-For</c>. That is deliberate and safe <b>only when the proxy is the app's ONLY route in</b>
/// — the managed-proxy case this was built for (Render fronts the app on an unknown, changing IP, so the
/// default loopback-only allowlist would ignore its headers, and the app's port isn't publicly reachable).
/// It is <b>not</b> safe if the app is also reachable directly: any client could then forge its source IP,
/// defeating the per-IP passwordless rate limiter and — through it — the MFA attempt cap (ADM-3).
/// </para>
/// <para>
/// If the app is directly reachable, or you front it with a proxy on known addresses, narrow the trust with
/// <c>Proxy:KnownNetworks</c> (CIDRs; headers honored only from a peer inside them). <c>Proxy:ForwardLimit</c>
/// (default 1) caps how many hops back the middleware walks — raise it only to the real number of proxies
/// in front. Both fail closed: a bad CIDR or a limit below 1 stops startup rather than widening trust.
/// </para>
/// </summary>
public static class ProxyForwardingExtensions
{
    private const string EnabledKey = "Proxy:Enabled";
    private const string KnownNetworksKey = "Proxy:KnownNetworks";
    private const string ForwardLimitKey = "Proxy:ForwardLimit";

    /// <summary>Configures forwarded-headers processing when <c>Proxy:Enabled</c> is true; otherwise a no-op.</summary>
    public static IServiceCollection AddProxyForwarding(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue(EnabledKey, false))
            return services;

        // Parse eagerly, at registration: a bad value must stop the app here rather than surface on the
        // first request (or, worse, degrade to trusting everyone).
        var knownNetworks = ParseKnownNetworks(configuration);
        var forwardLimit = configuration.GetValue(ForwardLimitKey, 1);
        if (forwardLimit < 1)
            throw new InvalidOperationException($"{ForwardLimitKey} must be at least 1 (got {forwardLimit}).");

        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            // How many proxy hops to walk back through, from the right. 1 = trust only the entry the
            // nearest proxy appended, so a client that pre-seeds its own X-Forwarded-For can't reach past
            // it. Raise ONLY to the real number of proxies in front of the app.
            o.ForwardLimit = forwardLimit;

            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();

            if (knownNetworks.Count > 0)
            {
                // Narrowed trust: headers are honored only from a peer inside these ranges.
                foreach (var network in knownNetworks)
                    o.KnownIPNetworks.Add(network);
            }

            // Otherwise both lists stay empty, which the forwarded-headers middleware reads as
            // "trust any peer" — see the SOLE-INGRESS note on this class for when that's safe.
        });
        return services;
    }

    /// <summary>
    /// Reads <c>Proxy:KnownNetworks</c> (CIDR strings). Fail-closed: an unparseable entry throws rather
    /// than being skipped, because silently dropping a range widens trust instead of narrowing it.
    /// </summary>
    private static List<System.Net.IPNetwork> ParseKnownNetworks(IConfiguration configuration)
    {
        var networks = new List<System.Net.IPNetwork>();
        foreach (var cidr in configuration.GetSection(KnownNetworksKey).Get<string[]>() ?? [])
        {
            if (string.IsNullOrWhiteSpace(cidr))
                continue;

            if (!System.Net.IPNetwork.TryParse(cidr.Trim(), out var network))
                throw new InvalidOperationException(
                    $"{KnownNetworksKey}: '{cidr}' is not a valid CIDR range (expected e.g. 10.0.0.0/8).");

            networks.Add(network);
        }
        return networks;
    }

    /// <summary>Applies <c>UseForwardedHeaders</c> when enabled — must run first, before anything reads the IP/scheme.</summary>
    public static IApplicationBuilder UseProxyForwarding(this IApplicationBuilder app, IConfiguration configuration)
    {
        if (configuration.GetValue(EnabledKey, false))
            app.UseForwardedHeaders();
        return app;
    }
}
