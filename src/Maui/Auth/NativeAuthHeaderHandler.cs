using Vuelto.Shared.Ui.Auth;

namespace Vuelto.Maui.Auth;

/// <summary>
/// Attaches the in-memory JWT access token as a Bearer header on every API request —
/// the native counterpart of the web <c>AuthHeaderHandler</c>. Without it, the RCL pages
/// that call [Authorize] endpoints (household, linked logins) would send no token and 401.
/// Renewed first when it is about to expire, and renewed-then-resent once on a 401 (v4 T32,
/// R126); the behaviour lives in the shared <see cref="BearerRetry"/>, and a test holds both
/// handlers to it.
/// </summary>
public sealed class NativeAuthHeaderHandler(AuthService auth) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        BearerRetry.SendAsync(auth, request, base.SendAsync, cancellationToken);
}
