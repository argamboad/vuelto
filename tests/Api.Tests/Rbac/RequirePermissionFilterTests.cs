using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Perezosoft.Api.Endpoints;
using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Authorization;

namespace Perezosoft.Api.Tests.Rbac;

/// <summary>
/// Drives RBAC-1's minimal-API enforcement path (ADR-009): <c>.RequirePermission(...)</c> returns
/// <b>403 Forbidden</b> (authorization — not 402 payment) when the caller's role lacks the permission,
/// and lets the request through when it's granted. Mirrors <c>RequireEntitlementFilterTests</c>: runs
/// the real endpoint filter through a minimal host with a stub permission service (the role→permission
/// resolution itself is covered by <see cref="PermissionServiceTests"/> and the matrix by Core.Tests).
/// </summary>
public class RequirePermissionFilterTests
{
    [Fact]
    public async Task DeniedPermission_Returns403_NamingThePermission()
    {
        using var host = await HostWith(granted: false);

        var response = await host.GetTestServer().CreateClient().GetAsync("/gated");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("forbidden", body);
        Assert.Contains(nameof(Permission.ManageMembers), body);
    }

    [Fact]
    public async Task GrantedPermission_AllowsTheRequestThrough()
    {
        using var host = await HostWith(granted: true);

        var response = await host.GetTestServer().CreateClient().GetAsync("/gated");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<IHost> HostWith(bool granted) =>
        new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer()
               .ConfigureServices(services =>
               {
                   services.AddRouting();
                   services.AddSingleton<IPermissionService>(new StubPermissions(granted));
               })
               .Configure(app =>
               {
                   app.UseRouting();
                   app.UseEndpoints(e =>
                       e.MapGet("/gated", () => Results.Ok("ok")).RequirePermission(Permission.ManageMembers));
               });
        }).StartAsync();
}

internal sealed class StubPermissions(bool result) : IPermissionService
{
    public Task<bool> HasAsync(Permission permission, CancellationToken cancellationToken = default) =>
        Task.FromResult(result);
}
