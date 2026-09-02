using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.Extensions.Logging;

namespace Vuelto.Shared.Ui.Auth;

/// <summary>Outcome of a native primary-auth attempt: signed in, failed, or owes an MFA step-up.</summary>
public enum SignInStatus { Success, Failed, MfaRequired }

/// <summary>
/// A native sign-in result; carries the MFA challenge when <see cref="SignInStatus.MfaRequired"/>,
/// and on failure the server's error code (e.g. <c>too_many_attempts</c>) so the UI can pick the
/// right copy instead of a blanket "incorrect or expired".
/// </summary>
public sealed record SignInResult(SignInStatus Status, string? Challenge = null, string? Error = null)
{
    public static readonly SignInResult Failed = new(SignInStatus.Failed);
    public static readonly SignInResult Success = new(SignInStatus.Success);
    public static SignInResult FailedWith(string? error) => new(SignInStatus.Failed, Error: error);
    public static SignInResult Mfa(string challenge) => new(SignInStatus.MfaRequired, challenge);
}

/// <summary>
/// Client-side authentication with refresh-token support.
/// The access token is held in memory only — never persisted.
/// Sessions survive reloads/restarts via the refresh token: on startup we silently
/// exchange it for a fresh access token. Where that refresh token lives is the only
/// per-host difference, abstracted behind <see cref="ISessionStore"/> — an HttpOnly
/// cookie on the web, the OS secure store on native (MAUI).
/// </summary>
public class AuthService(
    HttpClient httpClient,
    ILogger<AuthService> logger,
    ISessionStore sessionStore,
    IOAuthInitiator? oauth = null,
    IOAuthResumeStore? resumeStore = null,
    TimeProvider? timeProvider = null)
{
    private string? _accessToken;
    private Task<bool>? _refreshInFlight;

    /// <summary>
    /// Raised when the service transitions from unauthenticated to holding a valid access
    /// token — every sign-in path lands here (OTP/MFA/native OAuth via the body flow, and
    /// the cookie flow's silent refresh), so MainLayout can reconcile per-user preferences
    /// on interactive sign-ins too, not just cold starts (PREFS-1, ADR-022). NOT raised on
    /// mid-session token rotation (still signed in) or impersonation (deliberate — an admin
    /// session must not adopt/apply the impersonated user's preferences as its own).
    /// </summary>
    public event Action? SignedIn;

    /// <summary>
    /// Raised when the service transitions from authenticated to signed-out (logout, or a refresh that
    /// finds no valid session). NOT raised when already signed out. MainLayout clears device-stored
    /// preferences on it so the next user on a shared device can't inherit this account's theme/locale
    /// (v3 audit ADM-9) — safe because a new sign-in can only follow a logout (the refresh cookie
    /// otherwise auto-restores this session).
    /// </summary>
    public event Action? SignedOut;

    /// <summary>
    /// Raised when the active identity is swapped in place — entering or leaving impersonation —
    /// without a full sign-in. Lets parameterless chrome (the header) re-source the displayed
    /// identity + staff flag from the new token; the soft nav that follows re-renders the layout
    /// body but not a parameterless child. Distinct from <see cref="SignedIn"/> so it does NOT
    /// trigger preference reconciliation (an admin session must not adopt the impersonated user's
    /// preferences as its own).
    /// </summary>
    public event Action? IdentityChanged;

    private TimeProvider Time => timeProvider ?? TimeProvider.System;

    public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken) && !IsTokenExpired(_accessToken);

    /// <summary>
    /// True on native hosts (MAUI). The Login page uses it to swap the web's full-page
    /// OAuth navigation for the native browser flow and to drop the web-only magic link.
    /// </summary>
    public bool IsNative => sessionStore.UsesBodyTransport;

    /// <summary>The current JWT access token, or null when not signed in.</summary>
    public string? AccessToken => _accessToken;

    /// <summary>The signed-in user's id (from the JWT NameIdentifier claim), or null.</summary>
    public Guid? UserId
    {
        get
        {
            if (string.IsNullOrEmpty(_accessToken) || IsTokenExpired(_accessToken))
                return null;
            try
            {
                var jwt = new JwtSecurityTokenHandler().ReadJwtToken(_accessToken);
                var sub = jwt.Claims.FirstOrDefault(c =>
                    c.Type is "nameid" or ClaimTypes.NameIdentifier or "sub")?.Value;
                return Guid.TryParse(sub, out var id) ? id : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Attempts a silent refresh using the HttpOnly refresh cookie. Returns true
    /// when a fresh access token was obtained. Called on startup and on expiry.
    /// Concurrent callers share one in-flight call: the refresh endpoint ROTATES the
    /// token, so two overlapping requests would make the second fail with a revoked
    /// token. MainLayout is the single refresh entry point now, so this is mostly
    /// defense-in-depth. Blazor WASM is single-threaded, so sharing the Task suffices.
    /// </summary>
    public Task<bool> TryRefreshAsync()
    {
        if (_refreshInFlight is { } inFlight)
            return inFlight;
        var refresh = RunRefreshAsync();
        // Cache only a task that is still RUNNING (v3 T45c). When RunRefreshAsync completes
        // synchronously — a native session store answering "no stored token" without yielding — its
        // finally has ALREADY cleared the field, and `_refreshInFlight ??= …` would re-cache the
        // completed task forever: every later refresh would replay the stale result without ever
        // hitting the network, leaving a native session dead until app restart.
        if (!refresh.IsCompleted)
            _refreshInFlight = refresh;
        return refresh;
    }

    private async Task<bool> RunRefreshAsync()
    {
        try
        {
            HttpResponseMessage response;
            if (sessionStore.UsesBodyTransport)
            {
                // Native: the refresh token lives in the OS secure store; send it in the
                // body. No stored token means simply "not signed in" — skip the call.
                var stored = await sessionStore.GetRefreshTokenAsync();
                if (string.IsNullOrEmpty(stored))
                {
                    _accessToken = null;
                    return false;
                }
                response = await httpClient.PostAsJsonAsync("/api/auth/refresh",
                    new { refresh_token = stored });
            }
            else
            {
                // Web: the CookieHandler attaches the HttpOnly refresh cookie; no body.
                response = await httpClient.PostAsync("/api/auth/refresh", null);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Token refresh failed: {StatusCode}", response.StatusCode);
                await ClearSessionAsync();
                return false;
            }

            var payload = await response.Content.ReadFromJsonAsync<TokenResponse>();
            if (!string.IsNullOrEmpty(payload?.AccessToken))
            {
                await AcceptTokensAsync(payload);
                return true;
            }

            logger.LogWarning("Refresh response missing access_token");
            await ClearSessionAsync();
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to refresh access token");
            await ClearSessionAsync();
            return false;
        }
        finally
        {
            // Allow a fresh refresh next time; only *concurrent* calls are coalesced.
            _refreshInFlight = null;
        }
    }

    /// <summary>
    /// Completes native OAuth: runs the platform browser flow, exchanges the returned
    /// one-time code for tokens, and stores them. Returns true on success. Web hosts
    /// sign in by full-page navigation and never call this.
    /// </summary>
    public async Task<SignInResult> SignInWithOAuthAsync(string provider)
    {
        if (oauth is null)
        {
            logger.LogError("SignInWithOAuthAsync called with no IOAuthInitiator registered");
            return SignInResult.Failed;
        }
        try
        {
            var result = await RunResumableBrowserFlowAsync(provider, linkToken: null);
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
    /// Links an OAuth provider to the current account on native hosts, carrying the
    /// caller-issued <paramref name="linkToken"/> through the system-browser flow.
    /// Returns null on success, or an error key ("in_use", "expired", "cancelled",
    /// "link_failed") for the UI.
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

    // ── OAuth resume across process death (NATIVE-12) ────────────────────────

    /// <summary>
    /// How long a stashed callback stays exchangeable. Mirrors the API's one-time-code
    /// TTL (<c>NativeAuthCodeService</c>, 5 min) — an older stash is dead server-side,
    /// so we fail it with a friendly retry instead of a doomed exchange.
    /// </summary>
    public static readonly TimeSpan OAuthResumeTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// True while an OAuth browser flow is awaiting its callback in THIS process. The
    /// Android callback activity reads it to tell warm delivery (WebAuthenticator will
    /// complete normally) from a cold start after process death (the callback must be
    /// stashed for the startup resume instead).
    /// </summary>
    public bool OAuthFlowInFlightInProcess { get; private set; }

    private OAuthResumeResult? _resumeHandoff;

    /// <summary>
    /// One-shot handoff of a resume outcome the Login page must act on (MFA step-up,
    /// expired stash, failed exchange). Set by <see cref="TryCompletePendingOAuthAsync"/>;
    /// consumed by Login's OnInitialized.
    /// </summary>
    public OAuthResumeResult? TakeOAuthResumeHandoff()
    {
        var handoff = _resumeHandoff;
        _resumeHandoff = null;
        return handoff;
    }

    /// <summary>
    /// Runs the platform browser flow with a persisted in-flight marker around it, so a
    /// process killed mid-round-trip can resume from the stashed callback on next start.
    /// The marker is cleared the moment the flow returns to this process — from here on
    /// the normal in-memory path owns the result.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>?> RunResumableBrowserFlowAsync(string provider, string? linkToken)
    {
        resumeStore?.SetInFlight(new OAuthFlowMarker(provider, linkToken, Time.GetUtcNow()));
        OAuthFlowInFlightInProcess = true;
        try
        {
            return await oauth!.RunBrowserFlowAsync(provider, linkToken);
        }
        finally
        {
            OAuthFlowInFlightInProcess = false;
            resumeStore?.ClearInFlight();
        }
    }

    /// <summary>
    /// Startup entry point (called once from MainLayout, right after
    /// <see cref="InitializeAsync"/>): if the previous process died during an OAuth
    /// browser round-trip and the callback was stashed by the platform callback
    /// activity, finish the flow — exchange the one-time code through the normal native
    /// login path, or report the link outcome. Consumes the persisted state either way;
    /// a no-op returning <see cref="OAuthResumeResult.None"/> on web and on normal starts.
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

            if (Time.GetUtcNow() - marker.StartedUtc > OAuthResumeTtl)
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
    /// Verifies an OTP code and establishes the session from the tokens in the response
    /// body. Used by native hosts (the web Login page keeps its cookie + callback flow).
    /// </summary>
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

    /// <summary>
    /// Completes a native MFA step-up: posts the challenge from a prior login + a TOTP/recovery code and
    /// stores the tokens the API returns in the body. Returns true on success. Web hosts complete the
    /// step-up via the cookie flow (Login page) and don't call this.
    /// </summary>
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

        await AcceptTokensAsync(new TokenResponse { AccessToken = payload.AccessToken, RefreshToken = payload.RefreshToken });
        return SignInResult.Success;
    }

    // ── Platform-staff admin surface (ADR-014) ──────────────────────────────

    // Cache the staff probe for the session so nav rendering doesn't re-hit the API.
    // Reset whenever the identity changes (impersonate/stop/logout).
    private bool? _isStaff;

    /// <summary>
    /// Whether the signed-in user is platform staff — drives the admin nav link + page gate.
    /// Cheap probe of <c>GET /api/admin/me</c> (200 with <c>is_staff</c> for any authenticated user);
    /// cached per identity. False when signed out, on any error, or while impersonating.
    /// </summary>
    public async Task<bool> IsStaffAsync()
    {
        if (_isStaff is { } cached) return cached;
        if (!IsAuthenticated || IsImpersonating) return (_isStaff = false).Value;
        try
        {
            // This service's HttpClient deliberately has NO Bearer handler (it would be a DI cycle —
            // see Program.cs), so attach the in-memory token explicitly for this authenticated probe.
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/me");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
            using var response = await httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return false; // don't cache transient failures
            var res = await response.Content.ReadFromJsonAsync<StaffStatus>();
            return (_isStaff = res?.IsStaff ?? false).Value;
        }
        catch
        {
            return false; // don't cache transient failures
        }
    }

    /// <summary>
    /// The OAuth providers the server has actually configured (lowercase, e.g. "google"). Anonymous
    /// probe of <c>GET /api/auth/providers</c> — the login + settings pages render only these, so an
    /// unconfigured provider shows no dead button (challenging it 500s). Empty on any error (fail closed
    /// to no OAuth rather than a broken button).
    /// </summary>
    public async Task<IReadOnlyList<string>> GetEnabledProvidersAsync()
    {
        try
        {
            var res = await httpClient.GetFromJsonAsync<ProvidersResponse>("/api/auth/providers");
            return res?.Providers ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>True when the current access token is an admin "sign in as" token.</summary>
    public bool IsImpersonating => Claim(AppClaims.ImpersonatedBy) is not null;

    /// <summary>
    /// Enters an impersonated session using a short-lived admin token (no refresh token — it's
    /// non-refreshable by design). Held in memory only; a reload or expiry returns the staff user to
    /// their own identity via the untouched refresh cookie.
    /// </summary>
    public void BeginImpersonation(string accessToken)
    {
        _accessToken = accessToken;
        _isStaff = null;
        IdentityChanged?.Invoke();
    }

    /// <summary>
    /// Leaves an impersonated session and restores the staff user from their refresh cookie/store.
    /// Returns true when the original identity was restored.
    /// </summary>
    public async Task<bool> StopImpersonationAsync()
    {
        _accessToken = null;
        _isStaff = null;
        var restored = await TryRefreshAsync();
        IdentityChanged?.Invoke();
        return restored;
    }

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

        await ClearSessionAsync();
    }

    /// <summary>Sets the in-memory access token and persists the rotated refresh token (native).</summary>
    private async Task AcceptTokensAsync(TokenResponse payload)
    {
        var wasAuthenticated = IsAuthenticated;
        _accessToken = payload.AccessToken;
        _isStaff = null; // identity may have changed; re-probe on demand
        if (sessionStore.UsesBodyTransport && !string.IsNullOrEmpty(payload.RefreshToken))
            await sessionStore.SaveRefreshTokenAsync(payload.RefreshToken);

        if (!wasAuthenticated && IsAuthenticated)
            SignedIn?.Invoke();
    }

    private async Task ClearSessionAsync()
    {
        var wasAuthenticated = IsAuthenticated;
        _accessToken = null;
        _isStaff = null;
        if (sessionStore.UsesBodyTransport)
            await sessionStore.ClearAsync();
        if (wasAuthenticated)
            SignedOut?.Invoke();
    }

    /// <summary>
    /// Resolves the session on app startup: with no token in memory, silently
    /// exchanges the HttpOnly refresh cookie for an access token. Called ONCE from
    /// MainLayout — the single auth entry point (mirrors phase2). Because the layout
    /// awaits this before rendering @Body, pages never trigger their own refresh.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (IsAuthenticated) return;
        await TryRefreshAsync();
    }

    /// <summary>Display name from the JWT 'name' claim, falling back to email.</summary>
    public string? DisplayName => Claim("name") ?? Claim(ClaimTypes.Name) ?? Claim("email") ?? Claim(ClaimTypes.Email);

    /// <summary>Tenant ("household") name from the JWT.</summary>
    public string? TenantName => Claim(AppClaims.TenantName);

    /// <summary>The user's saved UI locale from the JWT (e.g. "es"), or null if unset.</summary>
    public string? Locale => Claim(AppClaims.Locale);

    /// <summary>The user's saved UI theme from the JWT ("light"/"dark"/"system"), or null when never chosen.</summary>
    public string? Theme => Claim(AppClaims.Theme);

    private string? Claim(string type)
    {
        if (string.IsNullOrEmpty(_accessToken) || IsTokenExpired(_accessToken))
            return null;
        try
        {
            return new JwtSecurityTokenHandler().ReadJwtToken(_accessToken)
                .Claims.FirstOrDefault(c => c.Type == type)?.Value;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsTokenExpired(string token)
    {
        try
        {
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
            return jwt.ValidTo < DateTime.UtcNow;
        }
        catch
        {
            return true;
        }
    }

    // GET /api/admin/me — is the caller platform staff?
    private sealed record StaffStatus
    {
        [System.Text.Json.Serialization.JsonPropertyName("is_staff")]
        public bool IsStaff { get; init; }
    }

    // GET /api/auth/providers — the OAuth providers this deployment configured.
    private sealed record ProvidersResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("providers")]
        public IReadOnlyList<string>? Providers { get; init; }
    }

    // A native primary-auth response: either tokens, or an MFA challenge to step up (mfa_required).
    private sealed record NativeAuthResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("mfa_required")]
        public bool MfaRequired { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("challenge")]
        public string? Challenge { get; init; }

        // Present only on a failure body (ErrorResponse); e.g. too_many_attempts on OTP lockout.
        [System.Text.Json.Serialization.JsonPropertyName("error")]
        public string? Error { get; init; }
    }

    // Mirrors the API's TokenResponse (snake_case JSON).
    private sealed record TokenResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        // Present only for native clients; the web flow keeps the token in the cookie.
        [System.Text.Json.Serialization.JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }
    }
}
