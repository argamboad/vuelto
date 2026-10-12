using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vuelto.Api.Configuration;
using Vuelto.Api.Endpoints;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Billing;

namespace Vuelto.Api.Tests.Billing;

/// <summary>
/// Drives BILLING-1's gate (ADR-006): <c>.RequireEntitlement(...)</c> returns <b>402 Payment
/// Required</b> (not 403/500), naming the entitlement, when the tenant lacks it — and lets the request
/// through when granted. Runs the real endpoint filter through a minimal host with a stub entitlement
/// service (the fail-closed resolution itself is covered by <see cref="EntitlementServiceTests"/>).
/// </summary>
public class RequireEntitlementFilterTests
{
    [Fact]
    public async Task DeniedEntitlement_Returns402_NamingTheEntitlement()
    {
        using var host = await HostWith(entitled: false);

        var response = await host.GetTestServer().CreateClient().GetAsync("/gated");

        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("payment_required", body);
        Assert.Contains(Entitlements.ProFeature, body);
    }

    [Fact]
    public async Task GrantedEntitlement_AllowsTheRequestThrough()
    {
        using var host = await HostWith(entitled: true);

        var response = await host.GetTestServer().CreateClient().GetAsync("/gated");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<IHost> HostWith(bool entitled) =>
        new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer()
               .ConfigureServices(services =>
               {
                   services.AddRouting();
                   services.AddSingleton<IEntitlementService>(new StubEntitlements(entitled));
                   services.AddSingleton<IApplicationSettings>(new TestAppSettings());
               })
               .Configure(app =>
               {
                   app.UseRouting();
                   app.UseEndpoints(e =>
                       e.MapGet("/gated", () => Results.Ok("ok")).RequireEntitlement(Entitlements.ProFeature));
               });
        }).StartAsync();
}

internal sealed class StubEntitlements(bool result) : IEntitlementService
{
    public Task<bool> HasAsync(string entitlementKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(result);
}
