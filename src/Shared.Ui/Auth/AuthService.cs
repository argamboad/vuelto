using System.Security.Claims;
using Microsoft.Extensions.Logging;

namespace Vuelto.Shared.Ui.Auth;

/// <summary>
/// Client-side authentication with refresh-token support.
/// The access token is held in memory only — never persisted.
/// Sessions survive reloads/restarts via the refresh token: on startup we silently
/// exchange it for a fresh access token. Where that refresh token lives is the only
/// per-host difference, abstracted behind <see cref="ISessionStore"/> — an HttpOnly
/// cookie on the web, the OS secure store on native (MAUI).
/// <para>
/// An open session keeps itself alive (ADR-002 addendum, 2026-09-22): a timer renews the access token
/// shortly before it expires, a request renews it first if the timer was missed (the device slept, the
/// renewal failed), and the app coming back to the foreground does the same. Only the server rejecting
/// the refresh token ends a session — a timeout, a 5xx while the host cold-starts or no network leaves
/// the stored refresh token where it is, to be tried again.
/// </para>
/// <para>
/// This class is the session's state machine and nothing else (v4 audit T57): who is signed in, which session
/// an answer belongs to, when it ends. The parts it used to carry live beside it — <see cref="AccessTokenState"/>
/// (the token and its clock), <see cref="SessionTransport"/> (the refresh and logout calls),
/// <see cref="RenewalScheduler"/> (the keep-alive timer), <see cref="NativeSignIn"/> (body-token sign-in and
/// OAuth resume), <see cref="AuthProbes"/> (staff / billing / providers) and <see cref="PreferenceShadow"/>.
/// </para>
/// </summary>
public class AuthService
{
    private readonly ILogger<AuthService> _logger;
    private readonly ISessionStore _sessionStore;
    private readonly TimeProvider _time;
    private readonly AccessTokenState _token;
    private readonly SessionTransport _transport;
    private readonly RenewalScheduler _renewal;
    private readonly NativeSignIn _native;
    private readonly AuthProbes _probes;
    private readonly PreferenceShadow _remembered = new();

    private Task<RefreshOutcome>? _refreshInFlight;
    private int _refreshInFlightEpoch;
    private readonly object _refreshGate = new();

    // True from the moment tokens are accepted until the session is cleared: the one state in which renewing
    // is worth a call. Anonymous pages hit the API too, and must not spend a refresh on every request.
    private bool _sessionHeld;

    // The session epoch (v4 T31, R125): bumped by every change of session — logout, entering or leaving an
    // impersonation, an impersonation expiring. A refresh captures it when it starts; an answer that lands
    // after the epoch moved belongs to a session that is gone or superseded and is discarded, never applied.
    private int _epoch;

    // Impersonation is a STATE entered by BeginImpersonation and left by Stop, expiry or a cleared session —
    // never a claim read off the current token, which went silent the moment the token expired.
    private bool _impersonating;
    private ITimer? _impersonationExpiry;

    public AuthService(
        HttpClient httpClient,
        ILogger<AuthService> logger,
        ISessionStore sessionStore,
        IOAuthInitiator? oauth = null,
        IOAuthResumeStore? resumeStore = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _sessionStore = sessionStore;
        _time = timeProvider ?? TimeProvider.System;
        _token = new AccessTokenState(_time);
        _transport = new SessionTransport(httpClient, sessionStore, logger, _time);
        _renewal = new RenewalScheduler(_time, RenewInBackgroundAsync);
        _native = new NativeSignIn(httpClient, logger, oauth, resumeStore, _time, AcceptTokensAsync);
        _probes = new AuthProbes(httpClient);
    }

    /// <summary>
    /// How long before the access token expires the session renews it — capped at a quarter of the token's
    /// lifetime, so a short-lived token isn't renewed the moment it arrives.
    /// </summary>
    public static readonly TimeSpan RenewLead = TimeSpan.FromMinutes(1);

    /// <inheritdoc cref="RenewalScheduler.RenewRetryDelay"/>
    public static readonly TimeSpan RenewRetryDelay = RenewalScheduler.RenewRetryDelay;

    /// <inheritdoc cref="RenewalScheduler.RenewRetryCap"/>
    public static readonly TimeSpan RenewRetryCap = RenewalScheduler.RenewRetryCap;

