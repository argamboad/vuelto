using System.Security.Claims;
using Perezosoft.Core.Abstractions;

namespace Perezosoft.Api.Observability;

/// <summary>
/// Opens a per-request logging scope carrying <c>tenant_id</c> and <c>user_id</c>, so every log line
/// emitted while handling the request is filterable by tenant/user (OBS-1, ADR-008). Tenant comes from
/// <see cref="ICurrentTenant"/> (the JWT claim), user from the <see cref="ClaimTypes.NameIdentifier"/>
/// claim. An anonymous request adds no scope. <b>Only identifiers</b> go in the scope — never tokens or
/// other secrets.
/// </summary>
public sealed class RequestLoggingScopeMiddleware(RequestDelegate next, ILogger<RequestLoggingScopeMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ICurrentTenant currentTenant)
    {
        var scope = BuildScope(context, currentTenant);
        if (scope.Count == 0)
        {
            await next(context);
            return;
        }

        using (logger.BeginScope(scope))
            await next(context);
    }

    /// <summary>The scope state for a request — tenant/user identifiers only. Extracted for testability.</summary>
    internal static Dictionary<string, object> BuildScope(HttpContext context, ICurrentTenant currentTenant)
    {
        var scope = new Dictionary<string, object>();

        if (currentTenant.TenantId is { } tenantId)
            scope["tenant_id"] = tenantId;

        if (context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value is { } userId)
            scope["user_id"] = userId;

        return scope;
    }
}

/// <summary>Registers <see cref="RequestLoggingScopeMiddleware"/>. Call after authentication so claims are available.</summary>
public static class RequestLoggingScopeExtensions
{
    public static IApplicationBuilder UseRequestLoggingScope(this IApplicationBuilder app) =>
        app.UseMiddleware<RequestLoggingScopeMiddleware>();
}
