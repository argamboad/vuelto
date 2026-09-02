using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Perezosoft.Api.Observability;
using Perezosoft.Api.Tests.Infrastructure;
using Perezosoft.Core.Abstractions;

namespace Perezosoft.Api.Tests.Observability;

/// <summary>
/// OBS-2 (ADR-008): the request span is tagged with tenant_id/user_id (identifiers only) so traces
/// filter per tenant, and an anonymous request adds no such tags.
/// </summary>
public class TelemetryEnrichmentTests
{
    [Fact]
    public void EnrichSpan_Authenticated_AddsTenantAndUserTags()
    {
        var tenant = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7().ToString();
        using var activity = new Activity("request").Start();

        TelemetryExtensions.EnrichSpan(activity, ContextWith(tenant, userId));

        Assert.Equal(tenant.ToString(), activity.GetTagItem("tenant_id"));
        Assert.Equal(userId, activity.GetTagItem("user_id"));
    }

    [Fact]
    public void EnrichSpan_Anonymous_AddsNoTags()
    {
        using var activity = new Activity("request").Start();

        TelemetryExtensions.EnrichSpan(activity, ContextWith(tenant: null, userId: null));

        Assert.Null(activity.GetTagItem("tenant_id"));
        Assert.Null(activity.GetTagItem("user_id"));
    }

    [Fact]
    public void EnrichSpan_RequestServicesNull_DoesNotThrow()
    {
        // Requests rejected before the pipeline runs (Kestrel bad requests, early aborts) reach the
        // enrichment hook with RequestServices unset — the non-nullable annotation notwithstanding.
        using var activity = new Activity("request").Start();

        TelemetryExtensions.EnrichSpan(activity, new DefaultHttpContext());

        Assert.Null(activity.GetTagItem("tenant_id"));
        Assert.Null(activity.GetTagItem("user_id"));
    }

    private static DefaultHttpContext ContextWith(Guid? tenant, string? userId)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentTenant>(new TestCurrentTenant { TenantId = tenant });
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        if (userId is not null)
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));
        return context;
    }
}
