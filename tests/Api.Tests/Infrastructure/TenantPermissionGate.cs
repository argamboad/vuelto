using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Api.Authentication;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Tests.Infrastructure;

/// <summary>
/// Runs the <see cref="RequireTenantPermissionAttribute"/> that guards a controller action, exactly as
/// the MVC pipeline would, so unit tests that call an action <b>method directly</b> can still assert the
/// permission gate (v2 audit B9-5 moved the gate from an inline controller check into this action filter,
/// which direct method calls bypass). Reflects the attribute off the named action, executes its filter
/// against the caller's principal + a real <see cref="ITenantRepository"/>, and returns the short-circuit
/// <see cref="IActionResult"/> (a 401/403 envelope) — or <c>null</c> when the gate lets the request through.
/// The HTTP boundary is separately pinned by <c>RbacForbiddenIntegrationTests</c>.
/// </summary>
public static class TenantPermissionGate
{
    public static async Task<IActionResult?> RunAsync(
        Type controllerType, string actionName, ITenantRepository tenants, Guid callerUserId, Guid callerTenantId)
    {
        var attribute = controllerType.GetMethod(actionName)!
            .GetCustomAttribute<RequireTenantPermissionAttribute>()
            ?? throw new InvalidOperationException(
                $"{controllerType.Name}.{actionName} has no [RequireTenantPermission].");

        var services = new ServiceCollection().AddSingleton(tenants).BuildServiceProvider();
        // The principal carries the JWT tenant_id — authz resolves membership for that tenant (LB-ADM-3).
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, callerUserId.ToString()),
             new Claim(Vuelto.Api.Services.JwtClaims.TenantId, callerTenantId.ToString())],
            authenticationType: "test"));
        var httpContext = new DefaultHttpContext { RequestServices = services, User = user };

        var actionContext = new ActionContext(
            httpContext, new Microsoft.AspNetCore.Routing.RouteData(), new ActionDescriptor());
        var executing = new ActionExecutingContext(
            actionContext, [], new Dictionary<string, object?>(), controller: null!);

        await attribute.OnActionExecutionAsync(executing,
            () => Task.FromResult(new ActionExecutedContext(actionContext, [], controller: null!)));

        return executing.Result;
    }
}
