using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Perezosoft.Api.Tests.Infrastructure;

namespace Perezosoft.Api.Tests.Integration;

/// <summary>
/// DEPLOY-1 (ADR-017): proves single-origin hosting against the REAL app routing. When enabled, the API
/// serves the Blazor WASM client + framework assets and falls back to the SPA shell for client-side
/// routes — but an unmatched <c>/api/*</c> must stay an API-shaped 404, never the shell (the sharp edge).
/// Serving is config-gated OFF by default, so the platform's existing behavior is unchanged.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class SingleOriginHostingTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    /// <summary>A client on a host that serves the web client from <paramref name="webRoot"/>.</summary>
    private HttpClient CreateServingClient(TempWebRoot webRoot) =>
        _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Hosting:ServeWebClient", "true");
            b.UseWebRoot(webRoot.Path);
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Default_DoesNotServeTheWebClient()
    {
        // Off by default (additive): a client-side route has no server endpoint ⇒ 404, not an SPA shell.
        var res = await _factory.CreateClient().GetAsync("/some/client/route");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task WhenEnabled_UnknownNonApiRoute_ServesTheSpaShell()
    {
        using var web = TempWebRoot.Create();
        var res = await CreateServingClient(web).GetAsync("/settings");

        res.EnsureSuccessStatusCode();
        Assert.Contains(TempWebRoot.Sentinel, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WhenEnabled_FrameworkAssets_AreServed()
    {
        using var web = TempWebRoot.Create();
        var res = await CreateServingClient(web).GetAsync("/_framework/dotnet.js");

        res.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task WhenEnabled_UnknownApiRoute_Is404_NotTheSpaShell()
    {
        using var web = TempWebRoot.Create();
        var res = await CreateServingClient(web).GetAsync("/api/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.DoesNotContain(TempWebRoot.Sentinel, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WhenEnabled_KnownApiRoute_StillAuthenticates_NotShadowed()
    {
        using var web = TempWebRoot.Create();
        // A protected API route without a token must still 401 from the API — not be swallowed by the shell.
        var res = await CreateServingClient(web).GetAsync("/api/notifications");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // --- v3 DEP-2/DEP-3: the browser HTML host ships security + cache headers ---

    [Fact]
    public async Task WhenEnabled_SpaShell_CarriesSecurityHeaders_AndIsNoCache()
    {
        using var web = TempWebRoot.Create();
        var res = await CreateServingClient(web).GetAsync("/settings"); // a client route → the shell
        res.EnsureSuccessStatusCode();

        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("strict-origin-when-cross-origin", res.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("no-cache", res.Headers.CacheControl!.ToString()); // shell must revalidate (integrity)
    }

    [Fact]
    public async Task WhenEnabled_FrameworkAssets_AreImmutablyCacheable()
    {
        using var web = TempWebRoot.Create();
        var res = await CreateServingClient(web).GetAsync("/_framework/dotnet.js");
        res.EnsureSuccessStatusCode();

        var cache = res.Headers.CacheControl!;
        Assert.True(cache.Public);
        Assert.Contains("immutable", cache.ToString());
    }

    [Fact]
    public async Task WhenDisabled_NoSecurityHeadersAdded()
    {
        // The headers ride with the SPA host; an API-only deployment is unchanged.
        var res = await _factory.CreateClient().GetAsync("/api/does-not-exist");
        Assert.False(res.Headers.Contains("X-Frame-Options"));
    }
}
