using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Billing;

namespace Vuelto.Api.Tests.Billing;

/// <summary>
/// GATES-1 (ADR-027): billing is a config-gated surface, default OFF, so a deployment can run private
/// and free before it is published. The gate is deliberately stronger than a runtime check — the billing
/// controllers are removed from the MVC application model at startup, so with the gate off the routes do
/// not EXIST. That distinction is what these tests assert: an unauthenticated request to a route that
/// exists answers <b>401</b>, so a <b>404</b> on the same request is proof the route is gone rather than
/// merely refusing. Hiding the link in the client is cosmetic; the API is the authority.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class BillingGateTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    private HttpClient GatedOnClient() =>
        _factory.WithWebHostBuilder(b => b.UseSetting("Billing:Enabled", "true"))
            .CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static HttpRequestMessage Request(string method, string path) =>
        new(new HttpMethod(method), path);

    [Theory]
    [InlineData("GET", "/api/billing")]
    [InlineData("POST", "/api/billing/checkout")]
    [InlineData("POST", "/api/billing/portal")]
    public async Task GateOff_TenantBillingRoutes_DoNotExist(string method, string path)
    {
        var res = await _factory.CreateClient().SendAsync(Request(method, path));

        // 404, not 401 — the route is absent, not protected. (The harness boots with the gate at its
        // shipped default, which is off, so this also pins the default posture.)
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task GateOff_TheProviderWebhook_DoesNotExist()
    {
        // The webhook is anonymous and signature-authenticated, which is NOT a reason to accept it while
        // billing is off: with no provider account behind the deployment there is nothing to apply.
        var res = await _factory.CreateClient().SendAsync(Request("POST", "/api/billing/webhook"));

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/billing")]
    [InlineData("POST", "/api/billing/checkout")]
    [InlineData("POST", "/api/billing/portal")]
    public async Task GateOn_TenantBillingRoutes_Exist_AndAuthenticate(string method, string path)
    {
        var res = await GatedOnClient().SendAsync(Request(method, path));

        // Route present ⇒ the auth pipeline runs and refuses the tokenless caller. Anything but 404.
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // --- the proof the relaxed Stripe startup check rests on: with the gate off, NOTHING billing-shaped is mapped ---

    /// <summary>The prefixes a deployment opts into; each must be absent from the route table while its gate is off.</summary>
    private static readonly string[] GatedPrefixes = ["api/billing", "api/public", "api/apikeys", "api/webhooks"];

    private static List<string> MappedUnder(IServiceProvider services, string prefix) =>
        services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText?.Trim('/') ?? "")
            .Where(raw => raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void GateOff_NothingUnderTheGatedPrefixes_IsMapped() // v4 T45 (BILL-2, ADV-P4-6, R86)
    {
        // The old proof was an attribute scan over controllers whose CLASS route starts with api/billing. A
        // minimal-API group, an action-level absolute route or a controller named otherwise slipped past it
        // — the adversarial pass mapped a billing-prefixed group and got 200 while /api/billing gave 404. This
        // reads the REAL route table of the host booted at the shipped defaults (every gate off): whatever
        // maps a route under a gated prefix, by whatever mechanism, shows up here.
        foreach (var prefix in GatedPrefixes)
            Assert.True(MappedUnder(_factory.Services, prefix).Count == 0,
                $"{prefix}/* is mapped while its gate is off: {string.Join(", ", MappedUnder(_factory.Services, prefix))}");
    }

    [Fact]
    public void GateOn_TheBillingRoutes_AreMapped_AndTheOtherGatesStayClosed()
    {
        var services = _factory.WithWebHostBuilder(b => b.UseSetting("Billing:Enabled", "true")).Services;

        Assert.Contains("api/billing", MappedUnder(services, "api/billing"));
        Assert.Contains("api/billing/webhook", MappedUnder(services, "api/billing"));
        foreach (var prefix in GatedPrefixes.Where(p => p != "api/billing"))
            Assert.Empty(MappedUnder(services, prefix)); // one gate opens one surface
    }

    [Fact]
    public async Task GateOff_AdminComp_Returns404_BeforeTheStaffCheck() // v4 T45 (BILL-3, C10)
    {
        // The staff comp lives under api/admin, so the route-table proof above does not cover it, and while
        // billing is off it could still hand a tenant Pro — contradicting the "nobody holds ProFeature while
        // off" consequence of ADR-027. It answers 404 while off, before the staff check runs (a non-staff caller
        // sees the same 404 as staff would: the surface does not exist, rather than being refused).
        var user = await _factory.SeedUserAsync();
        var client = _factory.CreateClientFor(user);
        var body = new { plan_key = PlanKeys.Pro };

        var put = await client.PutAsJsonAsync($"/api/admin/tenants/{user.TenantId}/subscription", body);
        var delete = await client.DeleteAsync($"/api/admin/tenants/{user.TenantId}/subscription");
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        // With the gate on the same caller is refused by the staff check: the route is back.
        var on = GatedOnClient();
        on.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _factory.IssueAccessToken(user));
        Assert.Equal(HttpStatusCode.Forbidden, (await on.PutAsJsonAsync($"/api/admin/tenants/{user.TenantId}/subscription", body)).StatusCode);
    }

    [Fact]
    public async Task GateOn_TheProviderWebhook_Exists_AndRejectsAnUnsignedPayload()
    {
        var req = Request("POST", "/api/billing/webhook");
        req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        var res = await GatedOnClient().SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode); // reached the handler: invalid_signature
    }
}
