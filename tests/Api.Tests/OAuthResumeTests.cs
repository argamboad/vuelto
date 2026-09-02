using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Perezosoft.Shared.Ui.Auth;

namespace Perezosoft.Api.Tests;

/// <summary>
/// NATIVE-12: native OAuth must survive Android killing the app during the browser
/// round-trip. The initiator persists an "in flight" marker before launching the browser;
/// a cold-started callback stashes the redirect URI; on startup
/// <see cref="AuthService.TryCompletePendingOAuthAsync"/> finishes the exchange through the
/// normal native login path. These tests pin the marker lifecycle, the resume outcomes
/// (signed in / MFA / link / error), and the one-time-code TTL guard.
/// </summary>
public class OAuthResumeTests
{
    private const string CallbackBase = "perezosoft://auth";

    // ── fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeResumeStore : IOAuthResumeStore
    {
        public OAuthFlowMarker? Marker { get; private set; }
        public string? PendingCallback { get; set; }

        public void SetInFlight(OAuthFlowMarker marker) => Marker = marker;
        public OAuthFlowMarker? GetInFlight() => Marker;
        public void ClearInFlight() => Marker = null;
        public void SetPendingCallback(string callbackUri) => PendingCallback = callbackUri;

        public string? TakePendingCallback()
        {
            var value = PendingCallback;
            PendingCallback = null;
            return value;
        }
    }

    private sealed class FakeSessionStore : ISessionStore
    {
        public string? Saved { get; private set; }
        public bool Cleared { get; private set; }

        public bool UsesBodyTransport => true;
        public Task<string?> GetRefreshTokenAsync() => Task.FromResult(Saved);

        public Task SaveRefreshTokenAsync(string refreshToken)
        {
            Saved = refreshToken;
            return Task.CompletedTask;
        }

        public Task ClearAsync()
        {
            Cleared = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>Scripted HTTP handler: records requests, answers with the queued response.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            return respond(request, body);
        }
    }

