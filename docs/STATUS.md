# Project Status & Operator Guide — 2026-07-04

> Point-in-time compass: where the platform stands, what remains, and step-by-step guides for the
> tasks that need a human. Complements `ROADMAP.md` (sequencing) and `DEPLOYMENT.md` (prod runbook).
> Snapshot as of develop `588a0bb` (PR #117).

## 1. Where we stand

The platform is **feature-complete and continuously verified**:

- **All 13 foundation epics done** — auth/tenancy, JOBS, BILLING 1–8, OBS, RBAC, FILES, GDPR, MFA,
  NOTIFY, ADMIN, PUBAPI, HOOKS, E2E.
- **Staging live** at `https://template-staging.onrender.com` with version-gated auto-deploy from
  develop (DEPLOY-1..3).
- **NATIVE epic at its resting point** — parity gaps G1–G7 all closed; all four MAUI targets
  (Android / Windows / iOS / macCatalyst) compile on every push; every native-relevant merge boots
  the real app through a full OTP sign-in on **Windows** (WebView2 CDP) and an **Android emulator**
  (playwright-core `_android`) in CI.
- **Safety net:** 473 unit/integration tests (Api 431 + Core 42), 29 browser E2E journeys, 2 native
  smoke jobs, a 118-case manual QA plan with generated PDFs, plus secret/license/supply-chain/
  doc-sync CI gates. PRs #100–#117 merged in the NATIVE arc.

## 2. What's missing, who owns it

| # | Item | Owner | Unblocks |
|---|------|-------|----------|
| 1 | ~~NATIVE-6 manual QA pass — Android + Windows~~ → **✅ PASSED 2026-07-14** (maintainer device pass, §3) — **epic NATIVE COMPLETE** | — | Done — nothing follows it on this repo (ADR-024) |
| 2 | ~~Apple first-run smoke~~ → **✅ COMPLETE** (first run 2026-07-06: 5/7 + OAuth PASS, two fixes PR #125, CI Apple smokes shipped; remaining spot-checks closed in the 2026-07-14 full pass) | — | Nothing |
| 3 | ~~NATIVE-8/9 signed AAB + MSIX release plumbing~~ → **⤵ MOVED DOWNSTREAM 2026-07-14 (ADR-024)** — signing/stores are per-app; checklist in `NEW_APP_GUIDE.md` Phase 9 | — | Nothing here (each app's first native release) |
| 4 | ~~RLS tenancy backstop~~ → **✅ MERGED** (ADR-020 addendum; staging live-enforced; two-role prod setup in `DEPLOYMENT.md` §7) | — | Downstream prod activation |
| 5 | ~~Production deploy activation~~ → **⤵ MOVED DOWNSTREAM 2026-07-14 (ADR-017 amendment)** — the platform never activates prod; staging is its terminal environment. §5 stays as the downstream Phase-8 runbook | — | Each app's Phase 8 (`NEW_APP_GUIDE.md`) |
| 6 | Parked by choice: HOOKS-3 UI, API-key rotation, CACHE (multi-node), FR/DE/PT translations | — | Nothing today |

> **With rows 1–5 resolved, the platform reached its terminal state (2026-07-14)**: every epic
> complete, verified on web + all four native platforms, staging continuously deployed. The v3
> delta audit (2026-07-15 → 07-27) then hardened it in place; its §14a re-run rows + QA-AND-15
> (NATIVE-12) are the open device items on the current 150-case plan. What remains is maintenance
> (toolchain drift, QA findings) and building downstream apps (`NEW_APP_GUIDE.md`).

## 3. Guide — native QA pass on Android + Windows (~1–2 h)

> **Executed 2026-07-14 — PASSED**, as part of the maintainer completing the **entire manual QA
> process** as the plan stood then (125 cases: web + all four native columns incl. the §13b Apple
> leftovers — no open findings; see the QA_TEST_PLAN changelog). Kept as the recipe for future
> passes (full §12–13b regression is due whenever native glue or the .NET/MAUI toolchain changes;
> the post-pass additions — §14a re-runs + QA-AND-15 — are tracked in §2's terminal-state note).

1. **Prep:** `git pull` on develop. Open `docs/QA_TEST_GUIDE.pdf` (walkthroughs) and
   `docs/QA_RUN_LOG.pdf` (the sheet to mark Pass/Fail per case).
2. **Stack:** `docker compose up -d db mail`, then
   `dotnet run --project src/Api --launch-profile https`. Mailpit UI (OTP emails):
   <http://localhost:8025>.
3. **Windows:** `dotnet build src/Maui -f net10.0-windows10.0.19041.0 -t:Run`.
   Minimum = the §13c checklist column (DSK-01, DSK-03, DSK-06 + one of DSK-08..12); ideal =
   all DSK-01..14. Highest-value cases (OS behavior CI can't see): **DSK-10** (share flyout),
   **DSK-11** (billing refresh on returning to the app).
4. **Android:** start the emulator, `adb reverse tcp:5238 tcp:5238`
   (see `docs/MOBILE_TESTING.md`), then `dotnet build src/Maui -f net10.0-android -t:Run`.
   Minimum = AND-01, AND-03, AND-07 + one of AND-08..12. Human-only cases: **AND-07** (hardware
   back), **AND-10** (share sheet), **AND-13** (edge-to-edge on Android 15).
5. **Record & report:** mark each case in the run log; report failures (each becomes a small fix
   slice). All-pass on §13c ⇒ NATIVE-6 done ⇒ **epic NATIVE complete** (signing/store distribution
   is downstream-app work per ADR-024 — no NATIVE-8/9 follows).

## 4. Guide — Apple first-run smoke on the MacBook (§13b: QA-IOS-01..04, QA-MAC-01..03)

> **Executed 2026-07-06** (results in the QA_TEST_PLAN changelog): QA-IOS-01/02/04 + QA-MAC-01/02 +
> the OAuth leg of QA-MAC-03 **PASS** on the maintainer's MacBook (iPhone 17 / 17 Pro Max / iPad Air
> simulators + Mac Catalyst). Two platform gaps were found and fixed (PR #125: SMTP revocation knob;
> Catalyst Debug session store), and the pass unpinned the CI Apple smokes (`native-smoke-apple`).
> The remaining spot-checks (QA-IOS-03 + rest of QA-MAC-03) **passed in the 2026-07-14 full pass**.
> **One recipe correction is baked in below** (Phase 4 step 2): the ASP.NET dev cert **cannot** be a
> simulator trust anchor — it's `CA:FALSE`, and iOS rejects it with `errSSL -9813`, which reads as
> "OAuth buttons missing on the login page". Use the CA-signed localhost cert instead.

These seven cases had **never been run** before 2026-07-06 — iOS/macCatalyst compile in CI but the
app had only ever booted on Apple hardware in theory. QA-IOS-01 alone (it boots at all) validates
the G7 crash fix on a real Apple runtime. **No paid Apple Developer account is needed for any of
this** — the simulator and Mac Catalyst run free; the $99/**yr, recurring** account only matters
for physical-iPhone installs and store distribution, which are downstream-app work (ADR-024;
`NEW_APP_GUIDE.md` Phase 9).

### Phase 0 — check the MacBook is viable (5 min)

```bash
sw_vers        # need macOS 15.5 (Sequoia) or newer — Xcode 26.x requires it
uname -m       # arm64 = Apple silicon (ideal); Intel works but is slow
df -h /        # want ~60 GB free (Xcode ≈ 15 GB + simulator runtime + .NET + repo)
```

If the Mac can't run macOS 15.5+, stop and report back — we'd need to discuss pinning an older
toolchain instead.

> **Verdict for the target machine (2026-07-04):** the maintainer's Mac is a 2020 MacBook Air
> M1, 8 GB RAM, 512 GB SSD, on the latest macOS with Xcode and VS Code already installed —
> **viable**; Phase 0 passes and Phase 1 shrinks to steps 2–6. The only pinch point is the
> **8 GB of RAM**; work in this order and it's comfortable:
> 1. Cap Docker Desktop's memory at ~2 GB (Settings → Resources) — Postgres + Mailpit need far less.
> 2. Close browsers and other heavy apps during the pass.
> 3. Do the **macCatalyst cases first** (Phase 3, no simulator), then quit that app before Phase 4.
> 4. Keep exactly **one** simulator booted; expect the first iOS build to take several minutes.

### Phase 1 — install the toolchain (~1–2 h, mostly downloads)

1. **Xcode 26.5** — App Store (search "Xcode"), or the exact version from
   <https://developer.apple.com/download/applications/> (free Apple ID sign-in). 26.5 is the CI
   pin; a newer 26.x from the App Store is normally fine. *(Already installed on the target
   MacBook — just confirm `xcodebuild -version` reports 26.x, and still run step 2: the iOS
   simulator runtime is a separate download that a stock Xcode install may not have.)*
2. **First-launch setup** (Terminal):
   ```bash
   sudo xcode-select -s /Applications/Xcode.app
   sudo xcodebuild -license accept
   sudo xcodebuild -runFirstLaunch
   xcodebuild -downloadPlatform iOS      # installs the iOS simulator runtime (~8 GB)
   ```
3. **.NET 10 SDK** — <https://dotnet.microsoft.com/download/dotnet/10.0>, macOS **Arm64** installer
   on Apple silicon (x64 on Intel). Verify: `dotnet --version` → 10.0.3xx.
4. **Docker Desktop for Mac** — <https://www.docker.com/products/docker-desktop/> (for Postgres +
   Mailpit).
5. **Clone the repo** (public now, plain https works):
   ```bash
   git clone https://github.com/argamboad/perezosoft-platform.git
   cd perezosoft-platform && git checkout develop
   ```
6. **MAUI workloads** (from the repo root):
   ```bash
   sudo dotnet workload restore src/Maui/Perezosoft.Maui.csproj
   ```

### Phase 2 — bring up the local stack (15 min)

1. `cp .env.example .env` and fill in the dev values — easiest is copying your Windows repo-root
   `.env` across (AirDrop/USB; **never commit it**).
2. `docker compose up -d db mail` — Postgres 17 + Mailpit.
3. HTTPS certificate — **skip `dotnet dev-certs` if the simulator is on the menu** and go straight
   to the CA-signed cert (Phase 4 step 2 explains why the dev cert can't work there); Kestrel is
   pointed at it via two `Kestrel__Certificates__Default__Path/KeyPath` lines in `.env`. For a
   Catalyst/web-only session, `dotnet dev-certs https --trust` (keychain password prompt) is enough.
4. Start the API: `dotnet run --project src/Api --launch-profile https`. Verify
   <https://localhost:7160/health> returns Healthy; Mailpit UI at <http://localhost:8025>.

### Phase 3 — Mac Catalyst first (it's the easy one)

The app runs on the Mac itself, so the keychain-trusted cert just works. Note that local Debug
builds are ad-hoc signed and run **unsandboxed with a file-based session store** — the keychain
needs a restricted entitlement no ad-hoc build can claim (see `Entitlements.Debug.plist` and
`DebugFileSessionStore`, PR #125):

```bash
dotnet build src/Maui -t:Run -f net10.0-maccatalyst
```

Run **QA-MAC-01** (launches, usable resizable window, login renders — no G7 crash),
**QA-MAC-02** (OTP sign-in via Mailpit), **QA-MAC-03** (core flows + one Google OAuth round-trip +
restart persistence).

### Phase 4 — iOS simulator

1. Boot a simulator: `open -a Simulator` (or let step 3 pick the default device).
2. **Trust a CA-signed localhost cert in the simulator** (2026-07-06 correction — the ASP.NET dev
   cert is `CA:FALSE`, so `add-root-cert` on it silently buys nothing and TLS fails `errSSL -9813`;
   symptom: the login page renders email-only, no OAuth buttons). One-time setup: create a tiny
   local CA + a CA-signed `localhost` leaf (SANs `localhost`,`127.0.0.1`,`::1`; LibreSSL needs
   config-file syntax, no `-addext`), point Kestrel at the leaf via
   `Kestrel__Certificates__Default__Path/KeyPath` in `.env`, trust the **leaf** in the login
   keychain (`security add-trusted-cert -r trustAsRoot -p ssl -k ~/Library/Keychains/login.keychain-db localhost.pem`)
   and the **CA root** in the simulator — per booted device:
   ```bash
   xcrun simctl keychain booted add-root-cert /path/to/ca.pem
   ```
3. Build & run:
   ```bash
   dotnet build src/Maui -t:Run -f net10.0-ios
   ```
   The simulator shares the host network, so `https://localhost:7160` reaches the API directly —
   no `adb reverse` equivalent needed.
4. Run **QA-IOS-01** (boots to login — the G7 validation), **QA-IOS-02** (OTP sign-in),
   **QA-IOS-03** (core-flows spot-check incl. share sheet + language persistence),
   **QA-IOS-04** (Google OAuth via `ASWebAuthenticationSession` → returns on `perezosoft://auth` —
   this path has never been exercised anywhere).

### Phase 5 — record & report

Mark the seven §13b rows in the run log and report results. Failures become fix slices; a green
pass unpins the Apple column: the iOS-simulator CI smoke leg gets built. (IPA/pkg signing + store
submission are downstream-app work per ADR-024 — an Apple Developer account is only needed by an
app that ships, not by the platform.)

### Troubleshooting

| Symptom | Fix |
|---|---|
| Build says Xcode not found / unsupported | `sudo xcode-select -s /Applications/Xcode.app`, confirm `xcodebuild -version` ≥ 26 |
| `-t:Run -f net10.0-ios` picks the wrong device | `xcrun simctl list devices`, then append `-p:_DeviceName=:v2:udid=<UDID>` |
| App loads but sign-in spins / TLS errors (iOS) | Redo Phase 4 step 2 with the simulator **booted**; restart the app |
| OTP email never arrives | Mailpit running? `docker compose ps`; UI at :8025 |
| App can't reach the API | API must be on the **https** profile (port 7160); check `/health` in Safari on the Mac |
| Want to test on a physical iPhone | Different setup (LAN-bound API + `PEREZOSOFT_API_BASE_URL` + free-provisioning signing) — not needed for §13b; ask Claude when ready |

## 5. Guide — activate production (~10 min) — ⤵ downstream-app work (ADR-017 amendment, 2026-07-14)

> **The platform itself never runs this** — staging is its terminal environment; a prod service
> with zero users is pure cost. This guide is each downstream app's **Phase 8**
> (`NEW_APP_GUIDE.md`). Note for that first activation: the **RLS two-role setup + posture guard**
> (§7 of `DEPLOYMENT.md`) and the **live-Stripe-key startup guard** run for real for the first
> time there — budget a smoke check after flipping them on.

> **Prerequisite (hard gate):** the **Postgres RLS tenancy backstop** (ADR-020,
> `PLATFORM_BACKLOG.md` §11) must be implemented and merged first. Pre-production it's a
> provisioning-config change; retrofitting DB roles + policies under live tenants becomes a data
> migration with a rollback plan. Don't activate prod without it.

1. In Render, create the prod service (same Docker setup as staging — `docs/DEPLOYMENT.md` is the
   runbook) with a **separate Neon database** and prod env vars. Note the Production Stripe-key
   guard: the app refuses to start in Production with test keys misconfigured.
2. Copy the service's deploy-hook URL → GitHub repo → Settings → Secrets and variables → Actions →
   new secret `RENDER_DEPLOY_HOOK_PROD`.
3. GitHub → Settings → Environments → `production` → add yourself as **required reviewer** — every
   prod deploy then needs a manual approval.
4. Flow from then on: PR develop → main, merge, approve the deployment.

## 6. What prevents building SaaS apps on this platform?

**For web SaaS: nothing.** The intended flow works end-to-end today: clone → fill in the
conceptualization docs (`CLAUDE.md` TODOs, `PROJECT_BRIEF.md`, `FEATURES.md`, `DATA_MODEL.md`) →
run the `REBRANDING.md` checklist → build vertical slices by copying the Notes sample → deploy on
the proven Render/Neon/Brevo path. Multi-tenant auth, billing with quotas and dunning, GDPR, MFA,
notifications, admin, observability and CI/CD are all built and test-guarded.

Each new app needs **external accounts, not code**: its own OAuth client registrations, Stripe
account, SMTP sender, and hosting (documented in `DEPLOYMENT.md` / `.env.example`).

Honest qualifiers:

1. **Native distribution isn't finished** — web products ship today; installable
   Android/Windows/iOS binaries wait on items 1–3 in §2. Web-first apps are unaffected.
2. **The QA pass hasn't run on devices** — native code is CI-smoked but "verified on hardware" is
   an open checkbox.
3. **Rebranding is a manual checklist**, not a script — deliberate, but the most error-prone step
   for a downstream app (email templates are the classic miss).
4. **Deferred conveniences:** no Redis/cache layer (fine single-node), webhooks are managed via API
   only, FR/DE/PT locales are scaffolded but untranslated.
