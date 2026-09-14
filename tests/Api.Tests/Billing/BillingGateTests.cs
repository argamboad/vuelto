using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Vuelto.Api.Tests.Infrastructure;

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

    [Fact]
    public async Task GateOn_TheProviderWebhook_Exists_AndRejectsAnUnsignedPayload()
    {
        var req = Request("POST", "/api/billing/webhook");
        req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        var res = await GatedOnClient().SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode); // reached the handler: invalid_signature
    }
}