    /// <inheritdoc cref="SessionTransport.RefreshTimeout"/>
    public static readonly TimeSpan RefreshTimeout = SessionTransport.RefreshTimeout;

    /// <inheritdoc cref="NativeSignIn.ResumeTtl"/>
    public static readonly TimeSpan OAuthResumeTtl = NativeSignIn.ResumeTtl;

    /// <summary>
    /// The pauses between startup attempts when the server can't be reached — a free-tier host takes up to
    /// half a minute to wake, and bouncing the user to the login page meanwhile is half of what this fixes.
    /// The app shows its loading spinner throughout.
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> StartupRetryDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15)];

    private enum RefreshOutcome
    {
        Renewed,
        /// <summary>The server answered and said no (401/400/403): the refresh token is dead.</summary>
        Rejected,
        /// <summary>No usable answer (network, timeout, 5xx, a proxy's error page): nothing is known, keep it.</summary>
        Unreachable,
        /// <summary>The answer arrived after the session it belonged to ended or was superseded; not applied.</summary>
        Discarded,
    }

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

    public bool IsAuthenticated => _token.Live;

    /// <summary>
    /// True from the moment tokens are accepted until the session is cleared — through an unreachable server
    /// that outlasts the access token, where <see cref="IsAuthenticated"/> already reads false. What the layout
    /// gates the signed-in shell on (v4 T33, R84): only the server saying no ends a session, and until then the
    /// user is still signed in, just waiting on a renewal.
    /// </summary>
    public bool HasSession => _sessionHeld;

    /// <summary>
    /// True on native hosts (MAUI). The Login page uses it to swap the web's full-page
    /// OAuth navigation for the native browser flow and to drop the web-only magic link.
    /// </summary>
    public bool IsNative => _sessionStore.UsesBodyTransport;

    /// <summary>The current JWT access token, or null when not signed in.</summary>
    public string? AccessToken => _token.Token;

    /// <summary>The signed-in user's id (from the JWT NameIdentifier claim), or null.</summary>
    public Guid? UserId => _token.UserId;

    /// <summary>Display name from the JWT 'name' claim, falling back to email.</summary>
    public string? DisplayName => _token.Claim("name") ?? _token.Claim(ClaimTypes.Name) ?? _token.Claim("email") ?? _token.Claim(ClaimTypes.Email);

    /// <summary>Tenant ("household") name from the JWT.</summary>
    public string? TenantName => _token.Claim(AppClaims.TenantName);

    /// <summary>The user's saved UI locale (e.g. "es") — one saved this session, else the JWT's — or null if unset.</summary>
    public string? Locale => _token.Token is null ? null : _remembered.Locale ?? _token.Claim(AppClaims.Locale);

    /// <summary>The user's saved UI theme ("light"/"dark"/"system") — one saved this session, else the JWT's —
    /// or null when never chosen.</summary>
    public string? Theme => _token.Token is null ? null : _remembered.Theme ?? _token.Claim(AppClaims.Theme);

    /// <summary>Records a theme the account has just saved, so the session reports it before its next token.</summary>
    public void RememberTheme(string theme) => _remembered.Theme = theme;

    /// <summary>Records a locale the account has just saved, so the session reports it before its next token.</summary>
    public void RememberLocale(string locale) => _remembered.Locale = locale;

    // ── Refresh: one call at a time, and only for the session that asked ────────────────────────────

    /// <summary>
    /// Attempts a silent refresh using the HttpOnly refresh cookie. Returns true
    /// when a fresh access token was obtained. Called on startup and on expiry.
    /// Concurrent callers share one in-flight call: the refresh endpoint ROTATES the
    /// token, so two overlapping requests would make the second fail with a revoked
    /// token. MainLayout is the single refresh entry point now, so this is mostly
    /// defense-in-depth. Blazor WASM is single-threaded, so sharing the Task suffices.
    /// </summary>
    public async Task<bool> TryRefreshAsync(bool force = false)
    {
        if (force)
        {
            // The caller's reason for asking (Join's accept) postdates whatever refresh is already on the wire,
            // so that one's answer is stale by definition: let it land, then ask again (v4 LB-UI-16).
            Task<RefreshOutcome>? inFlight;
            lock (_refreshGate)
                inFlight = _refreshInFlight;
            if (inFlight is not null)
            {
                try { await inFlight; }
                catch { /* its outcome is not ours to report */ }
            }
        }
        return await RefreshAsync() == RefreshOutcome.Renewed;
    }

    private Task<RefreshOutcome> RefreshAsync()
    {
        // Locked because on native the renewal timer fires on a thread-pool thread, and requests are issued
        // off the UI thread too; on WASM (single-threaded) the lock costs nothing.
        lock (_refreshGate)
        {
            // Share only a refresh of THIS session: one started before the epoch moved will be discarded, and
            // a caller from the new session (Stop restoring the staff identity) needs its own.
            if (_refreshInFlight is { } inFlight && _refreshInFlightEpoch == _epoch)
                return inFlight;
            var refresh = RunRefreshAsync();
            // Cache only a task that is still RUNNING (v3 T45c). When RunRefreshAsync completes
            // synchronously — a native session store answering "no stored token" without yielding — its
            // finally has ALREADY cleared the field, and `_refreshInFlight ??= …` would re-cache the
            // completed task forever: every later refresh would replay the stale result without ever
            // hitting the network, leaving a native session dead until app restart.
            if (!refresh.IsCompleted)
            {
                _refreshInFlight = refresh;
                _refreshInFlightEpoch = _epoch;
            }
            return refresh;
        }
    }

    private async Task<RefreshOutcome> RunRefreshAsync()
    {
        var epoch = _epoch; // the session this refresh belongs to
        try
        {
            var answer = await _transport.RefreshAsync();
            if (answer.Kind == RefreshAnswerKind.NoStoredToken)
            {
                _token.DropToken();
                return RefreshOutcome.Rejected;
            }
            if (epoch != _epoch)
                return await DiscardStaleAsync(answer.Payload);
            if (answer.Kind == RefreshAnswerKind.Rejected)
            {
                await ClearSessionAsync();
                return RefreshOutcome.Rejected;
            }
            if (answer.Kind == RefreshAnswerKind.Unreachable)
                return RefreshOutcome.Unreachable;
            if (!string.IsNullOrEmpty(answer.Payload?.AccessToken))
            {
                await AcceptTokensAsync(answer.Payload);
                return RefreshOutcome.Renewed;
            }

            _logger.LogWarning("Refresh response missing access_token");
            await ClearSessionAsync();
            return RefreshOutcome.Rejected;
        }
        catch (Exception ex)
        {
            // Applying the answer failed (the secure store, a handler of ours): the token's fate is unknown.
            _logger.LogWarning(ex, "Token refresh could not reach the server; keeping the session");
            return RefreshOutcome.Unreachable;
        }
        finally
        {
            // Allow a fresh refresh next time; only *concurrent* calls are coalesced. A stale refresh must not
            // clear a newer session's in-flight one.
            lock (_refreshGate)
            {
                if (_refreshInFlightEpoch == epoch)
                    _refreshInFlight = null;
            }
        }
    }

    // The answer belongs to a session that ended (logout) or was superseded (an impersonation began): the access
    // token is never applied — it would sign the user back in, or swap the impersonation for the staff identity.
    // The server rotated the refresh token regardless; the browser has the new cookie already, and on native the
    // body carried it: keep it while the session is still held (the staff session behind an impersonation, so
    // leaving it can restore them), never after a logout, which emptied the store on purpose.
    private async Task<RefreshOutcome> DiscardStaleAsync(TokenResponse? payload)
    {
        if (_sessionHeld && _sessionStore.UsesBodyTransport && !string.IsNullOrEmpty(payload?.RefreshToken))
            await _sessionStore.SaveRefreshTokenAsync(payload.RefreshToken);
        _logger.LogInformation("Discarded a refresh answer that belonged to a superseded session");
        return RefreshOutcome.Discarded;
    }

    // ── Native sign-in (body tokens) — see NativeSignIn ─────────────────────────────────────────────

    /// <summary>
    /// Completes native OAuth: runs the platform browser flow, exchanges the returned
    /// one-time code for tokens, and stores them. Web hosts sign in by full-page navigation and never call this.
    /// </summary>
    public Task<SignInResult> SignInWithOAuthAsync(string provider, CancellationToken cancellationToken = default) => _native.SignInWithOAuthAsync(provider, cancellationToken);

    /// <inheritdoc cref="NativeSignIn.LinkProviderAsync"/>
    public Task<string?> LinkProviderAsync(string provider, string linkToken) => _native.LinkProviderAsync(provider, linkToken);

    /// <summary>
    /// True while an OAuth browser flow is awaiting its callback in THIS process. The
    /// Android callback activity reads it to tell warm delivery (WebAuthenticator will
    /// complete normally) from a cold start after process death (the callback must be
    /// stashed for the startup resume instead).
    /// </summary>
    public bool OAuthFlowInFlightInProcess => _native.FlowInFlightInProcess;

    /// <summary>
    /// One-shot handoff of a resume outcome the Login page must act on (MFA step-up,
    /// expired stash, failed exchange). Set by <see cref="TryCompletePendingOAuthAsync"/>;
    /// consumed by Login's OnInitialized.
    /// </summary>
    public OAuthResumeResult? TakeOAuthResumeHandoff() => _native.TakeResumeHandoff();

    /// <summary>
    /// Startup entry point (called once from MainLayout, right after <see cref="InitializeAsync"/>): finishes an
    /// OAuth flow the previous process died in the middle of (NATIVE-12).
    /// </summary>
    /// <inheritdoc cref="NativeSignIn.TryCompletePendingOAuthAsync"/>
    public Task<OAuthResumeResult> TryCompletePendingOAuthAsync() => _native.TryCompletePendingOAuthAsync();

    /// <summary>
    /// Verifies an OTP code and establishes the session from the tokens in the response
    /// body. Used by native hosts (the web Login page keeps its cookie + callback flow).
    /// </summary>
    public Task<SignInResult> VerifyOtpAsync(string email, string code) => _native.VerifyOtpAsync(email, code);

    /// <summary>
    /// Completes a native MFA step-up: posts the challenge from a prior login + a TOTP/recovery code and
    /// stores the tokens the API returns in the body. Returns true on success. Web hosts complete the
    /// step-up via the cookie flow (Login page) and don't call this.
    /// </summary>
    public Task<bool> VerifyMfaAsync(string challenge, string code) => _native.VerifyMfaAsync(challenge, code);

    // ── Probes — see AuthProbes ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the signed-in user is platform staff — drives the admin nav link + page gate (ADR-014).
    /// Cached per identity. False when signed out, on any error, or while impersonating.
    /// </summary>
    public Task<bool> IsStaffAsync() => _probes.IsStaffAsync(IsAuthenticated && !IsImpersonating, _token.Token);

    /// <summary>
    /// The OAuth providers the server has actually configured (lowercase, e.g. "google") — the login + settings
    /// pages render only these, so an unconfigured provider shows no dead button (challenging it 500s).
    /// </summary>
    public Task<IReadOnlyList<string>> GetEnabledProvidersAsync() => _probes.GetEnabledProvidersAsync();

    /// <summary>
    /// Whether this deployment has the billing surface switched on (GATES-1, ADR-027) — drives the
    /// billing nav link and the <c>/billing</c> page gate. False on any error: fail closed to "no billing"
    /// rather than render a link into routes that may not exist.
    /// </summary>
    public async Task<bool> IsBillingEnabledAsync() => await ProbeBillingAsync() ?? false;

    /// <summary>
    /// The same probe, with "could not tell" kept apart from "off" (v4 audit BILL-7/UX-13): <c>null</c> when
    /// the probe failed. A caller that would do something visible on "off" (bounce away from <c>/billing</c>,
    /// tell an owner there is no plan to buy) asks this one, so a transient failure on a billing-on deployment
    /// is never acted on as a decision.
    /// </summary>
    public Task<bool?> ProbeBillingAsync() => _probes.ProbeBillingAsync();

    // ── Impersonation (ADR-014) ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// True while an admin "sign in as" session is active — a state entered by <see cref="BeginImpersonation"/>
    /// and left by <see cref="StopImpersonationAsync"/>, by the token's expiry, or by a cleared session. Not a
    /// claim read: that read went silent when the token expired, and the client then renewed into the staff
    /// identity on the target's page (v4 LB-UI-13).
    /// </summary>
    public bool IsImpersonating => _impersonating;

    /// <summary>
    /// Enters an impersonated session using a short-lived admin token (no refresh token — it's
    /// non-refreshable by design). Held in memory only; a reload returns the staff user to their own
    /// identity via the untouched refresh cookie, and expiry ends the impersonation (<see cref="IdentityChanged"/>,
    /// with no token) rather than renewing — the layout then reloads home as the staff user.
    /// </summary>
    public void BeginImpersonation(string accessToken, int? expiresInSeconds = null)
    {
        // The staff session's renewal must not fire under the impersonation: a refresh restores the staff
        // identity, which would silently end the "sign in as". Stopping re-arms it through the refresh. A
        // refresh already on the wire belongs to the staff session: the epoch moves, so its answer is discarded.
        _renewal.Cancel();
        lock (_refreshGate)
        {
            _epoch++;
            _token.Set(accessToken, expiresInSeconds);
            _probes.ForgetStaff();
            // A token without the claim — or one that can't be read, or has already expired — is not an
            // impersonation; it reads as signed out, as any hostile token does.
            _impersonating = _token.Claim(AppClaims.ImpersonatedBy) is not null;
            _impersonationExpiry?.Dispose();
            _impersonationExpiry = _impersonating
                ? _time.CreateTimer(_ => EndExpiredImpersonation(), null, _token.TimeUntilExpiry(), Timeout.InfiniteTimeSpan)
                : null;
        }
        IdentityChanged?.Invoke();
    }

    /// <summary>
    /// Leaves an impersonated session and restores the staff user from their refresh cookie/store.
    /// Returns true when the original identity was restored.
    /// </summary>
    public async Task<bool> StopImpersonationAsync()
    {
        lock (_refreshGate)
        {
            _epoch++;
            _impersonating = false;
            _impersonationExpiry?.Dispose();
            _impersonationExpiry = null;
            _token.Clear();
            _probes.ForgetStaff();
            _sessionHeld = true; // the staff session behind the impersonation is restorable (an expiry had let go of it)
        }
        var restored = await TryRefreshAsync();
        IdentityChanged?.Invoke();
        return restored;
    }

    // The impersonation token expired: the "sign in as" is over. Nothing is renewed — a refresh would put the
    // STAFF identity on the target's page. The in-memory session is let go (the staff session is still in the
    // cookie/store for the next boot) and IdentityChanged tells the layout to reload home as the staff user.
    private void EndExpiredImpersonation()
    {
        lock (_refreshGate)
        {
            if (!_impersonating)
                return;
            _epoch++;
            _impersonating = false;
            _impersonationExpiry?.Dispose();
            _impersonationExpiry = null;
            _token.Clear();
            _sessionHeld = false;
            _probes.ForgetStaff();
        }
        IdentityChanged?.Invoke();
    }

    // ── The session's two doors: tokens in, session out ─────────────────────────────────────────────

    /// <summary>
    /// True while a sign-out the user asked for (<see cref="LogoutAsync"/>) is ending the session. Every caller of
    /// <see cref="LogoutAsync"/> then leaves for /login with a full reload, so a <see cref="SignedOut"/> listener must not
    /// navigate as well: its client-side redirect raced the reload and could rewrite the history entry being left, and
    /// Back no longer reached the page the bfcache guard protects. Read it synchronously in the handler.
    /// </summary>
    public bool SigningOut { get; private set; }

    public async Task LogoutAsync()
    {
        SigningOut = true;
        try
        {
            await _transport.LogoutAsync();
            await ClearSessionAsync();
        }
        finally
        {
            SigningOut = false;
        }
    }

    /// <summary>Sets the in-memory access token and persists the rotated refresh token (native).</summary>
    private async Task AcceptTokensAsync(TokenResponse payload)
    {
        var wasAuthenticated = IsAuthenticated;
        var previousUser = AccessTokenState.SubjectOf(_token.Token);
        _token.Set(payload.AccessToken!, payload.ExpiresIn);
        _sessionHeld = true;
        _renewal.ResetFailures(); // the server is back: the next pause starts from the base again
        _probes.ForgetStaff(); // identity may have changed; re-probe on demand
        // A different account's token must not inherit this one's remembered choice; the same account's renewal
        // keeps it — that renewal may have been on the wire while the choice was made, carrying the OLD claim
        // (v4 UX-17). Sign-out forgets it too (ClearSessionAsync).
        if (previousUser is null || previousUser != AccessTokenState.SubjectOf(_token.Token))
            _remembered.Forget();
        if (_sessionStore.UsesBodyTransport && !string.IsNullOrEmpty(payload.RefreshToken))
            await _sessionStore.SaveRefreshTokenAsync(payload.RefreshToken);
        _renewal.Schedule(_token.RenewalDue(RenewLead));

        if (!wasAuthenticated && IsAuthenticated)
            SignedIn?.Invoke();
    }

    private async Task ClearSessionAsync()
    {
        // "Was signed in" is the held session, not an unexpired token (v4 T33, R84): a rejection that lands after
        // an outage outlasted the token must still raise SignedOut — once — so the device-preference wipe runs.
        var wasSignedIn = _sessionHeld;
        lock (_refreshGate)
        {
            _epoch++; // a refresh on the wire belongs to the session being ended: its answer is discarded
            _token.Clear();
            _sessionHeld = false;
            _impersonating = false;
            _impersonationExpiry?.Dispose();
            _impersonationExpiry = null;
        }
        _renewal.Cancel();
        _probes.ForgetStaff();
        _remembered.Forget();
        if (_sessionStore.UsesBodyTransport)
            await _sessionStore.ClearAsync();
        if (wasSignedIn)
            SignedOut?.Invoke();
    }

    // ── Keeping the session alive ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the session on app startup: with no token in memory, silently
    /// exchanges the HttpOnly refresh cookie for an access token. Called ONCE from
    /// MainLayout — the single auth entry point (mirrors phase2). Because the layout
    /// awaits this before rendering @Body, pages never trigger their own refresh.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (IsAuthenticated) return;
        var outcome = await RefreshAsync();
        // A server still waking up (or a phone still finding signal) is not "signed out": wait and try again
        // before the layout sends the user to the login page. A rejection is final and never retried.
        foreach (var delay in StartupRetryDelays)
        {
            if (outcome != RefreshOutcome.Unreachable) return;
            await Task.Delay(delay, _time);
            outcome = await RefreshAsync();
        }
    }

    /// <summary>
    /// The access token to send with a request, renewed first when it is inside the renewal window or already
    /// expired — the timer's safety net for a device that slept through it, or a renewal that couldn't reach
    /// the server. Null when there is no session (anonymous pages never spend a refresh). Returned as-is while
    /// impersonating: that token is not refreshable, and a refresh would restore the staff identity instead.
    /// Both hosts' bearer handlers call this; so does the layout when the app returns to the foreground.
    /// </summary>
    public async Task<string?> GetFreshAccessTokenAsync()
    {
        var token = _token.Token;
        if (IsImpersonating)
        {
            // Never renewed. Past its expiry the impersonation is over (the timer normally gets there first).
            if (token is null || _token.Expired)
                EndExpiredImpersonation();
            return _token.Token;
        }
        if (!_sessionHeld)
            return token;
        if (token is not null && _token.RenewalDue(RenewLead) > TimeSpan.Zero)
            return token;
        await RefreshAsync();
        return _token.Token;
    }

    /// <summary>
    /// The bearer handlers' second chance (v4 T32, R126): the server answered 401 to a request sent with a token
    /// the client believed live. Renews once and returns the token to resend with — the same one when the server
    /// couldn't be reached (nothing new to send), null when the refresh was rejected (the session is over) or
    /// there is no session to renew. Never while impersonating: that token is not refreshable.
    /// </summary>
    public async Task<string?> RenewAfterRejectedRequestAsync()
    {
        if (!_sessionHeld || IsImpersonating)
            return null;
        return await RefreshAsync() == RefreshOutcome.Renewed ? _token.Token : null;
    }

    // What the keep-alive timer does when it fires (RenewalScheduler only keeps the time).
    private async Task RenewInBackgroundAsync()
    {
        try
        {
            if (!_sessionHeld || IsImpersonating)
                return;
            if (_token.Token is not null && _token.RenewalDue(RenewLead) is var due && due > TimeSpan.Zero)
            {
                _renewal.Schedule(due); // woke early (the wait was capped): not due yet
                return;
            }
            // Renewed re-arms the timer (AcceptTokensAsync); Rejected ends the session (ClearSessionAsync);
            // Discarded means the session moved on — whatever replaced it arms its own timer.
            if (await RefreshAsync() == RefreshOutcome.Unreachable && _sessionHeld && !IsImpersonating)
                _renewal.ScheduleRetry();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background session renewal failed");
        }
    }
}
