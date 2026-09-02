# Lesson A.2 — Native auth bridge (appendix)

> **Appendix · The native shells — finale.** The web auth model (2.7) leans on two
> browser-specific things: an `HttpOnly` cookie holds the refresh token, and the app and API share
> an origin (8.1) so that cookie is sent automatically. A native app has *neither* — no cookie jar
> tied to your origin, no same-origin guarantee, no browser to run the OAuth redirect dance. So the
> auth system needs a **bridge**: keep the exact same tokens and flows, but swap the browser-shaped
> transport for native equivalents — the OS secure store instead of a cookie, a request header
> instead of same-origin, and a per-platform OAuth initiator instead of the browser. The elegant
> part is how *little* changes: the API grew one seam for this back in Part 2, and the RCL never
> learns it's running native.

**Goal:** understand how the refresh token moves from an `HttpOnly` cookie to the OS secure store;
how the API's dual transport (cookie for web, body for native) is selected by a header; how OAuth
runs on each platform behind one interface; and why native MFA step-up needs no new work.

**Concepts & principles:** secure storage as the native cookie-equivalent; body-token transport vs
cookie transport (one API, two transports, header-selected); the OAuth-initiator seam (loopback vs
custom-scheme); the two-HttpClient pattern (auth vs Bearer); every-platform-registers-one
(fail-fast DI); the auth choke point reused (MFA step-up for free).

**Maps to:** ADR-018, NATIVE epic · repo: `src/Maui/Auth/SecureStorageSessionStore.cs`,
`src/Maui/Auth/WebAuthenticatorOAuthInitiator.cs`, `LoopbackOAuthInitiator.cs`,
`NativeAuthHeaderHandler.cs`, `src/Maui/MauiProgram.cs`.

**Prerequisites:** 2.7 (JWT access + refresh-cookie rotation — the model being bridged), 2.5 (OAuth
providers), 6.5 (MFA step-up choke point — reused here), A.1 (the shell hosting this).

---

## 1. Motivate — the browser assumptions native breaks

The web auth design is deliberately browser-shaped (2.7): a short-lived access token lives in memory
in the WASM app, and a long-lived refresh token lives in an `HttpOnly` cookie the browser attaches
automatically to the same-origin silent-refresh call. That's secure *because* of the browser — an
`HttpOnly` cookie is unreadable by JavaScript (XSS-resistant), and same-origin (8.1) means it's sent
without any code. A native app breaks all three assumptions: there's no `HttpOnly` cookie mechanism
tied to your API's origin, the WebView's cookie jar isn't a place you'd trust a refresh token
anyway, and native HTTP calls aren't "same-origin" with anything. And OAuth — a redirect dance
designed for a browser — has no browser to run in.

So you bridge, without changing the *substance*. The tokens are the same, the rotation is the same,
the API endpoints are the same; only the *transport* of the refresh token and the *mechanism* of
OAuth change. The bridge has three pieces: where the refresh token lives (§2), how it travels (§3),
and how OAuth runs (§4).

## 2. Secure storage — the native `HttpOnly` cookie

The refresh token moves from a cookie to the **OS secure store**:

```csharp
// src/Maui/Auth/SecureStorageSessionStore.cs
public sealed class SecureStorageSessionStore : ISessionStore
{
    public bool UsesBodyTransport => true;   // ← the refresh call carries the token in the body (§3)
    public async Task<string?> GetRefreshTokenAsync()
    {
        try { return await SecureStorage.Default.GetAsync("refresh_token"); }
        catch { return null; }   // a corrupted/locked store reads as "no session", never a crash
    }
    public Task SaveRefreshTokenAsync(string t) => SecureStorage.Default.SetAsync("refresh_token", t);
    public Task ClearAsync() { SecureStorage.Default.Remove("refresh_token"); return Task.CompletedTask; }
}
```

`SecureStorage` is MAUI's abstraction over each OS's hardware-backed secret store —
**DPAPI on Windows, Keychain on Apple, Keystore on Android**. Its doc comment names the analogy
exactly: *"the native equivalent of the web's HttpOnly cookie — it's what lets the desktop app stay
signed in across restarts."* Same security property (a secret the rest of the app/other apps can't
read), different substrate (OS keystore vs browser cookie). The defensive `catch` is a small correct
touch: a locked or corrupted store yields "no session" (re-login), never a crash on boot. And it's
behind an `ISessionStore` interface (defined in the RCL/shared auth) — so `AuthService` calls
`GetRefreshTokenAsync()` and neither knows nor cares whether the token came from a cookie or the
Keychain.

## 3. Body-token transport — one API, two transports, header-selected

If the refresh token is in the Keychain (not a cookie), the refresh *call* must carry it somewhere —
so native sends it in the request **body** instead of relying on an auto-attached cookie. The API
supports *both* transports and picks based on a header the native clients set:

```csharp
// both native HttpClients set this — it tells the API "use body-token transport, not cookies"
client.DefaultRequestHeaders.Add("X-Native-Client", "true");
```

