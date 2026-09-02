# Stories — Native (MAUI) client feature parity (`NATIVE`)

> One file per epic. Brings the **MAUI Blazor Hybrid** clients (Android, Windows, **iOS, macOS**) to
> **full parity** with web: every feature verified working on native, WebView-specific gaps closed, the
> native build + UI tested in CI. Design decision + accepted costs in **ADR-018**. Stories use Gherkin
> acceptance criteria. **Status: ✅ EPIC COMPLETE (2026-07-14) — the NATIVE-6 Android + Windows
> device pass came back green, closing the last platform slice.** (NATIVE-12 landed after as a
> QA-finding hardening slice, PR #172; its QA-AND-15 on-device drill is the open device item.)
> **Scope change (ADR-024, 2026-07-14):** signed artifacts + store submission (Wave 4, NATIVE-8..11)
> are **downstream-app work**, kept below as reference only.

**Epic key:** `NATIVE`

**The nature of the work (read first).** MAUI here is **Blazor Hybrid** reusing the shared RCL
(`Shared.Ui`), so the native shells already render *every* web screen inside a native WebView, and auth
is already native (OTP, OAuth via system browser, MFA step-up MFA-4, secure-storage tokens). So this epic
is **mostly verification + native-glue + CI/distribution plumbing, not rebuilding features.** The work is:
(1) guard the native build, (2) close the handful of places a WebView differs from a browser, (3) verify
the full feature surface on every platform, (4) test it automatically. ((5) shipping signed artifacts
was the original fifth step — moved downstream per ADR-024; Wave 4 below is now reference material.)

**Prerequisites (external — the platform's real costs, per ADR-018):**
- A **macOS CI runner** (GitHub-hosted `macos-latest`) — required to build/test **iOS + macCatalyst**.
- Android SDK + the `.NET maui` workloads on the runners.
- ~~An Apple Developer account ($99/yr) + signing material as repo secrets~~ — **downstream-release
  prerequisites now (ADR-024)**; each app brings its own identity (Android keystore, Windows/MSIX
  publisher, Apple certs + profiles — base64 in *its* repo secrets, ADR-001 discipline).

**Current baseline:** targets `net10.0-android` + `net10.0-windows`; not built in CI; QA covers 13
auth-focused desktop/Android cases (`QA-DSK-01..07`, `QA-AND-01..06`). See `docs/MOBILE_TESTING.md`.

---

## Wave 1 — Guardrails (build gate + audit)

### NATIVE-1 — Build MAUI in CI, all target platforms

**Status: ✅ Implemented** (`feat/native-1-ci-build-gate`). `net10.0-ios` + `net10.0-maccatalyst` TFMs
restored, **host-conditional** (iOS/MacCatalyst only on macOS hosts, mirroring the Windows pattern — a
Windows box without a paired Mac can't build them, and `dotnet build src/Maui` with no `-f` must never
try a target the host can't produce; the `Platforms/iOS` + `Platforms/MacCatalyst` scaffolds were already
in git). Two ci.yml jobs: **`native-build`** (matrix: Android on `ubuntu-latest` + JDK 17, Windows on
`windows-latest`; `dotnet workload install maui-<platform>` → compile-only `dotnet build -f <tfm>`) runs
on every trigger; **`native-build-apple`** (iOS + MacCatalyst on `macos-latest`) runs on **develop pushes
only** — a free-tier amendment: macOS runners bill **10×** minutes on a private repo, so per-PR Apple
builds would burn the quota for little added signal (rot is still caught within one merge). **Lockfile
resolved by documented exclusion:** `RestorePackagesWithLockFile=false` for the Maui project — the
host-conditional TFM list makes the resolved graph differ per OS, so one committed lockfile can never
satisfy locked-mode on all three runners; CPM alone pins its versions. (Side-benefit: kills the
recurring untracked `src/Maui/packages.lock.json`.) Android + Windows verified locally; Apple legs
verified on the post-merge develop run.

**As a** maintainer
**I want** the MAUI app compiled in CI on every PR, for every target platform
**So that** a change that breaks the native build fails the PR instead of rotting silently until a manual run

**Context / notes:** add `net10.0-ios` + `net10.0-maccatalyst` to the target frameworks. A CI matrix:
Android on `ubuntu-latest`, Windows on `windows-latest`, iOS + macCatalyst on `macos-latest`
(`dotnet workload install maui-*`, then `dotnet build src/Maui -f <tfm>`). Compile-only — no emulator, no
signing (that's Wave 3/4). CPM lockfile: MAUI's `packages.lock.json` is currently **excluded** from CI
(it regenerates on full-solution builds) — this slice brings it under control or documents the exclusion.

**Acceptance criteria**

```gherkin
Scenario: The native build is a required check
  Given a PR that breaks the MAUI build (any target platform)
  When CI runs
  Then the native-build job fails and blocks the merge

Scenario: All four platforms compile
  Then android, windows, ios, and maccatalyst each build in CI on their respective runners
```

**Out of scope:** running the app; signing; tests. **DoD:** matrix builds green on a clean PR; MAUI
lockfile handled deterministically; ADR-018 referenced.

### NATIVE-2 — Native-concerns audit → `docs/NATIVE_PARITY.md`

**Status: ✅ Implemented** (`docs/native-2-parity-audit`). Source-inspection audit at `develop@198319d`
covering every concern below + all shared-RCL screens (incl. the post-plan BILLING-8 billing page and
ADMIN-3 announcements). **Six gaps registered (G1–G6), each mapped to a Wave-2 slice**; uncertain cells
marked 🔍 for the NATIVE-6 device pass (all iOS/macCatalyst cells implicitly 🔍 — never run yet).
Highlights: G1 GDPR-export `target=_blank` download dead-ends the WebView (→ NATIVE-3); G3 Android
hardware back exits the app (→ NATIVE-4); **G5 (discovered): a native member cannot join a household** —
`Join.razor` reads the token from the query string only, no manual entry, and invite emails link to the
web origin (→ **new slice NATIVE-4b**: token-entry input on `/join`, benefits web too); G6 language
switching is inert on native (no culture bootstrap in `src/Maui`; → NATIVE-5). Confirmed-working: MFA QR
scripts included in both `index.html` hosts, native refresh/impersonation-stop, OAuth callback scheme,
bell polling (C# `PeriodicTimer`), secure-storage sessions; magic link correctly hidden on native.
Also produced three **maintainer rules** (keep the two `index.html` hosts in sync; emailed links land on
web — flows need an in-app path; `forceLoad` external nav = leaving the app).

**As a** developer
**I want** a written matrix of every place WebView-hosted Blazor differs from browser Blazor
**So that** Wave 2 fixes real, enumerated gaps instead of guessing

**Context / notes:** produce `docs/NATIVE_PARITY.md` — a table over: file **download/upload/share**,
external links / `mailto` / `target=_blank`, Android **hardware back**, deep links / OAuth callback,
clipboard, **culture/RTL**, **safe areas / status bar / window sizing**, session-across-restart, cold
networking (dev `adb reverse` vs prod URL), and per **feature** (household, invites, settings, notifications,
MFA, admin, GDPR export, files). Each cell: ✅ works / ⚠️ gap / N-A, with a note. This is the scoping artifact.

**Acceptance criteria**

```gherkin
Scenario: Every WebView-vs-browser delta is enumerated
  Then docs/NATIVE_PARITY.md lists each concern × platform with a done/gap/N-A verdict
  And each ⚠️ gap maps to a Wave-2 slice (or is explicitly deferred)
```

**Out of scope:** fixing the gaps (Wave 2). **DoD:** the matrix is complete and drives the Wave-2 backlog.

---

## Wave 2 — Close the WebView gaps (each built only if the audit confirms it)

### NATIVE-3 — File download / upload / share in the WebView

**Status: ✅ Implemented (download half)** (`feat/native-3-download-bridge`). Two-part fix, and it
improved **web** too: the audit found the signed URL was served **inline** (no Content-Disposition), so
even the browser flow showed raw JSON in a `_blank` tab rather than downloading. (1) `FilesController`
now serves signed files with `Content-Disposition: attachment`, named by the storage key's basename
(server-controlled). (2) The export anchor became a reusable **`IFileDownloadLauncher`** seam:
`BrowserFileDownloadLauncher` (web) navigates same-tab — a real download, the page stays, no
popup-blocker exposure; `ShareFileDownloadLauncher` (MAUI) fetches the bytes to the app cache and opens
the **OS share sheet** (core MAUI `Share`, all four targets; filename from the header, sanitized to a
basename). TDD: red-first `FilesControllerTests.ValidToken_SetsAttachmentFilenameFromKeyBasename` +
new E2E `Owner_Downloads_The_Data_Export` (asserts a real browser download lands as `.json` and the
page is not navigated away); suite 28→29, all green. **Upload half stays N-A** per the audit — no UI
consumer of `IFileStorage` upload exists yet; build it with the first feature that needs an uploader.
Share-sheet UX gets its device pass in NATIVE-6.

**As a** native user
**I want** downloads (GDPR export, future attachments/avatars) and uploads to work
**So that** file features aren't silently broken on native (a browser download won't "just happen" in a WebView)

```gherkin
Scenario: Download a signed-URL file on native
  Given a signed download URL (e.g. the GDPR export)
  When I trigger it in the native app
  Then the file is saved/shared via the platform (not a dead WebView navigation)

Scenario: Upload a file on native
  When a feature needs a file picker
  Then the native picker opens and the file uploads through the same API
```

**DoD:** download + upload verified on Android + one desktop target; a reusable native file bridge; ADR-018.

### NATIVE-4 — External links, mailto, and back-navigation

**Status: ✅ Implemented** (`feat/native-4-back-and-return`). **G3 (Android back):**
`MainPage.OnBackButtonPressed` walks the WebView history (which contains Blazor's pushState route
changes) via `CanGoBack()`/`GoBack()`, falling through to the default exit only at the history root.
**G2 (external return-trip):** external opens were already correct (BlazorWebView's default
`UrlLoading` hands external hosts to the system browser; the provider's redirect landing on web is
accepted by design until https App Links — G4). The stale-page half is fixed with a small
`AppResumeNotifier` seam in the RCL: MAUI fires it from the window's `Resumed` **and** `Activated`
lifecycle events (Android returns from the browser via onStop→Resumed; desktop only loses focus, so
the return is Activated), and `/billing` subscribes to refetch its summary on return — web registers
the notifier but never fires it (its return path is a full redirect). **Windows verification** (CDP +
API request log): launch-Activated→Notify proven from the lifecycle log; Notify→billing-refetch proven
live (timer-fired Notify produced matching `GET /api/billing` requests); a genuine focus transition
can't be synthesized from a background shell (Windows foreground-lock), so the human-click case +
Android land in NATIVE-6. Web regression: full E2E 29/29 green. `mailto:` cells were already ✅ (none
exist in the RCL).

**As a** native user
**I want** external links to open in the system browser and the Android back button to behave
**So that** the app doesn't trap me in the WebView or dead-end on a `target=_blank`

```gherkin
Scenario: External link opens the system browser
  When I tap a target=_blank or mailto link
  Then it opens outside the WebView, and in-app navigation stays in the app

Scenario: Android hardware back
  When I press the device back button
  Then it navigates the in-app history, and exits only at the root
```

**DoD:** link routing + hardware-back verified on Android; ADR-018.

### NATIVE-4b — Join a household by invite code (audit gap G5)

**Status: ✅ Implemented** (`feat/native-4b-join-by-code`). Bare `/join` (no `?token=`) now renders an
invite-code entry form instead of the old "invalid link" dead-end — the entry point already existed (the
Household page's "Have an invite?" button links to `/join`), so the whole fix is in `Join.razor`: the
accept call is shared between the URL-token path (unchanged) and the pasted-code path; failures on the
form are inline + retryable (the URL path keeps its terminal error state). Unauthenticated visitors get
the existing sign-in-first redirect for both paths. No API change — the email already carries the raw
token as a copy fallback. EN+ES strings added (`Join_EnterCode*`); the now-unreachable
`Join_MissingToken`/`Join_InvalidTitle` strings removed. TDD: two new Playwright journeys
(`Member_Joins_By_Pasting_The_Invite_Code`, `Pasting_An_Invalid_Code_Shows_An_Inline_Error`) written
red-first; suite 26→28, full local run green. Android device verification lands in NATIVE-6.

**As a** native user invited to a household
**I want** to enter the invite code from the email directly in the app
**So that** I can join at all — the emailed `/join?token=…` link opens the web app, and a native app has
no address bar to reach it

**Context / notes:** discovered by the NATIVE-2 audit: `Join.razor` reads the token **only** from the
query string and invite emails link to `Auth:AppBaseUrl` (web); the email's raw-token fallback has
nowhere to be pasted. Add a token-entry input on `/join` (reachable from the app, e.g. via Household or
the nav) — this benefits **web** too (email clients that mangle links). Optional later: https App
Links/Universal Links.

```gherkin
Scenario: Join with a pasted invite code
  Given I received an invitation email
  When I open the app's Join screen and paste the invite code
  Then I join the household exactly as the emailed link would have

Scenario: Web keeps working
  When I open the emailed /join?token=… link in a browser
  Then the flow is unchanged
```

**DoD:** token entry verified on Android + web regression (E2E roster journey still green); ADR-018.

### NATIVE-5 — Localization + theming/layout polish per platform

**Status: ✅ Implemented** (`feat/native-5-culture-bootstrap`). The audit's G6 wording was corrected
during the build: the runtime switch already worked on native (the switcher sets the in-process culture
before the WebView reload) and signed-in users were already reconciled to their server locale by
`MainLayout` — the real gap was **cold-start persistence for anonymous users** (the choice lived in
WebView localStorage, unreadable from C# at native startup). Fix: `ICulturePersistence` seam in the RCL
(web impl → the same localStorage key, behavior unchanged; MAUI impl → OS `Preferences`) and a
`MauiProgram` bootstrap that applies the saved culture before first render (OS-culture fallback, per
`docs/LOCALIZATION.md`). The web bootstrap also gained a `CultureNotFoundException` guard.
**Verified on real native Windows** by driving the app's WebView2 over CDP
(`--remote-debugging-port`): EN → switch ES (renders Spanish) → kill → cold start → **still Spanish**
on `/login` (anonymous page, OS culture en-US — only the new bootstrap explains it), then switched back
to EN. Desktop window sizing 🔍 resolved for Windows (usable ~1140×571 default). Web regression: full
E2E 28/28 + Core/Api suites green. Android/RTL/safe-area cells stay with NATIVE-6 (RTL has no shipped
language; safe-areas need a device).

**As a** native user
**I want** device culture (incl. RTL) and correct safe-areas / status bar / window sizing
**So that** the app looks and reads right on each platform, matching web

```gherkin
Scenario: Device locale drives the UI
  Given the device is set to Spanish
  Then the app renders in Spanish (matching web i18n), RTL where applicable

Scenario: Platform chrome is correct
  Then mobile respects safe areas + status bar, and desktop opens at a sensible window size
```

**DoD:** verified on Android + Windows (+ iOS/macOS once runners exist); ADR-018.

---

## Wave 3 — Verification (manual + automated)

### NATIVE-6 — Native QA pass: expand the QA plan to the full feature surface

**Status: ✅ Authored** (`docs/native-6-qa-plan`) — **execution pending a device pass** (needs the
maintainer's hardware; Apple cases need a Mac). `docs/QA_TEST_PLAN.md` grew 96→117 cases:
**QA-DSK-08..14** (desktop per-feature parity: join-by-code, culture persistence, export share,
billing return-refresh, MFA native step-up, bell/prefs, admin console), **QA-AND-07..13** (the same
plus the Android-only hardware back 🔴 and share sheet, and the Android-15 edge-to-edge check that
resolves the audit's 🔍 safe-area cell), the first-ever **iOS/macCatalyst smoke** (§13b,
QA-IOS-01..04 + QA-MAC-01..03 — G7 boot, OTP, core-flow spot, first OAuth run), and a per-release
**native release checklist** (§13c). Traceability matrix + per-client coverage + release gate updated;
both QA PDFs regenerated (B11-8 gate green). **Authoring found G7** — iOS/macCatalyst crashed at boot
(no `IOAuthInitiator` registered; `GetRequiredService` throws) — fixed separately
(`fix/native-6a-apple-boot`): the WebAuthenticator initiator generalized to
Android+iOS+macCatalyst + the callback scheme registered in both Apple Info.plists. The slice
completes when the pass is **run**: results go in the §16 sign-off (run log).

**As a** QA tester
**I want** per-feature native cases (not just the 13 auth-focused ones)
**So that** "works on native" is deliberately verified, not merely inherited from the shared RCL

**Context / notes:** extend `docs/QA_TEST_PLAN.md` §11–13 with a native case per feature area (household,
invites, settings, notifications, MFA, admin, GDPR, files) × {Android, Windows, iOS, macOS} smoke, and a
native release checklist. Regenerate the QA PDFs (B11-8 gate).

```gherkin
Scenario: Full native regression exists
  Then each web feature has a matching native case in §11–13 for each supported platform
```

**DoD:** QA plan expanded + PDFs regenerated; a native smoke suite a human can run per release.

### NATIVE-7 — Automated native UI tests in CI

**Status: ✅ COMPLETE for the non-Apple platforms — both smokes green in CI** (develop run
after #116; Windows leg green 4× consecutively). Getting the Android leg green took five CI
iterations, each fixing one runner-environment delta the local rehearsal couldn't expose — the
sequence is instructive: (1) silent 45-min hang → hardened every phase with deadlines + narration +
`if: always()` diagnostics (a timed-out job archives NO logs); (2) `Unknown AVD name` → avdmanager
and the emulator resolve the AVD home differently on runners, pinned via `ANDROID_AVD_HOME`;
(3) `adb: command not found` → sdkmanager-installed platform-tools isn't on the runner's PATH.
The Windows leg needed one fix (assert `Attached`, not `Visible` — the runner's narrow window
collapses the header). Originally implemented as:

**Windows half** (`feat/native-7-windows-smoke`). No Appium: the smoke
drives the REAL MAUI app's WebView over the **Chrome DevTools Protocol** (the recipe proven during
Wave 2) — the app launches with remote debugging enabled and Playwright attaches with
`ConnectOverCDPAsync`, reusing the E2E project's page test-ids and Mailpit helper.
`NativeSmokeTests` (`[Explicit]` + `Category=NativeSmoke`, so the browser e2e job never runs it):
CDP connect w/ retry → login renders (a G7-style boot crash dies here) → OTP sign-in end-to-end →
Household loads over the native Bearer path. New CI job **`native-smoke-windows`** (develop pushes
only — Windows bills 2×, ~10 min; NOT in deploy-staging needs so native flake can't block web
deploys): preinstalled-Postgres + downloaded Mailpit + API on plain HTTP + the built exe, pointed at
the stack via the new **`PEREZOSOFT_API_BASE_URL`** override in `MauiProgram` (also useful for
physical-device testing against a LAN API). Verified locally with the exact CI shape (smoke green in
5 s against the live app). **Android leg ✅ Implemented**
(`feat/native-7b-android-smoke`): Android WebView's CDP lacks the browser-context management
`ConnectOverCDPAsync` needs, so this leg uses **playwright-core's Node-only `_android` module**
(adb + WebView attach) — a tiny committed spec in `tests/native-smoke-android/` mirroring the
Windows journey; CI job `native-smoke-android` hand-rolls the emulator from the runner's
preinstalled SDK (no marketplace action). Two gotchas encoded: Debug APKs must build with
`EmbedAssembliesIntoApk=true` (a fast-deployment APK silently fails to start from `adb install`),
and the smoke waits for the app process before attaching. **Rehearsed green on a real local
emulator** (boot + OTP + roster). The same PR adds the **`native-paths` cost gate** (docs-only
develop pushes skip the Apple builds + both smokes — the 2026-07-03 sprint exhausted the month's
free Actions minutes in a day) and **deploy-staging concurrency** (back-to-back merges cancel the
older deploy's version-gated smoke instead of failing it). **Apple legs ✅ Implemented 2026-07-06**
(unpinned by the NATIVE-6 Apple QA pass the same day): one CI job **`native-smoke-apple`**
(macos-26, same Xcode pin + cost conditions as `native-build-apple`; one job for both targets so
the expensive setup — workload restore, brew Postgres, API build — is paid once) covers **Mac
Catalyst** (launches the Debug .app binary directly on the runner — the unsandboxed
Debug-entitlements path from PR #125) and the **iOS simulator** (`simctl bootstatus -b`, install,
launch with `SIMCTL_CHILD_PEREZOSOFT_API_BASE_URL`), both against a plain-HTTP API (ATS exempts
loopback — the login page fully renders over http; no Mailpit since a boot smoke sends no email).
Each target asserts **boot-to-login**: the app process survives startup (the G7 crash class) AND
the login page's provider probe lands `GET /api/auth/providers → 200` in the API log
(`Logging__LogLevel__Microsoft.AspNetCore=Information` makes it grep-able; the iOS assert requires
the probe count to grow past the Catalyst phase's, so the shared API can't cross-satisfy);
screenshots uploaded as artifacts. **Deliberately shallower than the Windows/Android legs:**
WKWebView exposes no CDP, so driving the UI would need XCUITest/Appium — too heavy/flaky for a
canary; sign-in journeys stay manual (§13b/§13c). Rehearsed green on real hardware during the
2026-07-06 QA session: iOS ran the exact launch → probe → grep sequence (~3 s to assertion);
Catalyst verified boot + API reach (locally a stored session skips the login page and hits
`/api/auth/refresh` instead — impossible on a fresh runner, which always lands on login).

**As a** maintainer
**I want** the native critical paths driven automatically against a real emulator/simulator
**So that** native regressions are caught without a manual pass

**Context / notes:** stand up a native UI-test harness — **Appium** (or .NET MAUI UITest) driving the
**Android emulator** on CI and a desktop target, covering the auth-critical journeys (OTP sign-in, OAuth,
MFA step-up, a core feature flow). **Accepted risk (ADR-018):** native UI tests are slower + flakier than
Playwright-web; budget retries + generous timeouts, and keep the suite small (smoke, not exhaustive). iOS
simulator tests run on the macOS runner.

```gherkin
Scenario: Native smoke runs on every push
  Given the Android emulator (and iOS simulator) in CI
  When the native UI smoke runs
  Then OTP sign-in + a core feature flow pass headlessly, with retries for flakiness
```

**DoD:** an Android emulator smoke green in CI; iOS simulator smoke on macOS runner; flakiness mitigations documented.

---

## Wave 4 — Distribution (signed, shippable artifacts) — ⤵ MOVED DOWNSTREAM (ADR-024, 2026-07-14)

> **NATIVE-8..11 are no longer platform slices.** ADR-024 resolved the ADR-018 NATIVE-8 gate:
> signing identity (keystore / Apple certs / MSIX publisher) is inherently per-app, so signed
> artifacts and store listings are downstream-app deliverables. Those four sections are kept as the
> **downstream reference** — the scoped knowledge a new app's first native release needs; the
> actionable checklist lives in `docs/NEW_APP_GUIDE.md` Phase 9. The epic closes at NATIVE-6
> (verification). NATIVE-12 below is unaffected — a platform hardening slice, merged (PR #172).

> **Release builds require `-p:ApiBaseUrl=` (v3 audit NAT-3).** A Release build of `src/Maui` fails
> unless a real API base URL is supplied — the localhost fallback + `PEREZOSOFT_API_BASE_URL` override
> and the Android cleartext-traffic exception are **Debug-only**, so a by-the-book signed AAB can never
> ship pointing at (and sending credentials in plaintext to) device-localhost. Every signing command in
> this wave must pass e.g. `-p:ApiBaseUrl=https://api.yourapp.com`; it's compiled in via AssemblyMetadata
> and read back by `MauiProgram`. Debug/CI (native-build, native-smoke) are unaffected.

### NATIVE-8 — Android: signed AAB/APK in CI

**Context / notes (decisions scoped 2026-07-07):** the keystore IS the app's identity — updates only
install over the same signature, so losing it is unrecoverable and it never enters git (base64 →
repo secrets + an offline backup; ADR-001 discipline). Generated once locally with `keytool`.
Release build = `AndroidKeyStore=true` + `AndroidSigningKeyStore/KeyAlias/StorePass/KeyPass` from
env + `AndroidPackageFormat=aab`; version code derives from the release tag (csproj currently pins
`ApplicationVersion=1`). New **tag-triggered release workflow** (not per-push; `ubuntu-latest`,
cheap) + an `apksigner`/`jarsigner -verify` assertion on the artifact. **Deferred decision (bites
at NATIVE-11, not here):** Play App Signing — Google holds the app-signing key and this keystore
becomes the upload key (safer, resettable, effectively required for new Play apps); the keystore
works identically either way. Non-issue verified: OAuth runs through the system browser against
the API redirect URI, so signing-key fingerprints don't affect sign-in.

```gherkin
Scenario: A signed Android artifact is produced
  Given the release keystore (from repo secrets, never committed)
  When the release workflow runs on a tag
  Then a signed .aab is built and uploaded as an artifact
```

**DoD:** signed AAB from CI; keystore in secrets; ADR-018.

### NATIVE-9 — Windows: MSIX package + code-signing

**Context / notes (decisions scoped 2026-07-07):** today the app runs **unpackaged**
(`WindowsPackageType=None`) — this slice adds a packaged Release flavor (`MSIX` +
`Package.appxmanifest`) in the same tag-triggered release workflow (windows-latest leg). Signing
reality: an MSIX must be signed by a cert matching the manifest publisher, and since 2023 real OV
code-signing certs require an HSM/hardware token — "cert in GitHub secrets" only works for a
**self-signed cert**, which is this slice's scope (CI plumbing + sideload onto machines that trust
it). For real users the pragmatic path is **Microsoft Store distribution (NATIVE-11): the Store
signs the package, no cert to own** (~$19 one-time individual account); the alternative is Azure
Trusted Signing (~$10/mo). The slice must include a manual boot + sign-in check of the PACKAGED
build on Windows — MSIX apps run containerized, and Preferences/SecureStorage/file-path behavior
can differ from the unpackaged build all QA so far has exercised (same failure class as the
Catalyst keychain surprise, PR #125).

```gherkin
Scenario: A signed MSIX is produced
  Then a code-signed MSIX is built in CI (cert from secrets) and uploaded
```

**DoD:** signed MSIX artifact; ADR-018.

### NATIVE-10 — iOS + macOS: signed IPA / pkg (Apple)

**Context / notes:** needs the Apple Developer account ($99/**yr, recurring** — certs/apps lapse if
it stops) + certs/provisioning profiles (secrets, base64), built + signed on the macOS runner. iOS
`.ipa` + macCatalyst `.pkg`. **Must also re-verify SecureStorage under the real signing identity**:
properly-signed + provisioned builds can claim `keychain-access-groups`, at which point the
Catalyst store `Entitlements.plist` path works and `DebugFileSessionStore` (the ad-hoc Debug
fallback from PR #125) should be re-tested and considered for retirement.

```gherkin
Scenario: Signed Apple artifacts are produced
  Given Apple signing material in secrets and the macOS runner
  Then a signed .ipa (iOS) and .pkg (macCatalyst) are built and uploaded
```

**DoD:** signed Apple artifacts from CI; ADR-018.

### NATIVE-11 — (optional) Store submission

**Context / notes:** automate (or document the manual path for) Play Console / App Store Connect / MS Store
upload. Store review + accounts are external; the platform ships the upload plumbing behind flags/secrets.
**Account costs (checked 2026-07-07):** Apple $99/yr recurring; Google Play $25 one-time; Microsoft
Store ~$19 one-time (individual). Decisions parked here from earlier slices: enroll in **Play App
Signing** (NATIVE-8's keystore becomes the upload key) and let the **MS Store sign the MSIX**
(NATIVE-9 needs no purchased cert).

**DoD:** upload step wired (guarded/off by default) or the manual submission path documented per store.

### NATIVE-12 — Survive process death during the OAuth browser round-trip (Android)

**Status: ✅ Implemented** (`feat/native-oauth-resilience`). `AuthService` now brackets the browser
flow with a persisted `IOAuthResumeStore` marker (MAUI `Preferences` on Android/iOS/macCatalyst;
Windows' loopback flow needs none), `WebAuthenticatorCallbackActivity` stashes the redirect URI and
relaunches `MainActivity` when the callback lands in a fresh process (marker set, no in-process
flow), and `AuthService.TryCompletePendingOAuthAsync()` — called from MainLayout right after
`InitializeAsync` — finishes the exchange on startup: sign-in through the existing
`/api/auth/native/exchange` path (MFA challenge handed to Login), link outcomes routed to Settings'
existing `linked`/`link_error` banners, stashes older than the 5-min code TTL failed with a
friendly retry message. TDD: 14 red-first unit tests (`OAuthResumeTests`) pin the marker lifecycle,
resume outcomes, and TTL guard; web is a structural no-op (no store registered). **Mechanism proven
on the tablet emulator** with a scripted drill (playwright-core `_android`): tap Continue-with-Google
→ Custom Tab foregrounds → `am kill` (process verified dead) → fire
`perezosoft://auth?code=<invalid>` → the app cold-starts, **stays open**, runs the startup exchange,
and lands on Login with the friendly OAuth error (pre-fix behavior: flash open + close, no UI); the
standard Android smoke (boot + OTP + roster) passed after, so the warm path is unregressed. The
real-consent variant (valid code → signed in) = QA-AND-15 in the NATIVE-6 device pass.

**As a** native Android user signing in with Google/Microsoft
**I want** the sign-in to complete even if Android kills the app while I'm on the provider's page
**So that** the app doesn't flash open and close, silently losing my sign-in

**Context / notes:** observed on the tablet emulator under memory pressure (2026-07-07):
`WebAuthenticatorOAuthInitiator` awaits `WebAuthenticator.AuthenticateAsync`, whose pending state is
in-memory only. If the OS kills the process during the provider round-trip, the
`perezosoft://auth?code=…` redirect cold-starts a fresh process, `WebAuthenticatorCallbackActivity`
finds no pending operation, and the one-time code is lost. The Custom-Tabs `<queries>` fix (a3bad29)
shrinks the window but can't close it. Fix: (1) persist an "OAuth in flight" marker
(`IOAuthResumeStore` → MAUI Preferences) around the browser flow; (2) on a cold-start callback
(marker set, no in-process flow), stash the callback URI and relaunch `MainActivity`; (3) on startup,
`AuthService.TryCompletePendingOAuthAsync()` parses the stashed query (`code`/`linked`/`error` — the
`WebAuthenticatorResult.Properties` shape) and completes the exchange through the existing native
login path. The one-time code's server TTL is 5 min (`NativeAuthCodeService`) — older stashes fail
with a friendly retry message instead of a doomed exchange.

```gherkin
Scenario: Sign-in survives process death
  Given I started a native Google sign-in and Android killed the app while I was on Google's page
  When the provider redirects to perezosoft://auth?code=…
  Then the app relaunches, exchanges the stashed code on startup, and I am signed in

Scenario: MFA step-up after a resumed sign-in
  Given my resumed code exchange answers mfa_required
  When the app finishes starting
  Then the Login screen opens directly on the MFA code prompt

Scenario: Stale stash fails politely
  Given the stashed callback is older than the one-time code TTL
  When the app starts
  Then no exchange is attempted and Login shows a "took too long — try again" message

Scenario: Interrupted account-linking still reports its outcome
  Given the round-trip was a provider link (linked/error callback) and the process died
  When the app restarts
  Then I land on Settings with the existing linked/link_error banner

Scenario: Warm flow unchanged
  Given the app process survived the round-trip
  Then WebAuthenticator completes in-process exactly as before and no stash is consumed
```

**DoD:** unit tests for marker lifecycle, TTL expiry, resume exchange, MFA handoff, and link
outcomes (red-first); warm-path E2E/smokes stay green; `NATIVE_PARITY.md` auth row updated;
QA-AND-15 added for the on-device kill test (NATIVE-6).

---

## Slice plan (sequenced — guardrails first, distribution last)

1. ✅ **NATIVE-1** build gate — DONE (all four TFMs; Apple legs on develop pushes, see the free-tier
   amendment above) + ✅ **NATIVE-2** audit — DONE (`docs/NATIVE_PARITY.md`, incl. the post-plan
   BILLING-8/ADMIN-3 screens): six gaps G1–G6 registered, each mapped to a slice below.
2. ✅ **Wave 2 COMPLETE** — NATIVE-4b (G5 join-by-code), NATIVE-5 (G6 culture bootstrap; Windows
   window-sizing 🔍 resolved), NATIVE-3 (G1 downloads — attachment disposition +
   `IFileDownloadLauncher`; upload half N-A, no consumer yet), NATIVE-4 (G2 refresh-on-resume via
   `AppResumeNotifier` + G3 Android back handler). All six audit gaps closed; OS-chrome behaviors
   (share sheet, hardware back, real focus transitions) queue for the NATIVE-6 device pass.
3. ✅ **NATIVE-6** manual native QA pass — plan authored (117 cases incl. iOS/macCatalyst first-run
   smoke + §13c release checklist; G7 Apple-boot fix shipped alongside). **Apple column UNPINNED
   2026-07-06** — the maintainer ran §13b on a MacBook Air M1: QA-IOS-01/02/04 + QA-MAC-01/02 +
   the OAuth leg of QA-MAC-03 PASS (two platform gaps found and fixed, PR #125). **NATIVE-7 ✅
   COMPLETE — smokes for all four platforms in CI:** Windows (WebView2-CDP `native-smoke-windows`),
   Android (`native-smoke-android`), and iOS-simulator + Mac Catalyst (`native-smoke-apple`,
   boot-to-login canaries in one macOS job, added 2026-07-06 once the QA pass validated the
   runtimes). **✅ NATIVE-6 PASSED 2026-07-14** — the maintainer completed the ENTIRE manual QA
   process as the plan stood then (125 cases: web + all four native columns, incl. the leftover
   §13b Apple spot-checks), no open findings (recipe kept in `STATUS.md` §3). **Epic NATIVE
   COMPLETE.** (Post-pass additions — §14a re-run rows + QA-AND-15 — are the open device items.)
4. ⤵ **NATIVE-8/9/10/11** signing + packaging + store submission — **moved downstream (ADR-024)**:
   per-app work, executed by each downstream app at its first native release (checklist in
   `NEW_APP_GUIDE.md` Phase 9; Wave 4 above is the reference). **The epic completes at NATIVE-6.**
5. ✅ **NATIVE-12** (out-of-band hardening, 2026-07-07) — OAuth survives process death during the
   browser round-trip; found on the tablet emulator during NATIVE-6 prep, fixed ahead of the device
   pass so QA-AND-15 can verify it there.

Each slice is an independent, mergeable PR (branch off develop; TDD/verification per slice). Waves gate:
don't automate (7) before the app is verified working (6); distribution (8–11) is downstream-app work.

**Known sharp edges (from ADR-018):**
- **iOS/macOS need a Mac** — no macOS runner ⇒ NATIVE-1/7/10 can't cover Apple platforms; sequence Apple
  work once the runner + Apple account exist.
- **Signing material is secret** — keystores/certs/profiles live in repo secrets (base64), never the repo;
  gitleaks stays green.
- **Native UI tests are flaky** — keep the automated suite to smoke, with retries; manual QA (NATIVE-6)
  remains the broader safety net.
- **Parity ≠ more** — this epic makes native do what **web** does. OS push notifications, biometrics, and
  other native-only features are **beyond parity** and explicitly out of scope here (own future epics).
- **Web-first still holds** — new features land + prove on web first (golden rule 5); this epic keeps
  native *caught up*, it doesn't invert the order.
