using System.Net;
using System.Net.Http.Headers;

namespace Vuelto.Shared.Ui.Auth;

/// <summary>
/// What both hosts' bearer handlers do, in one place (v4 T32, R126): attach the session's access token,
/// renewed first when it is about to expire, and — when the server answers 401 to a request sent with a
/// token the client believed live (a device clock that drifted against the server's, a revocation the
/// client hasn't heard about) — renew ONCE and resend. A second 401 stands: the refresh that got there is
/// the server's final word, and <see cref="AuthService"/> has already ended the session if it was rejected.
/// The web <c>AuthHeaderHandler</c> and the MAUI <c>NativeAuthHeaderHandler</c> both delegate here; a test
/// scans their sources for it, so the two never drift apart again.
/// </summary>
public static class BearerRetry
{
    public static async Task<HttpResponseMessage> SendAsync(
        AuthService auth,
        HttpRequestMessage request,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        var token = await auth.GetFreshAccessTokenAsync();
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // A body must be sendable twice: buffer it now (a stream can't be rewound after the first send).
        if (request.Content is not null)
            await request.Content.LoadIntoBufferAsync();

        var response = await send(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized || string.IsNullOrEmpty(token))
            return response;

        var renewed = await auth.RenewAfterRejectedRequestAsync();
        if (string.IsNullOrEmpty(renewed))
            return response; // rejected (session ended) or unreachable (nothing new to send): the 401 stands

        response.Dispose();
        var retry = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Content = request.Content,
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
        };
        foreach (var header in request.Headers)
            retry.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in request.Options)
            retry.Options.TryAdd(option.Key, option.Value);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", renewed);
        return await send(retry, cancellationToken);
    }
}