On the API side, the refresh endpoint (2.7) reads the refresh token from the cookie for a web request
and from the request body for an `X-Native-Client` request, issuing a rotated token back the same way
(a `Set-Cookie` for web, a body field for native). One endpoint, one rotation logic, two transports —
the *dual-transport seam* the API grew in Part 2 precisely so native could plug in later without a
parallel auth stack. `UsesBodyTransport` on the session store (§2) is what the client-side auth flow
keys off to choose body-vs-cookie. This is the bridge's cleanest idea: the *protocol* (rotating
refresh tokens, reuse detection) is identical across web and native; only the envelope differs, and a
single header selects it.

The native shell wires **two HttpClients** (mirroring the web host) to avoid a DI cycle:

- an **auth client** with *no* Bearer handler — refresh/logout/otp are anonymous or carry the body
  token, so they need no access token (and the Bearer handler would depend on the very service that
  owns them);
- a **default client** whose `NativeAuthHeaderHandler` attaches the in-memory JWT as a `Bearer`
  header, so `[Authorize]` endpoints (household, settings) work.

Both carry `X-Native-Client`. This two-client split is the same shape the web host uses (Api /
ApiAuth) — parity in the plumbing, not just the UI.

## 4. OAuth — a per-platform seam behind one interface

OAuth is a browser redirect flow; native has no browser in-process, so each platform launches an
*external* browser and captures the callback differently — behind one interface the RCL depends on:

```csharp
// src/Maui/MauiProgram.cs — every platform registers an IOAuthInitiator
#if ANDROID || IOS || MACCATALYST
    builder.Services.AddSingleton<IOAuthInitiator>(sp => new WebAuthenticatorOAuthInitiator(…));  // custom scheme
#elif WINDOWS
    builder.Services.AddSingleton<IOAuthInitiator>(sp => new LoopbackOAuthInitiator(…));          // loopback listener
#endif
```

- **Windows** uses a **loopback HTTP listener**: it opens the system browser at the provider, and a
  tiny local `http://127.0.0.1:<port>/` server captures the redirect. That listener accepts *whatever*
  hits its port, so the flow carries a per-request random **`state` nonce** (v3 NAT-9): the app puts it
  on the login URL, the API threads it through the provider round-trip and echoes it on the redirect
  (`NativeAuthUrls`), and the app rejects any callback whose state doesn't match — otherwise a local
  process or a malicious page could inject an attacker's `?code=` and sign you into *their* account.
- **Android/iOS/macCatalyst** use **`WebAuthenticator`** with a **custom URL scheme**
  (`perezosoft://auth`): the OS routes the provider's redirect back to the app via a registered
  scheme (Android intent-filter, Apple `CFBundleURLSchemes`).

Both sit behind `IOAuthInitiator`, so `AuthService` and the `Login` page stay **platform-agnostic** —
they call `InitiateAsync(provider)` and get back a result, never knowing which mechanism ran. Two
things make this robust. First, the loopback/custom-scheme choice is exactly why `MauiProgram` targets
`localhost` for the Android dev API (not `10.0.2.2`): Google/Microsoft accept `localhost` as a redirect
host but reject raw IPs, so the *same* registered `redirect_uri` works for web, desktop, and Android.
Second — the G7 lesson from A.1 — **every platform must register an initiator**, because the
`AuthService` factory resolves it with `GetRequiredService`: a missing `#if` branch is not a
compile error, it's a *crash at first resolve* (how iOS/macCatalyst were dead-on-arrival until the
initiator was generalized across the three custom-scheme platforms). Fail-fast DI turns a missing
implementation into an immediate, obvious failure — but only if you actually *run* the platform,
which is why the smoke harness (A.1 §6) matters.

### Surviving process death mid-round-trip (NATIVE-12)

