using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Api.Configuration;
using Vuelto.Api.Endpoints;

namespace Vuelto.Api.Tests.Webhooks;

/// <summary>
/// v4 T44 (JOBS-7): the two webhook writes that cost something per call — the synchronous test-send and the
/// replay — carry the per-tenant <see cref="RateLimiting.WebhookWritePolicy"/>, and nothing else in the group
/// does (a list or a delete is not an outbound POST). The policy itself is exercised in <c>RateLimitingTests</c>;
/// this reads the metadata the REAL <see cref="WebhookEndpoints.MapWebhookManagement"/> produces, so a route
/// moved or re-mapped without the policy fails here. HOOKS is config-gated off in the integration host, hence
/// a minimal WebApplication: the mapping code is the production one, the host is only what routing needs.
/// </summary>
public class WebhookWritePolicyTests
{
    [Fact]
    public async Task OnlyTheTestSendAndTheReplay_CarryThePerTenantPolicy()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorization();
        // Registered so routing sees a service, not a body to infer, when it builds the handlers; never resolved.
        builder.Services.AddScoped<Vuelto.Api.Services.IWebhookSubscriptionService>(_ => throw new NotSupportedException("metadata only"));
        await using var app = builder.Build();
        app.MapWebhookManagement();
        await app.StartAsync();

        var throttled = app.Services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == RateLimiting.WebhookWritePolicy)
            .Select(e => $"{e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Single()} /{e.RoutePattern.RawText?.Trim('/')}")
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["POST /api/webhooks/deliveries/{deliveryId:guid}/replay", "POST /api/webhooks/{id:guid}/test"], throttled);
    }
}
