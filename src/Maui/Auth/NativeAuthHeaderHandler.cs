using System.Net.Http.Headers;
using Perezosoft.Shared.Ui.Auth;

namespace Perezosoft.Maui.Auth;

/// <summary>
/// Attaches the in-memory JWT access token as a Bearer header on every API request —
/// the native counterpart of the web <c>AuthHeaderHandler</c>. Without it, the RCL pages
/// that call [Authorize] endpoints (household, linked logins) would send no token and 401.
/// </summary>
public sealed class NativeAuthHeaderHandler(AuthService auth) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = auth.AccessToken;
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return base.SendAsync(request, cancellationToken);
    }
}
