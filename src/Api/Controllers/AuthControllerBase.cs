using Microsoft.AspNetCore.Mvc;
using Vuelto.Api.Authentication;
using Vuelto.Api.Services;

namespace Vuelto.Api.Controllers;

/// <summary>
/// Shared base for the auth controllers — holds request-scoped helpers common to the
/// OAuth/JWT/refresh flows. No routing or DI of its own; each derived controller carries
/// its own <c>[ApiController]</c>/<c>[Route]</c> and primary constructor.
/// </summary>
public abstract class AuthControllerBase : ControllerBase
{
    /// <summary>Caller IP for refresh-token auditing; "unknown" when unavailable.</summary>
    protected string ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>True when the request comes from a native (desktop/mobile) client.</summary>
    protected bool IsNativeClient => Request.Headers[AuthHeaders.NativeClient] == AuthHeaders.NativeClientValue;

    protected bool TryGetUserId(out Guid userId)
    {
        var id = User.GetUserId();
        userId = id ?? default;
        return id is not null;
    }

    protected static string AppendQuery(string url, string key, string value)
    {
        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}{key}={Uri.EscapeDataString(value)}";
    }

    protected static bool IsLikelyEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email) && System.Net.Mail.MailAddress.TryCreate(email.Trim(), out _);
}