    private sealed class FakeInitiator(Func<Task<IReadOnlyDictionary<string, string>?>> run) : IOAuthInitiator
    {
        public Task<IReadOnlyDictionary<string, string>?> RunBrowserFlowAsync(string provider, string? linkToken = null) => run();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string MakeJwt(TimeSpan lifetime) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims: [new System.Security.Claims.Claim("nameid", Guid.NewGuid().ToString())],
            expires: DateTime.UtcNow.Add(lifetime)));

    private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
    };

    private static (AuthService Auth, FakeResumeStore Store, FakeSessionStore Session, StubHandler Handler, FakeTimeProvider Time) CreateSut(
        Func<HttpRequestMessage, string, HttpResponseMessage>? respond = null)
    {
        var handler = new StubHandler(respond ?? ((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var store = new FakeResumeStore();
        var session = new FakeSessionStore();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-07T10:00:00Z"));
        var auth = new AuthService(
            new HttpClient(handler) { BaseAddress = new Uri("https://unit.test") },
            NullLogger<AuthService>.Instance,
            session,
            oauth: null,
            resumeStore: store,
            timeProvider: time);
        return (auth, store, session, handler, time);
    }

    private static void Stash(FakeResumeStore store, FakeTimeProvider time, string query, string? linkToken = null, TimeSpan? age = null)
    {
        store.SetInFlight(new OAuthFlowMarker("Google", linkToken, time.GetUtcNow() - (age ?? TimeSpan.Zero)));
        store.SetPendingCallback($"{CallbackBase}{query}");
    }

    // ── no pending state ─────────────────────────────────────────────────────

    [Fact]
    public async Task Resume_WithoutStore_IsNone()
    {
        // Web host: no resume store registered — the call must be a silent no-op.
        var auth = new AuthService(
            new HttpClient(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError))) { BaseAddress = new Uri("https://unit.test") },
            NullLogger<AuthService>.Instance,
            new FakeSessionStore());

        var result = await auth.TryCompletePendingOAuthAsync();

        Assert.Equal(OAuthResumeOutcome.None, result.Outcome);
    }

    [Fact]
    public async Task Resume_WithEmptyStore_IsNone()
    {
        var (auth, _, _, handler, _) = CreateSut();

        var result = await auth.TryCompletePendingOAuthAsync();

        Assert.Equal(OAuthResumeOutcome.None, result.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Resume_MarkerWithoutCallback_ClearsSilently()
    {
        // The flow died before any redirect came back (user never finished at the provider).
        var (auth, store, _, handler, time) = CreateSut();
        store.SetInFlight(new OAuthFlowMarker("Google", null, time.GetUtcNow()));

        var result = await auth.TryCompletePendingOAuthAsync();

        Assert.Equal(OAuthResumeOutcome.None, result.Outcome);
        Assert.Null(store.Marker);
        Assert.Empty(handler.Requests);
    }

    // ── sign-in resume ───────────────────────────────────────────────────────

    [Fact]
    public async Task Resume_FreshCode_ExchangesAndSignsIn()
    {
        var jwt = MakeJwt(TimeSpan.FromMinutes(10));
        var (auth, store, session, handler, time) = CreateSut((request, _) =>
            request.RequestUri!.AbsolutePath == "/api/auth/native/exchange"
                ? Json(new { access_token = jwt, refresh_token = "rt-1" })
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        // The code is URL-encoded in the redirect exactly as the API sends it.
        Stash(store, time, "?code=abc%2F123");

        var result = await auth.TryCompletePendingOAuthAsync();

        Assert.Equal(OAuthResumeOutcome.SignedIn, result.Outcome);
        Assert.True(auth.IsAuthenticated);
        Assert.Equal("rt-1", session.Saved);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("/api/auth/native/exchange", request.RequestUri!.AbsolutePath);
        Assert.Contains("abc/123", body); // decoded before the exchange
        Assert.Null(store.Marker);                 // one-shot: state consumed
        Assert.Null(store.PendingCallback);
        Assert.Null(auth.TakeOAuthResumeHandoff()); // success needs no Login-page handoff
    }

    [Fact]
    public async Task Resume_ExpiredStash_FailsWithoutExchange()
    {
        var (auth, store, _, handler, time) = CreateSut();
        Stash(store, time, "?code=abc", age: AuthService.OAuthResumeTtl + TimeSpan.FromSeconds(1));

        var result = await auth.TryCompletePendingOAuthAsync();

        Assert.Equal(OAuthResumeOutcome.Expired, result.Outcome);
        Assert.Empty(handler.Requests); // the one-time code is dead server-side; don't try
        Assert.False(auth.IsAuthenticated);
        var handoff = auth.TakeOAuthResumeHandoff();
        Assert.Equal(OAuthResumeOutcome.Expired, handoff!.Outcome);
        Assert.Null(auth.TakeOAuthResumeHandoff()); // handoff is one-shot
    }

    [Fact]
    public async Task Resume_ExchangeAnswersMfa_HandsChallengeToLogin()
    {
        var (auth, store, _, _, time) = CreateSut((_, _) => Json(new { mfa_required = true, challenge = "ch-42" }));
        Stash(store, time, "?code=abc");

        var result = await auth.TryCompletePendingOAuthAsync();

        Assert.Equal(OAuthResumeOutcome.MfaRequired, result.Outcome);
        Assert.Equal("ch-42", result.Challenge);
        var handoff = auth.TakeOAuthResumeHandoff();
        Assert.Equal(OAuthResumeOutcome.MfaRequired, handoff!.Outcome);
        Assert.Equal("ch-42", handoff.Challenge);
    }

    [Fact]
    public async Task Resume_ErrorCallback_FailsWithoutExchange()
    {
        var (auth, store, _, handler, time) = CreateSut();
        Stash(store, time, "?error=access_denied");

        var result = await auth.TryCompletePendingOAuthAsync();

        Assert.Equal(OAuthResumeOutcome.Failed, result.Outcome);
        Assert.Empty(handler.Requests);
        Assert.NotNull(auth.TakeOAuthResumeHandoff());
    }

    [Fact]
    public async Task Resume_ExchangeRejected_Fails()
    {
        var (auth, store, _, _, time) = CreateSut((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        Stash(store, time, "?code=used-already");

        var result = await auth.TryCompletePendingOAuthAsync();

        Assert.Equal(OAuthResumeOutcome.Failed, result.Outcome);
        Assert.False(auth.IsAuthenticated);
        Assert.NotNull(auth.TakeOAuthResumeHandoff());
    }

    [Fact]
    public async Task Resume_NetworkFailure_FailsGracefully()
    {
        var (auth, store, _, _, time) = CreateSut((_, _) => throw new HttpRequestException("offline"));
        Stash(store, time, "?code=abc");

        var result = await auth.TryCompletePendingOAuthAsync();

        Assert.Equal(OAuthResumeOutcome.Failed, result.Outcome);
        Assert.Null(store.Marker); // state still consumed — no retry loop on next start
    }

    // ── link resume ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Resume_LinkCallback_ReportsLinked()
    {
        // Linking completes server-side at redirect time — resume only reports the outcome.
        var (auth, store, _, handler, time) = CreateSut();
        Stash(store, time, "?linked=Google", linkToken: "lt-1");

        var result = await auth.TryCompletePendingOAuthAsync();

        Assert.Equal(OAuthResumeOutcome.LinkCompleted, result.Outcome);
        Assert.Equal("Google", result.Provider);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Resume_LinkErrorCallback_ReportsError()
    {
        var (auth, store, _, _, time) = CreateSut();
        Stash(store, time, "?error=in_use", linkToken: "lt-1");

        var result = await auth.TryCompletePendingOAuthAsync();

        Assert.Equal(OAuthResumeOutcome.LinkFailed, result.Outcome);
        Assert.Equal("in_use", result.Error);
    }

    // ── marker lifecycle around the warm (in-process) browser flow ───────────

    [Fact]
    public async Task SignIn_PersistsMarkerWhileBrowserFlowRuns_AndClearsIt()
    {
        var (_, store, session, handler, time) = CreateSut((_, _) => Json(new { access_token = MakeJwt(TimeSpan.FromMinutes(10)) }));
        OAuthFlowMarker? seenDuringFlow = null;
        bool? inFlightDuringFlow = null;
        AuthService auth = null!;
        var initiator = new FakeInitiator(() =>
        {
            seenDuringFlow = store.GetInFlight();
            inFlightDuringFlow = auth.OAuthFlowInFlightInProcess;
            return Task.FromResult<IReadOnlyDictionary<string, string>?>(new Dictionary<string, string> { ["code"] = "warm" });
        });
        auth = new AuthService(
            new HttpClient(handler) { BaseAddress = new Uri("https://unit.test") },
            NullLogger<AuthService>.Instance, session, initiator, store, time);

        var result = await auth.SignInWithOAuthAsync("Google");

        Assert.Equal(SignInStatus.Success, result.Status);
        Assert.Equal("Google", seenDuringFlow!.Provider);
        Assert.Null(seenDuringFlow.LinkToken);
        Assert.True(inFlightDuringFlow);           // the callback activity checks this to
        Assert.False(auth.OAuthFlowInFlightInProcess); // tell warm delivery from a cold start
        Assert.Null(store.Marker);                 // cleared the moment the flow returns
    }

    [Fact]
    public async Task SignIn_ClearsMarker_WhenBrowserFlowThrows()
    {
        var (_, store, session, handler, time) = CreateSut();
        var initiator = new FakeInitiator(() => throw new InvalidOperationException("boom"));
        var auth = new AuthService(
            new HttpClient(handler) { BaseAddress = new Uri("https://unit.test") },
            NullLogger<AuthService>.Instance, session, initiator, store, time);

        var result = await auth.SignInWithOAuthAsync("Google");

        Assert.Equal(SignInStatus.Failed, result.Status);
        Assert.Null(store.Marker);
        Assert.False(auth.OAuthFlowInFlightInProcess);
    }

    [Fact]
    public async Task Link_PersistsMarkerWithLinkToken()
    {
        var (_, store, session, handler, time) = CreateSut();
        OAuthFlowMarker? seenDuringFlow = null;
        var initiator = new FakeInitiator(() =>
        {
            seenDuringFlow = store.GetInFlight();
            return Task.FromResult<IReadOnlyDictionary<string, string>?>(new Dictionary<string, string> { ["linked"] = "Google" });
        });
        var auth = new AuthService(
            new HttpClient(handler) { BaseAddress = new Uri("https://unit.test") },
            NullLogger<AuthService>.Instance, session, initiator, store, time);

        var error = await auth.LinkProviderAsync("Google", "lt-9");

        Assert.Null(error);
        Assert.Equal("lt-9", seenDuringFlow!.LinkToken);
        Assert.Null(store.Marker);
    }
}
