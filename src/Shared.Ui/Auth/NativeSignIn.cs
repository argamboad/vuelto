using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Vuelto.Shared.Ui.Auth;

/// <summary>
/// The sign-in paths only a native host (MAUI) takes — tokens in the response body rather than a cookie — pulled
/// out of <see cref="AuthService"/> (v4 audit T57): OTP and MFA verification, the system-browser OAuth flow, and
/// resuming that flow after the process died mid-round-trip (NATIVE-12). It decides nothing about the session:
/// tokens it obtains are handed to <c>acceptTokens</c>, which is the session's one way in.
/// </summary>
internal sealed class NativeSignIn(
    HttpClient httpClient,
    ILogger logger,
    IOAuthInitiator? oauth,
    IOAuthResumeStore? resumeStore,
    TimeProvider time,
    Func<TokenResponse, Task> acceptTokens)
{
    /// <summary>
    /// How long a stashed callback stays exchangeable. Mirrors the API's one-time-code
    /// TTL (<c>NativeAuthCodeService</c>, 5 min) — an older stash is dead server-side,
    /// so we fail it with a friendly retry instead of a doomed exchange.
    /// </summary>
    public static readonly TimeSpan ResumeTtl = TimeSpan.FromMinutes(5);

    private OAuthResumeResult? _resumeHandoff;

    /// <summary>True while an OAuth browser flow is awaiting its callback in THIS process.</summary>
    public bool FlowInFlightInProcess { get; private set; }

    /// <summary>Verifies an OTP code and establishes the session from the tokens in the response body.</summary>
    public async Task<SignInResult> VerifyOtpAsync(string email, string code)
    {
        try
        {
            // Returns tokens — or, if the user has MFA on, an {mfa_required, challenge} to step up.
            var response = await httpClient.PostAsJsonAsync("/api/auth/otp/verify",
                new { email, code });
            return await CompleteFromResponseAsync(response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OTP verification failed");
            return SignInResult.Failed;
        }
    }

    /// <summary>Completes an MFA step-up: the challenge from a prior login plus a TOTP/recovery code.</summary>
    public async Task<bool> VerifyMfaAsync(string challenge, string code)
    {
        try
        {
            var response = await httpClient.PostAsJsonAsync("/api/auth/mfa/verify", new { challenge, code });
            var result = await CompleteFromResponseAsync(response);
            return result.Status == SignInStatus.Success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MFA verification failed");
            return false;
        }
    }

    /// <summary>Runs the platform browser flow and exchanges the returned one-time code for tokens.</summary>
    public async Task<SignInResult> SignInWithOAuthAsync(string provider, CancellationToken cancellationToken = default)
    {
        if (oauth is null)
        {
            logger.LogError("SignInWithOAuthAsync called with no IOAuthInitiator registered");
            return SignInResult.Failed;
        }
        try
        {
            var result = await RunResumableBrowserFlowAsync(provider, linkToken: null, cancellationToken);
            var code = result is not null && result.TryGetValue("code", out var c) ? c : null;
            if (string.IsNullOrEmpty(code))
                return SignInResult.Failed;

            // The exchange returns tokens — or, if the user has MFA on, an {mfa_required, challenge}.
            var response = await httpClient.PostAsJsonAsync("/api/auth/native/exchange", new { code });
            return await CompleteFromResponseAsync(response);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Native OAuth sign-in failed for {Provider}", provider);
            return SignInResult.Failed;
        }
    }

    /// <summary>
    /// Links an OAuth provider to the current account, carrying the caller-issued <paramref name="linkToken"/>
    /// through the system-browser flow. Null on success, or an error key ("unsupported", "in_use", "expired",
    /// "cancelled", "link_failed") for the UI.
    /// </summary>
    public async Task<string?> LinkProviderAsync(string provider, string linkToken)
    {
        if (oauth is null)
            return "unsupported";
        try
        {
            var result = await RunResumableBrowserFlowAsync(provider, linkToken);
            if (result is null)
                return "cancelled";
            if (result.TryGetValue("error", out var error))
                return string.IsNullOrEmpty(error) ? "link_failed" : error;
            return result.ContainsKey("linked") ? null : "link_failed";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Native provider link failed for {Provider}", provider);
            return "link_failed";
        }
    }

    /// <summary>One-shot handoff of a resume outcome the Login page must act on; set by <see cref="TryCompletePendingOAuthAsync"/>.</summary>
    public OAuthResumeResult? TakeResumeHandoff()
    {
        var handoff = _resumeHandoff;
        _resumeHandoff = null;
        return handoff;
    }

    /// <summary>
    /// If the previous process died during an OAuth browser round-trip and the callback was stashed by the
    /// platform callback activity, finish the flow — exchange the one-time code through the normal native login
    /// path, or report the link outcome. Consumes the persisted state either way; a no-op returning
    /// <see cref="OAuthResumeResult.None"/> on web and on normal starts.
    /// </summary>
    public async Task<OAuthResumeResult> TryCompletePendingOAuthAsync()
    {
        if (resumeStore is null)
            return OAuthResumeResult.None;

        var marker = resumeStore.GetInFlight();
        var callback = resumeStore.TakePendingCallback();
        // One-shot: any persisted flight reaching a fresh startup is dead — never leave
        // state behind to re-trigger on the next launch.
        resumeStore.ClearInFlight();
        if (marker is null || string.IsNullOrEmpty(callback))
            return OAuthResumeResult.None;

        try
        {
            var props = ParseCallbackQuery(callback);

            if (!string.IsNullOrEmpty(marker.LinkToken))
            {
                // Linking completes server-side at redirect time — nothing to exchange,
                // just surface the outcome (Settings shows its usual banner).
                if (props.ContainsKey("linked"))
                    return new(OAuthResumeOutcome.LinkCompleted, marker.Provider);
                props.TryGetValue("error", out var linkError);
                return new(OAuthResumeOutcome.LinkFailed, marker.Provider,
                    Error: string.IsNullOrEmpty(linkError) ? "link_failed" : linkError);
            }

            if (props.TryGetValue("error", out _) || !props.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
                return Handoff(new(OAuthResumeOutcome.Failed, marker.Provider));

            if (time.GetUtcNow() - marker.StartedUtc > ResumeTtl)
                return Handoff(new(OAuthResumeOutcome.Expired, marker.Provider));

            logger.LogInformation("Resuming OAuth sign-in for {Provider} after process death", marker.Provider);
            var response = await httpClient.PostAsJsonAsync("/api/auth/native/exchange", new { code });
            var result = await CompleteFromResponseAsync(response);
            return result.Status switch
            {
                SignInStatus.Success => new(OAuthResumeOutcome.SignedIn, marker.Provider),
                SignInStatus.MfaRequired => Handoff(new(OAuthResumeOutcome.MfaRequired, marker.Provider, result.Challenge)),
                _ => Handoff(new(OAuthResumeOutcome.Failed, marker.Provider)),
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resuming interrupted OAuth failed for {Provider}", marker.Provider);
            return Handoff(new(OAuthResumeOutcome.Failed, marker.Provider));
        }

        OAuthResumeResult Handoff(OAuthResumeResult result)
        {
            _resumeHandoff = result;
            return result;
        }
    }

    /// <summary>
    /// Runs the platform browser flow with a persisted in-flight marker around it, so a
    /// process killed mid-round-trip can resume from the stashed callback on next start.
    /// The marker is cleared the moment the flow returns to this process — from here on
    /// the normal in-memory path owns the result.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>?> RunResumableBrowserFlowAsync(string provider, string? linkToken, CancellationToken cancellationToken = default)
    {
        resumeStore?.SetInFlight(new OAuthFlowMarker(provider, linkToken, time.GetUtcNow()));
        FlowInFlightInProcess = true;
        try
        {
            return await oauth!.RunBrowserFlowAsync(provider, linkToken, cancellationToken);
        }
        finally
        {
            FlowInFlightInProcess = false;
            resumeStore?.ClearInFlight();
        }
    }

    /// <summary>
    /// Parses the callback redirect's query into the same shape as
    /// <c>WebAuthenticatorResult.Properties</c> (<c>code</c>, or <c>linked</c>/<c>error</c>).
    /// </summary>
    private static Dictionary<string, string> ParseCallbackQuery(string callbackUri)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var queryStart = callbackUri.IndexOf('?');
        if (queryStart < 0)
            return properties;
        var query = callbackUri[(queryStart + 1)..];
        var fragmentStart = query.IndexOf('#');
        if (fragmentStart >= 0)
            query = query[..fragmentStart];

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = separator < 0 ? pair : pair[..separator];
            var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
            properties[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        return properties;
    }

    /// <summary>
    /// Reads a native auth response: an <c>{mfa_required, challenge}</c> body means step up; otherwise the
    /// tokens are accepted and stored. Shared by the OTP, OAuth-exchange and MFA-verify paths.
    /// </summary>
    private async Task<SignInResult> CompleteFromResponseAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Native auth call failed: {StatusCode}", response.StatusCode);
            // Surface the server's error code (e.g. too_many_attempts) so the caller can distinguish
            // a lockout from a plain wrong/expired code. Body may be absent/unreadable → null.
            string? error = null;
            try { error = (await response.Content.ReadFromJsonAsync<NativeAuthResponse>())?.Error; }
            catch { /* no/unreadable body → generic failure */ }
            return SignInResult.FailedWith(error);
        }

        var payload = await response.Content.ReadFromJsonAsync<NativeAuthResponse>();
        if (payload is null)
            return SignInResult.Failed;

        if (payload.MfaRequired && !string.IsNullOrEmpty(payload.Challenge))
            return SignInResult.Mfa(payload.Challenge);

        if (string.IsNullOrEmpty(payload.AccessToken))
            return SignInResult.Failed;

        await acceptTokens(new TokenResponse { AccessToken = payload.AccessToken, RefreshToken = payload.RefreshToken, ExpiresIn = payload.ExpiresIn });
        return SignInResult.Success;
    }
}