The external-browser flow has a failure mode the desktop never taught you: while the user is off in
the consent tab, **your app is backgrounded — and the OS is free to kill it**. `WebAuthenticator`'s
pending state is in-memory, so on Android the kill used to be fatal: the `perezosoft://` redirect
cold-started a fresh process with no idea a sign-in was in flight — the app flashed open and closed,
and the one-time code died with it. (Seen live on a tablet emulator; Custom Tabs shrink the window
but can't close it.)

The fix is a persistence bracket around the round-trip, behind one more optional seam:

- **`IOAuthResumeStore`** (RCL) — before launching the browser, `AuthService` stashes a marker
  (provider, optional link token, started-at). MAUI implements it on OS `Preferences`
  (`PreferencesOAuthResumeStore`); web passes `null` and the whole mechanism no-ops.
- **The cold-started callback** — Android's `WebAuthenticatorCallbackActivity` detects the
  no-pending-auth case, stashes the full redirect URI beside the marker, and relaunches
  `MainActivity` instead of dying.
- **`TryCompletePendingOAuthAsync`** — on the next startup (`MainLayout`, before the preference
  reconcile), `AuthService` finds marker + stashed callback, finishes the code exchange, and hands
  outcomes to the UI: signed in, an MFA challenge (the choke point again — §5), an expired stash
  (5-minute TTL, matched to the code's own lifetime), or a friendly failure. One-shot semantics:
  the marker is cleared *before* acting on it, so a crash during resume can't loop.

Two design notes worth stealing. The seam is an **optional constructor parameter** defaulting to
null — the web build carries zero native machinery, same trick as `IOAuthInitiator`. And the resume
completes through the same `CompleteFromResponseAsync` path as a live sign-in, so everything wired
to that choke point (MFA step-up, the `SignedIn` event, session persistence) works on the resumed
path *by construction* — no parallel code path to forget. The logic is unit-tested from `Api.Tests`
(`OAuthResumeTests` — the RCL's first unit coverage, written before the RCL had a test host of its
own; the bUnit component chassis in `tests/Ui.Tests` arrived later, in the v3 audit), and the
on-device kill drill is QA-AND-15.

## 5. MFA step-up — free, because of the choke point

Here's the reward for a decision made three parts ago. Native MFA step-up (MFA-4) required *no new
step-up logic*, because every sign-in path — web *and* native — funnels through the one
session-issuing choke point (`MfaLoginService.CompleteOrChallengeAsync`, 6.5). The native OAuth/OTP
paths hit that same choke point, so if the user has MFA on, they get the same challenge, verified the
same way; the native shell only had to render the challenge UI (which it gets free from the RCL) and
carry the challenge/token over body transport. The `native` bool threaded through the flow (6.5) just
picks body-vs-cookie for the issued session. This is the single-choke-point design (6.5) paying its
largest dividend: *"enforced on every sign-in path"* included the native paths **by construction**,
so a whole platform's MFA came almost for free. A design that had checked MFA per-path would have
needed a fresh, forgettable check for each native path — the exact bug the choke point prevents.

## 6. Impersonation & session restore — the same tokens, restored natively

Two more flows fall out of the bridge with no special-casing. **Session across restart**: on boot the
app reads the refresh token from `SecureStorageSessionStore` and does a body-transport refresh — the
native equivalent of the browser's silent refresh, so the user stays signed in across app restarts.
**Admin impersonation** (7.5): "Stop impersonating" calls refresh, whose native branch restores the
staff identity from secure storage — the `impersonated_by` banner reads the same claim as web. Both
work because the bridge kept the *tokens and flows* identical and only swapped transport — so anything
built on the tokens (restore, impersonation, MFA) rides along.

## 7. Architecture Decision

> **The fork:** how does native authenticate, given no `HttpOnly` cookie and no in-process browser?
> (a) A separate native auth stack (different endpoints, different token model); (b) store the refresh
> token in WebView `localStorage`/a cookie and reuse the web flow as-is; (c) a *bridge* — the same
> tokens/flows/endpoints, with the refresh token in the OS secure store, a header-selected body
> transport, and OAuth behind a per-platform `IOAuthInitiator` seam.
>
> **Chosen:** (c) (ADR-018). The security *substance* (rotating refresh tokens, reuse detection, the
> MFA choke point) is identical to web; only browser-shaped *transport* is swapped for native
> equivalents (Keychain ≈ HttpOnly cookie, `X-Native-Client` body ≈ same-origin cookie, per-platform
> OAuth ≈ browser redirect). One API serves both via the dual-transport seam built in Part 2; the RCL
> stays host-agnostic behind `ISessionStore`/`IOAuthInitiator`; MFA and impersonation come free.
>
> **Rejected:** (a) — a parallel native auth stack doubles the security-critical surface and drifts
> from web (a fix to reuse-detection would need doing twice); the bridge keeps *one* auth system. (b)
> — `localStorage`/WebView cookies are readable and not hardware-backed; putting a long-lived refresh
> token there trades the cookie's `HttpOnly` protection for an exfiltration risk, on the most
> sensitive token you hold.
>
> **The trade:** the bridge needs an OAuth implementation per platform (loopback vs custom-scheme) and
> a dual-transport code path in the API — real surface, and every platform *must* register an
> initiator or crash (fail-fast, but only caught by actually running it). Accepted: it's dramatically
> less than a second auth stack, it keeps the security model single-sourced, and the fail-fast + smoke
> gate turn "forgot a platform" into a loud failure rather than a silent hole.

## 8. Checkpoint

```sh
git add -A && git commit -m "feat: native auth bridge — secure-storage refresh token, body-token transport, per-platform OAuth seam"
git tag lesson-A.2
```

**Appendix complete — and the course with it.** From an empty solution to a production-grade,
multi-tenant SaaS platform running on web and four native targets, sharing one UI and one auth
system, every line rebuilt and every decision understood. The native shells cost so little precisely
because of choices made chapters earlier — UI in the RCL, features web-first, seams at every host
boundary, one auth choke point. That's the deepest lesson of the whole course: *the architecture you
choose early is what makes the hard things cheap later.*

You should now be able to: explain how secure storage replaces the `HttpOnly` cookie and body
transport replaces same-origin; describe the header-selected dual transport and the two-client
pattern; contrast loopback vs custom-scheme OAuth behind one seam and why every platform must register
one; and explain why native MFA step-up, session restore, and impersonation needed almost no new code.

**The end.** You've built it all — now go make it yours.
