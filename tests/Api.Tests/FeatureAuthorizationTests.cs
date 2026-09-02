using System.Net;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Perezosoft.Api.Configuration;
using Perezosoft.Api.Endpoints;
using Perezosoft.Api.Tests.Infrastructure;

namespace Perezosoft.Api.Tests;

/// <summary>
/// A feature group registered via <see cref="FeatureEndpointExtensions.MapTenantFeatureGroup"/> is
/// guarded by the shared <see cref="AuthPolicies.TenantApi"/> policy, so an unauthenticated request
/// is rejected with 401 — the slice can't accidentally ship an open endpoint (CONF-3 / B4-2). Runs
/// the REAL policy + helper through auth middleware in a minimal host.
/// </summary>
public class FeatureAuthorizationTests
{
    [Fact]
    public async Task TenantFeatureGroup_Unauthenticated_Returns401()
    {
        using var host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer()
               .ConfigureServices(services =>
               {
                   services.AddRouting();
                   services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                           .AddJwtBearer(o => o.TokenValidationParameters = new TestJwtSettings().CreateParameters());
                   services.AddTenantApiAuthorization();
               })
               .Configure(app =>
               {
                   app.UseRouting();
                   app.UseAuthentication();
                   app.UseAuthorization();
                   app.UseEndpoints(e =>
                       e.MapTenantFeatureGroup("/api/sample").MapGet("/", () => Results.Ok()));
               });
        }).StartAsync();

        var response = await host.GetTestServer().CreateClient().GetAsync("/api/sample/");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
