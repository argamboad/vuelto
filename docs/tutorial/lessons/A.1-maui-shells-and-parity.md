# Lesson A.1 — MAUI shells & parity (appendix)

> **Appendix · The native shells.** Throughout the course the native apps were *deferred* —
> "web-first for features" (Golden Rule 5), UI components in the shared RCL (Golden Rule 3). This
> appendix cashes those two rules in: because every screen already lives in a Razor Class Library,
> hosting the *same* components in native desktop + mobile shells is comparatively cheap. But
> "cheap" isn't "free" — a WebView is not a browser, and the gaps between them are exactly what
> this lesson is about. The star here isn't code, it's a *method*: a **parity audit** that finds
> every WebView-vs-browser delta systematically, turns each into a tracked gap, and closes it with
> a seam you (mostly) designed web-first. It's how you extend a web app to native *responsibly*
> instead of discovering breakage on a device in front of a customer.

**Goal:** understand how a MAUI Blazor Hybrid shell hosts the same RCL the web app uses; how the
parity audit enumerates and verdicts every WebView-vs-browser difference; the gap register that
drives the native work; and why the native *seams* were designed web-first — plus the smoke
harness that keeps the four platforms from silently rotting.

**Concepts & principles:** RCL reuse (one UI, many hosts); Blazor Hybrid (WebView) vs Blazor WASM
(browser); the parity-audit method (concern × screen, verdicted); the gap register → fix-slice
pipeline; seams designed web-first (why native stays cheap); native-only fixes CSS can't reach; a
compile + smoke CI gate across four platforms.

**Maps to:** ADR-018, NATIVE epic · repo: `src/Maui/MauiProgram.cs`, `src/Maui/wwwroot/index.html`,
`docs/NATIVE_PARITY.md`, `docs/MOBILE_TESTING.md`, `src/Maui/PreferencesCulturePersistence.cs`,
`src/Maui/ShareFileDownloadLauncher.cs`.

**Prerequisites:** 3.3/3.4 (the RCL + web client — the shared UI), 3.5 (`ICulturePersistence` — a
native seam designed web-first), 6.3 (`IFileDownloadLauncher` — likewise), 8.3 (the CI the native
smokes join).

---

## 1. Motivate — the same components, a different host

The web app is Blazor **WASM**: your Razor components run in the browser's WebAssembly runtime,
served as static files (8.1). A MAUI **Blazor Hybrid** app runs those *same* components in a
`BlazorWebView` — a native control embedding the platform's WebView (WebView2 on Windows,
WKWebView on Apple, Android System WebView) — with the component code executing as **native .NET**,
not WASM, and talking to your API over HTTP just like the browser does. Because both hosts consume
the identical RCL (Golden Rule 3), a screen you built once renders on web, Windows, macOS, iOS, and
Android. That's the enormous payoff of having refused to put UI in the web app: the native shells are
mostly *hosting configuration*, not a rewrite.

But "mostly" hides real differences. A WebView is not a browser: it has no address bar, handles
downloads and external navigation differently, can't read the browser's `localStorage` when the
native process boots, and sits inside an OS with a back button, safe-area insets, and a lifecycle the
browser doesn't have. Ship the RCL into a WebView naively and a handful of features silently break —
*silently* being the operative word, because it all compiles and most of it works. The discipline
this appendix teaches is how to find the breakage *before a user does*.

## 2. The shell — hosting the RCL, per platform

`MauiProgram.cs` is the composition root of the native app, and it's strikingly small — it wires the
same abstractions the web host does, with platform-specific implementations:

```csharp
// src/Maui/MauiProgram.cs (essence)
builder.Services.AddMauiBlazorWebView();                              // host the RCL in a WebView
builder.Services.AddLocalization();
builder.Services.AddSingleton<ICulturePersistence, PreferencesCulturePersistence>();  // native impl of a web seam
builder.Services.AddSingleton<IFileDownloadLauncher>(sp => new ShareFileDownloadLauncher(…));  // native impl
builder.Services.AddSingleton<AppResumeNotifier>();                  // fire on window Resumed/Activated
// per-platform OAuth + secure session (Lesson A.2):
builder.Services.AddSingleton<ISessionStore, SecureStorageSessionStore>();
#if ANDROID || IOS || MACCATALYST
    builder.Services.AddSingleton<IOAuthInitiator>(sp => new WebAuthenticatorOAuthInitiator(…));
#elif WINDOWS
    builder.Services.AddSingleton<IOAuthInitiator>(sp => new LoopbackOAuthInitiator(…));
#endif
```

Every native-specific line is an *implementation of an interface the RCL already depends on*. The RCL
says "give me an `ICulturePersistence`" (3.5); the web host binds the localStorage impl, the MAUI
host binds `PreferencesCulturePersistence`. The RCL says "give me an `IFileDownloadLauncher`" (6.3);
web navigates same-tab, native opens the OS share sheet. **The seam is the boundary that makes the
same components behave correctly on both hosts** — and (the whole point of Golden Rule 5) those seams
were designed *while building the web feature*, so the native shell just supplies the other side. A
native-only concept, `AppResumeNotifier`, is fired by the window's Resumed/Activated lifecycle events
so a page can refresh after an external round-trip returns — the browser's redirect makes this a
no-op, so web registers it but never fires it.

