namespace Vuelto.Shared.Ui.Auth;

/// <summary>
/// The one bearer handler both hosts install on their default <see cref="HttpClient"/> (v4 T49, R102). It
/// attaches the in-memory access token — renewed first when about to expire, renewed-then-resent once on a
/// 401 (<see cref="BearerRetry"/>) — <b>only to the API's own requests</b>: a relative URI, or an absolute one
/// on <paramref name="apiOrigin"/>. A request to any other host goes out untouched: no token, no refresh.
/// Before this the web and native handlers were unscoped, and the native export download (a presigned S3
/// URL fetched through the default client) carried the tenant-scoped token to the file host — which AWS
/// rejects outright — and spent a refresh-token rotation on the way.
/// </summary>
public sealed class BearerScopedHandler(AuthService auth, Uri apiOrigin) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        BearerRetry.SendAsync(auth, apiOrigin, request, base.SendAsync, cancellationToken);
}
