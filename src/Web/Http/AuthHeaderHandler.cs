using System.Net.Http.Headers;
using Perezosoft.Shared.Ui.Auth;

namespace Perezosoft.Web.Http;

// Attaches the in-memory JWT access token as a Bearer header on every API request.
public class AuthHeaderHandler(AuthService auth) : DelegatingHandler
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