## 3. The parity audit — a method, not a guess

The heart of the appendix. `docs/NATIVE_PARITY.md` is a **systematic audit**: it walks every
cross-cutting concern (file download, external navigation, back button, culture, safe areas, session
persistence, auth, …) and every screen, and for each assigns a **verdict**:

- **✅ works** — verified in code / on device;
- **⚠️ gap** — a real WebView-vs-browser difference, mapped to a fix slice;
- **🔍 likely-OK, verify on device** — static analysis can't prove rendering/OS behavior, so it
  becomes a checklist item in the manual QA pass;
- **N-A** — not applicable (e.g. no upload UI exists yet).

The method matters more than any single row. It's *concern × platform × screen*, source-inspected and
then device-verified, so nothing is left to "it probably works." And it's honest about the limits of
static analysis: cells it *can't* prove from code (does the QR actually render? does the share sheet
appear?) are explicitly marked 🔍 and pushed into the device QA plan (8.3), not waved through. A
parity audit is how you convert "we support native" from a hope into an enumerated, verdicted,
tracked claim — the same "make completeness mechanical" instinct as the coverage gate and the RLS
parity gate, applied to cross-platform behavior.

## 4. The gap register — from audit to backlog

The audit's ⚠️ rows roll up into a **gap register** that *drives the native epic* — each gap is a
named slice with a fix sketch. The real ones the audit found are instructive, because each is a
place a WebView diverges from a browser:

- **G1 — file download.** A signed-URL export used `target=_blank`, which Android WebView drops
  silently. Fixed by the `IFileDownloadLauncher` seam (6.3) + serving files as
  `Content-Disposition: attachment`: web downloads same-tab, native fetches the bytes and opens the
  **share sheet**.
- **G2 — external navigation.** Billing checkout opens in the system browser and returns to *web*,
  leaving the native app's billing page stale. Fixed by `AppResumeNotifier` — the billing summary
  refetches when the app foregrounds.
- **G3 — Android back button.** The hardware back button exited the app instead of navigating.
  Fixed by walking the WebView history in `MainPage.OnBackButtonPressed`.
- **G5 — join by invite.** `Join.razor` read the token *only from the query string*, and a native app
  has no address bar — so a native-only member couldn't join. Fixed with a token-entry form (which
  helps the web too, for email clients that mangle links).
- **G6 — culture cold-start.** The language choice was saved to WebView `localStorage`, which C# can't
  read when the native process boots — so it died with the process. Fixed by the `ICulturePersistence`
  seam (3.5): native persists to OS `Preferences`, and `MauiProgram` applies it before first render.
- **G7 — Apple boot crash.** `IOAuthInitiator` was registered only for Android/Windows, but the
  `AuthService` factory resolves it with `GetRequiredService` — so iOS/macCatalyst **crashed at first
  resolve** despite compiling green (nothing ran the Apple builds). Fixed by generalizing the
  WebAuthenticator initiator across Android/iOS/macCatalyst.

G7 is the cautionary tale worth dwelling on: *it compiled, CI was green, and the app was
dead-on-arrival on two platforms* — because "compiles" and "boots" are different claims, and no test
ran the Apple binary. That gap between green-CI and works-on-device is precisely what the audit's 🔍
verdicts and the smoke harness (§6) exist to close.

## 5. Fixes CSS can't reach — the native floor

