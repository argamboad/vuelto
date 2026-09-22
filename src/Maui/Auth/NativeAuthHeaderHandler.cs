using System.Net.Http.Headers;
using Vuelto.Shared.Ui.Auth;

namespace Vuelto.Maui.Auth;

/// <summary>
/// Attaches the in-memory JWT access token as a Bearer header on every API request —
/// the native counterpart of the web <c>AuthHeaderHandler</c>. Without it, the RCL pages
/// that call [Authorize] endpoints (household, linked logins) would send no token and 401.
/// </summary>
public sealed class NativeAuthHeaderHandler(AuthService auth) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Renewed first when it is about to expire — the session stays alive while the app stays open.
        var token = await auth.GetFreshAccessTokenAsync();
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await base.SendAsync(request, cancellationToken);
    }
}
