using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Vuelto.Shared.Ui.Auth;

/// <summary>What one refresh call came back with — the wire's answer, before the session decides what it means.</summary>
internal enum RefreshAnswerKind
{
    /// <summary>Native only: the secure store holds no refresh token, so no call was made.</summary>
    NoStoredToken,
    /// <summary>The server answered and said no (401, or a 400/403 with the API's own error body): the refresh token is dead.</summary>
    Rejected,
    /// <summary>No usable answer (network, timeout, 5xx, a proxy's error page): nothing is known.</summary>
    Unreachable,
    /// <summary>A success body; <see cref="RefreshAnswer.Payload"/> is what it held (possibly without a token).</summary>
    Tokens,
}

/// <summary>A refresh call's answer and, for <see cref="RefreshAnswerKind.Tokens"/>, the body it carried.</summary>
internal readonly record struct RefreshAnswer(RefreshAnswerKind Kind, TokenResponse? Payload = null);

/// <summary>
/// The two calls that carry the refresh token, per host (v4 audit T57, pulled out of <see cref="AuthService"/>):
/// an HttpOnly cookie on the web, the OS secure store and the request body on native. It sends, and it sorts the
/// answer into what the server actually said; it holds no session state and changes none.
/// </summary>
internal sealed class SessionTransport(HttpClient httpClient, ISessionStore sessionStore, ILogger logger, TimeProvider time)
{
    /// <summary>
    /// How long the refresh call itself waits for an answer, on the injected clock. Shorter than the server's reuse
    /// grace window (<c>RefreshToken:ReuseGraceSeconds</c>, 60 s) minus <see cref="RenewalScheduler.RenewRetryDelay"/> on
    /// purpose: a refresh whose response is lost is retried with the old token, and that retry must land inside the
    /// window to be read as the benign race it is, not as theft. HttpClient's 100 s default did not. A cross-project
    /// test in Api.Tests (<c>ConfigPostureTests</c>) pins the three numbers together.
    /// </summary>
    public static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Exchanges the refresh token for a new access token. Never throws: no answer at all (no network, DNS, the
    /// call's own deadline) or an unreadable one comes back as <see cref="RefreshAnswerKind.Unreachable"/>.
    /// </summary>
    public async Task<RefreshAnswer> RefreshAsync()
    {
        try
        {
            // The call's own deadline, on the injected clock (so a fake clock can expire it in tests): a lost
            // response must be retried inside the server's reuse grace window — see RefreshTimeout.
            using var deadline = new CancellationTokenSource(RefreshTimeout, time);
            HttpResponseMessage response;
            if (sessionStore.UsesBodyTransport)
            {
                // Native: the refresh token lives in the OS secure store; send it in the
                // body. No stored token means simply "not signed in" — skip the call.
                var stored = await sessionStore.GetRefreshTokenAsync();
                if (string.IsNullOrEmpty(stored))
                    return new(RefreshAnswerKind.NoStoredToken);
                response = await httpClient.PostAsJsonAsync("/api/auth/refresh",
                    new { refresh_token = stored }, deadline.Token);
            }
            else
            {
                // Web: the CookieHandler attaches the HttpOnly refresh cookie; no body.
                response = await httpClient.PostAsync("/api/auth/refresh", null, deadline.Token);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized
                || (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden
                    && await IsApiErrorBodyAsync(response, deadline.Token)))
            {
                // The server looked at the token and refused it (revoked, expired, unknown): it is dead. A 400/403
                // counts only with the API's own error body — a firewall or proxy in front of the API answers
                // those too (an HTML challenge page), and that is not a verdict on the token (v4 UX-7).
                logger.LogWarning("Token refresh rejected: {StatusCode}", response.StatusCode);
                return new(RefreshAnswerKind.Rejected);
            }
            if (!response.IsSuccessStatusCode)
            {
                // A 5xx, a proxy's 502 while the host wakes, a 429, a 400/403 that isn't the API's: the server
                // never ruled on the token. Throwing it away here is what signed people out every morning
                // (found downstream, 2026-09-22).
                logger.LogWarning("Token refresh could not complete: {StatusCode}; keeping the session", response.StatusCode);
                return new(RefreshAnswerKind.Unreachable);
            }

            return new(RefreshAnswerKind.Tokens, await response.Content.ReadFromJsonAsync<TokenResponse>(deadline.Token));
        }
        catch (Exception ex)
        {
            // No answer at all (no network, DNS, our own deadline) or an unreadable one: the token's fate is unknown.
            logger.LogWarning(ex, "Token refresh could not reach the server; keeping the session");
            return new(RefreshAnswerKind.Unreachable);
        }
    }

    /// <summary>Tells the server the session is over. Best effort: a failure is logged, never thrown.</summary>
    public async Task LogoutAsync()
    {
        try
        {
            if (sessionStore.UsesBodyTransport)
            {
                var stored = await sessionStore.GetRefreshTokenAsync();
                await httpClient.PostAsJsonAsync("/api/auth/logout", new { refresh_token = stored });
            }
            else
            {
                await httpClient.PostAsync("/api/auth/logout", null);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to call logout endpoint");
        }
    }

    // True when the body is the API's own ErrorResponse ({"error": "...", "message": "..."}). Anything else —
    // HTML, empty, a different JSON shape — came from something in front of the API.
    private static async Task<bool> IsApiErrorBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return !string.IsNullOrEmpty(JsonSerializer.Deserialize<ApiErrorBody>(body)?.Error);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