Some gaps can't be fixed in the shared RCL at all, because they're *below* the web platform. The
sharpest example: Android's edge-to-edge display laid the WebView under the transparent status bar, so
the clock drew over the app header. The instinct is `env(safe-area-inset-top)` in CSS — but **Android
WebView always resolves `env()` to 0** (it's a WKWebView/iOS mechanism). So the fix is *native*:
`MainActivity` pads the content by the status-bar inset via `ViewCompat.SetOnApplyWindowInsetsListener`
and paints the strip brand green. The lesson: a hybrid app has a floor of genuinely
platform-native concerns (insets, back button, window lifecycle, secure storage) that no amount of
shared web code reaches — and recognizing which layer a gap lives in is half of fixing it. The seams
handle the "same behavior, different mechanism" gaps; the native floor handles the "the OS itself is
different" ones.

## 6. Keeping it from rotting — the four-platform smoke

Native support that isn't exercised *rots* — a refactor breaks a platform nobody built locally, and
you find out in a store review. So the CI (8.3) grows a **compile gate for all four TFMs** plus a
**boot smoke** per platform: Windows boots the real MAUI app and probes that it reached the login
screen (it originally drove the UI via WebView2's CDP endpoint, but WebView2 150 stopped exposing
CDP under the elevated CI runner, so the leg was rescoped to a boot-probe); Android runs the full
OTP sign-in journey on a real emulator; iOS-simulator and Mac Catalyst assert boot-to-login
canaries (WKWebView has no CDP, so they prove process-alive + a provider probe returns 200, not
full UI driving). It's
tiered by cost (macOS runners bill 10×, so the Apple legs run only on native-relevant develop pushes)
and gated so docs-only changes skip the native legs entirely. This is the DevOps discipline of Part 8
extended to the hardest-to-test surface: even a canary that only proves "the Apple app still boots to
a login screen" would have caught G7 the day it landed.

> **A red build you'll hit the moment you add `src/Maui` — the arch test vs XAML code-behind.** The
> `SourceFile_DeclaresATypeMatchingItsName` arch test (3.3) — "a file `Foo.cs` should declare a type
> `Foo`" — scans *all* of `src/`, and MAUI's XAML code-behind breaks its naming assumption: the file
> is `App.xaml.cs`, so the naive filename is `App.xaml`, but the type is `App`. Add the MAUI project
> and that test goes red until you teach the arch test to strip the `.xaml` before comparing (one line:
> `if (name.EndsWith(".xaml")) name = name[..^5];`). It's a small fix, but the point is the *pattern* —
> extending the platform to a new file convention (XAML) means the *arch tests* that enforce conventions
> must learn about it too, or they fire on the newcomer. The gate isn't wrong; it's asking you to
> explicitly accommodate the new convention, exactly as `WebhookDelivery` had to be allowlisted in 7.4.
> (A smaller ripple in the same vein: central-managing a new MAUI package can reclassify it in a
> transitive consumer's `packages.lock.json`, so regenerate the lockfiles — the Part-4 discipline.)

## 7. Two maintainer rules the audit surfaced

Worth stating because they're easy to violate silently:

- **The two `index.html` hosts must stay in sync** (`src/Web/wwwroot/index.html` ↔
  `src/Maui/wwwroot/index.html`): any script the RCL depends on (e.g. the MFA QR vendor script) must
  be in *both*, or the feature breaks on one host. (The brand-tokens refactor shrank this surface —
  `app.css` is single-sourced from the RCL — but the vendored scripts remain per-host.)
- **`Nav.NavigateTo(external, forceLoad: true)` means "leave the app" on native.** Any new external
  round-trip (payment, OAuth-like flow) needs a *return-trip story* on native, not just a redirect
  URL — because unlike a browser tab, a native app that navigates away doesn't automatically come
  back to your state.

## 8. Architecture Decision

> **The fork:** how do you build native apps, and how do you keep them at parity? (a) Separate native
> UIs (SwiftUI/Jetpack Compose/WinUI) — a real second app; (b) host the shared RCL in a WebView but
> port each screen ad hoc as breakage is found; (c) host the RCL in MAUI Blazor Hybrid, and drive the
> native work from a *systematic parity audit* → gap register → seams (designed web-first) + a native
> floor for OS-level concerns, guarded by a four-platform compile+smoke CI gate.
>
> **Chosen:** (c) (ADR-018). Reusing the RCL makes native mostly hosting config, not a rewrite; the
> audit converts "we support native" into an enumerated, verdicted, tracked claim; seams designed
> web-first (Golden Rule 5) mean each gap is closed by supplying the other half of an interface that
> already exists; the smoke gate stops silent rot. The native floor is small and explicit.
>
> **Rejected:** (a) — separate native UIs triple the surface to build and keep in sync for an app
> whose UI is inherently web-shaped; the RCL exists precisely to avoid that. (b) — ad-hoc "fix it when
> it breaks" ships breakage to users (G1/G6/G7 were all silent); a systematic audit finds them first.
>
> **The trade:** a WebView isn't a browser, so there's an irreducible native floor (insets, back
> button, lifecycle, secure storage) and a device-QA burden the audit's 🔍 rows make explicit; Apple
> requires real hardware/runners (10× cost) to truly verify. Accepted: one shared UI across five
> targets, with the gaps *known and tracked* rather than discovered by customers, is a far better
> position than five UIs or a pile of surprises.

## 9. Checkpoint

```sh
git add -A && git commit -m "feat: MAUI Blazor Hybrid shells hosting the RCL + parity audit + native seams + smoke gate"
git tag lesson-A.1
```

You should now be able to: explain how a Blazor Hybrid WebView reuses the RCL and why that makes
native cheap; run a parity audit (concern × screen, verdicted) and turn ⚠️ rows into a gap register;
explain why the native seams were designed web-first and give examples (culture, download,
resume-refresh); recognize the native floor (insets/back/lifecycle) CSS can't reach; and justify the
four-platform compile+smoke CI gate.

**Next:** *Appendix A.2 — Native auth bridge* — how the cookie-based web auth becomes secure-storage
+ body-token auth on native, and OAuth behind a per-platform seam.
