using System.Net.Http.Headers;
using Vuelto.Shared.Ui.Auth;

namespace Vuelto.Web.Http;

// Attaches the in-memory JWT access token as a Bearer header on every API request.
public class AuthHeaderHandler(AuthService auth) : DelegatingHandler
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
