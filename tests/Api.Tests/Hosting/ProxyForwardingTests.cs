using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vuelto.Api.Configuration;

namespace Vuelto.Api.Tests.Hosting;

/// <summary>
/// DEPLOY-1 (ADR-017): the reverse-proxy correctness gate. Exercises <see cref="ProxyForwardingExtensions"/>
/// through a minimal TestServer with a trivial echo endpoint — asserting X-Forwarded-For/-Proto are honored
/// when <c>Proxy:Enabled</c> is on and IGNORED when off (the anti-spoofing default). No DB/Program needed;
/// this is a focused middleware test of the exact configuration the deployed app uses.
/// </summary>
public class ProxyForwardingTests
{
    /// <param name="peer">The connecting peer's IP (i.e. who the proxy would be). Null = TestServer default.</param>
    /// <param name="settings">Extra <c>Proxy:*</c> config, e.g. KnownNetworks / ForwardLimit.</param>
    private static async Task<HttpClient> BuildEchoServerAsync(
        bool proxyEnabled,
        string? peer = null,
        Dictionary<string, string?>? settings = null)
    {
        var config = new Dictionary<string, string?> { ["Proxy:Enabled"] = proxyEnabled ? "true" : "false" };
        foreach (var (k, v) in settings ?? [])
            config[k] = v;

        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureAppConfiguration(c => c.AddInMemoryCollection(config));
                web.ConfigureServices((ctx, services) => services.AddProxyForwarding(ctx.Configuration));
                web.Configure((ctx, app) =>
                {
                    // Stand in for the transport's peer address BEFORE the forwarded-headers middleware
                    // runs — that's the address KnownNetworks is matched against.
                    if (peer is not null)
                    {
                        app.Use(async (http, next) =>
                        {
                            http.Connection.RemoteIpAddress = IPAddress.Parse(peer);
                            await next();
                        });
                    }

                    app.UseProxyForwarding(ctx.Configuration);
                    app.Run(async http =>
                    {
                        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "";
                        await http.Response.WriteAsync($"{ip}|{http.Request.Scheme}");
                    });
                });
            })
            .StartAsync();

        return host.GetTestClient();
    }

    private static HttpRequestMessage ForwardedRequest()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("X-Forwarded-For", "203.0.113.7");
        req.Headers.Add("X-Forwarded-Proto", "https");
        return req;
    }

    [Fact]
    public async Task WhenEnabled_HonorsForwardedForAndProto()
    {
        var client = await BuildEchoServerAsync(proxyEnabled: true);

        var body = await (await client.SendAsync(ForwardedRequest())).Content.ReadAsStringAsync();

        Assert.Equal("203.0.113.7|https", body); // real client IP + proxied scheme
    }

    [Fact]
    public async Task WhenDisabled_IgnoresForwardedHeaders()
    {
        var client = await BuildEchoServerAsync(proxyEnabled: false);

        var body = await (await client.SendAsync(ForwardedRequest())).Content.ReadAsStringAsync();

        Assert.DoesNotContain("203.0.113.7", body); // spoofable IP not trusted
        Assert.EndsWith("|http", body);             // scheme unchanged
    }

    // ── v3 audit DEP-1 / ADM-10: the sole-ingress assumption, made explicit + narrowable ────────────
    // Default (no KnownNetworks) trusts ANY peer's X-Forwarded-For. That is correct behind a managed
    // proxy on a changing IP that is the ONLY route to the app — but a directly-reachable deployment
    // with Proxy:Enabled=true lets any client forge its source IP, defeating the per-IP passwordless
    // rate limiter (and, via ADM-3, the MFA attempt cap). Proxy:KnownNetworks narrows that trust.

    [Fact]
    public async Task KnownNetworks_IgnoresForwardedFor_FromAnUntrustedPeer()
    {
        // Trust only 10.0.0.0/8; the request arrives straight from a public client pretending to be a proxy.
        var client = await BuildEchoServerAsync(
            proxyEnabled: true,
            peer: "198.51.100.9",
            settings: new() { ["Proxy:KnownNetworks:0"] = "10.0.0.0/8" });

        var body = await (await client.SendAsync(ForwardedRequest())).Content.ReadAsStringAsync();

        // The forged header must NOT become the client IP — the rate limiter keys on the real peer.
        Assert.StartsWith("198.51.100.9|", body);
        Assert.DoesNotContain("203.0.113.7", body);
    }

    [Fact]
    public async Task KnownNetworks_HonorsForwardedFor_FromATrustedPeer()
    {
        var client = await BuildEchoServerAsync(
            proxyEnabled: true,
            peer: "10.1.2.3",
            settings: new() { ["Proxy:KnownNetworks:0"] = "10.0.0.0/8" });

        var body = await (await client.SendAsync(ForwardedRequest())).Content.ReadAsStringAsync();

        Assert.Equal("203.0.113.7|https", body); // real proxy ⇒ headers honored as before
    }

    [Fact]
    public async Task NoKnownNetworks_StillTrustsAnyPeer_ThePreservedSoleIngressDefault()
    {
        // Regression guard: the managed-proxy deploy (Render) depends on this. Narrowing must be opt-in.
        var client = await BuildEchoServerAsync(proxyEnabled: true, peer: "198.51.100.9");

        var body = await (await client.SendAsync(ForwardedRequest())).Content.ReadAsStringAsync();

        Assert.Equal("203.0.113.7|https", body);
    }

    [Fact]
    public async Task ForwardLimit_DefaultsToOne_SoOnlyTheNearestHopIsTrusted()
    {
        var client = await BuildEchoServerAsync(proxyEnabled: true, peer: "10.1.2.3");

        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        // A client-forged entry prepended to what the real proxy appended.
        req.Headers.Add("X-Forwarded-For", "1.1.1.1, 203.0.113.7");
        var body = await (await client.SendAsync(req)).Content.ReadAsStringAsync();

        Assert.StartsWith("203.0.113.7|", body); // rightmost (proxy-appended) wins; the forged one is not reached
    }

    [Fact]
    public async Task ForwardLimit_IsConfigurable_ForMultiProxyChains()
    {
        var client = await BuildEchoServerAsync(
            proxyEnabled: true,
            peer: "10.1.2.3",
            settings: new() { ["Proxy:ForwardLimit"] = "2" });

        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("X-Forwarded-For", "203.0.113.7, 10.9.9.9");
        var body = await (await client.SendAsync(req)).Content.ReadAsStringAsync();

        Assert.StartsWith("203.0.113.7|", body); // two hops consumed ⇒ the original client is reached
    }

    [Fact]
    public async Task InvalidKnownNetwork_FailsClosedAtStartup()
    {
        // A typo'd CIDR must not silently degrade to trust-everyone; the app refuses to start.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => BuildEchoServerAsync(
            proxyEnabled: true,
            settings: new() { ["Proxy:KnownNetworks:0"] = "not-a-cidr" }));

        Assert.Contains("Proxy:KnownNetworks", ex.ToString());
    }
}
