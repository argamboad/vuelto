using Vuelto.Shared.Ui.Auth;

namespace Vuelto.Web.Http;

// Attaches the in-memory JWT access token as a Bearer header on every API request — renewed first when it is
// about to expire, and renewed-then-resent once on a 401 (v4 T32, R126). The behaviour lives in the shared
// BearerRetry so the native handler is identical; a test holds both to it.
public class AuthHeaderHandler(AuthService auth) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        BearerRetry.SendAsync(auth, request, base.SendAsync, cancellationToken);
}
