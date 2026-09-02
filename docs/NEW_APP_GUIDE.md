# Creating an App from this Platform — the end-to-end guide

> The spine document: every phase from "I have an idea" to "customers are using it in
> production", in order, with links to the detailed doc for each step. The README gives the
> 3-step short version; this is the full path with nothing implicit.

**The journey at a glance:**

| Phase | What happens | Where | Typical time |
|---|---|---|---|
| 0 | Install tools | your machine | 30 min (once) |
| 1 | Conceptualize the app | Claude **chat** | a half-day of thinking |
| 2 | Create the repo | GitHub | 15 min |
| 3 | Rebrand + fill placeholders | Claude **Code** | ~1 hour |
| 4 | First local run | your machine | 15 min |
| 5 | Model the domain | Claude Code | hours |
| 6 | Build features, slice by slice | Claude Code | your actual product work |
| 7 | Stand up staging | Neon/Brevo/Stripe/Render | an afternoon (once) |
| 8 | Activate production | Render + GitHub | ~10 min |
| 9 | Native apps (optional, later) | — | when the web product is stable |

Phases 0–4 are a **single day**. Everything the platform already does (auth, tenancy, billing,
GDPR, admin, CI/CD — see `OVERVIEW.md`) never appears on this list: that's the point.

---

## Phase 0 — Tools (once per machine)

- **Git**, and a **GitHub** account (repo + CI live there).
- **.NET 10 SDK** — <https://dotnet.microsoft.com/download/dotnet/10.0> (check
  `docs/TECH_STACK.md` for the currently pinned line; re-verify "latest stable" at project start).
- **Docker Desktop** — runs local Postgres + the Mailpit email trap.
- **Claude** — chat access for Phase 1, Claude Code for Phases 3+.
- An IDE is optional (Claude Code does the driving); Rider/VS/VS Code all work.

No cloud accounts are needed until Phase 7.

## Phase 1 — Conceptualize (in Claude chat, not Code)

Paste **`_PLATFORM_PRIMER.md`** into a fresh Claude chat and describe your app. The primer makes
Claude run a structured session: clarifying questions, recommendations, scope discipline
(an explicit OUT list), and ADRs for every settled decision. The stack is frozen, so the whole
conversation is about *your product*.

**Leave this phase holding:** filled-in `PROJECT_BRIEF.md`, `FEATURES.md`, `DATA_MODEL.md`,
`DECISIONS.md` — plus three things the next phase needs: an **app name**, the **tenant's
app-facing label** (Team? Workspace? Household?), and a **logo file** (SVG or large PNG).

## Phase 2 — Create the repo

1. Copy the platform tree into a new repository (don't fork — a new app is not a branch of the
   platform): `git clone`, remove `.git`, `git init`, point at your new GitHub remote.
2. Drop the Phase-1 docs into `docs/`.
3. Create the two branches and protect them: **`main` is deploy-only** (protect it; nothing lands
   there except release merges), **`develop` is the working branch** — one branch + PR per slice.
4. Push. **CI runs immediately and should be green** (build, ~500 tests, secret/license/QA-doc
   gates, native builds, browser E2E). The deploy jobs stay skipped until Phase 7's secrets exist.

## Phase 3 — Rebrand + fill the placeholders (first Claude Code session)

Open Claude Code in the repo and use **the expected first prompt from `README.md` Step 3**
(copy-paste block): app name + tenant label + logo attached. Claude then:

- Runs **`docs/REBRANDING.md` end to end** — name/wordmark, tagline, logo assets and derived
  icons/favicon/OG image, the **colour palette derived from your logo** (the semantic tokens in
  `src/Shared.Ui/wwwroot/css/app.css` + the email colours in `BrandedEmail.cs` — the email
  templates are the classically missed spot), the OAuth callback scheme + `ApplicationId`, and
  the brand strings in every localization `.resx`.
- Fills the `CLAUDE.md` TODOs (app-specific golden rules, conventions) from your Phase-1 docs.
- Verifies: `git grep -i perezosoft` returns nothing, and a test OTP email arrives with the new
  brand.

## Phase 4 — First local run

```bash
cp .env.example .env          # then fill it — at minimum Jwt__Secret (any ≥32-char string)
docker compose up -d db mail  # Postgres 17 + Mailpit
dotnet run --project src/Api --launch-profile https    # API on https://localhost:7160
dotnet run --project src/Web                           # web UI on https://localhost:7008
```

Sign in with **"Email me a 6-digit code"** and read the code from Mailpit at
<http://localhost:8025> — no OAuth keys needed yet (Google/Microsoft buttons need Phase 7 §5
credentials; everything else works without them). You now have a branded, multi-tenant,
running app with zero features.

## Phase 5 — Model the domain

From `DATA_MODEL.md`, Claude Code adds your entities (tenant-owned ones implement `ITenantScoped`
— the global query filter then isolates them automatically) and generates the EF Core migration on
top of the platform schema. The architecture tests fail the build if an entity dodges the tenancy
rules. Read `docs/DATA_MODEL.md`'s derived-rules section first: **derived values are computed,
never stored**.

## Phase 6 — Build features, slice by slice

The rhythm, per `docs/WAYS_OF_WORKING.md`:

1. Pick the next epic; Claude writes its user stories into `docs/stories/<epic>.md` (Gherkin).
2. Each slice = one branch + one PR off `develop`, test-first (the failing test precedes the
   code), end-to-end (API + UI + tests), leaving the app working.
3. Copy **`src/Api/Features/Notes`** as the reference slice shape; **delete the Notes sample**
   when your first real feature lands.
4. CI gates every PR; merge to `develop` auto-deploys staging once Phase 7 is done.

Scope discipline: before building anything, check the OUT list in `PROJECT_BRIEF.md`.

## Phase 7 — Stand up staging (an afternoon, once)

Follow **`docs/DEPLOYMENT.md`** top to bottom — it's the runbook. The order and the accounts:

1. **§1 Neon** — free serverless Postgres; copy the connection string.
2. **§2 Brevo** — free SMTP (300/day); the four `Email__Smtp__*` values.
3. **§3 Stripe (test mode)** — **required**: the image runs as Production, which fail-closes
   without `Billing__Stripe__SecretKey`; a `sk_test_…` key is fine.
4. **§4 Render** — apply `render.yaml` as a Blueprint, paste the secrets from 1–3, deploy.
5. **§5 OAuth (optional)** — register your staging domain with Google/Microsoft, add the client
   id/secret env vars. One provider console entry per domain.
6. **§6 CI auto-deploy** — GitHub secret **`RENDER_DEPLOY_HOOK_STAGING`**; from then on every
   merge to `develop` deploys staging and runs the version-gated smoke.

## Phase 8 — Activate production (~10 min, when ready for customers)

Per `DEPLOYMENT.md` §6 / `STATUS.md` §5: create the prod Render service (separate Neon DB;
**live** Stripe key — the startup guard enforces sanity), add GitHub secret
**`RENDER_DEPLOY_HOOK_PROD`**, and create the **`production` environment with a required
reviewer**. Releases become: PR `develop → main`, merge, click approve.

⚠️ Heads-up: the platform itself never activated production (ADR-017 amendment — it's a template;
staging is its terminal environment), so **your** activation is the first real run of two guards:
the **RLS two-role setup + posture guard** (`DEPLOYMENT.md` §7) and the **live-Stripe-key startup
guard**. Both are tested in CI, but budget a smoke check (sign-in + a checkout round-trip) right
after flipping them on.

## Phase 9 — Native apps (optional, and deliberately last)

The Windows/Android/iOS/macOS shells already build in CI and carry your brand from Phase 3. When
the web product is stable and you want installable apps, this phase is **yours, not the
platform's** — signing identity (keystore, certs, store listings) is inherently per-app, so the
platform deliberately ships no release workflow (ADR-024). What it ships instead is this
checklist, plus the full scoping notes in `docs/stories/native.md` Wave 4 (read the matching
section before each step — the traps below were found the hard way).

**First, verify:** run the native QA pass (`docs/QA_TEST_PLAN.md` §12–13, guides in the QA PDFs).
Don't sign an app you haven't seen working.

**Then, the first-native-release checklist:**

1. **Android** (native.md → NATIVE-8): generate the release keystore once with `keytool` — it IS
   your app's identity; losing it is unrecoverable. Base64 → repo secrets + an offline backup,
   never git. Build with `AndroidKeyStore=true` + the four signing props from env,
   `AndroidPackageFormat=aab`, in a **tag-triggered** release workflow (`ubuntu-latest`); assert
   the artifact with `jarsigner -verify`. Enroll in **Play App Signing** — your keystore becomes
   the upload key (Google holds the app-signing key; effectively required for new Play apps).
2. **Windows** (native.md → NATIVE-9): the app runs unpackaged today (`WindowsPackageType=None`);
   add a packaged Release flavor (MSIX + `Package.appxmanifest`). Don't buy a code-signing cert —
   real OV certs need an HSM since 2023; **let the Microsoft Store sign the package** (~$19
   one-time account), or self-sign for sideloading only. ⚠️ **Trap: packaged MSIX runs
   containerized** — manually boot the PACKAGED build and re-verify sign-in +
   Preferences/SecureStorage/file paths before shipping; every QA pass so far ran unpackaged
   (same failure class as the Catalyst keychain surprise, platform PR #125).
3. **iOS/macOS** (native.md → NATIVE-10): needs the Apple Developer account ($99/**yr**,
   recurring — certs lapse if it stops) + certs/provisioning profiles as base64 secrets, built on
   a macOS runner (iOS `.ipa`, macCatalyst `.pkg`). ⚠️ **Trap: re-verify SecureStorage under the
   real signing identity** — properly-provisioned builds can claim `keychain-access-groups`, at
   which point the platform's `DebugFileSessionStore` fallback (Catalyst Debug, PR #125) should be
   re-tested and considered for retirement in your fork.
4. **Store submission** (native.md → NATIVE-11): Play Console / App Store Connect / MS Store —
   accounts: Google $25 once, Apple $99/yr, Microsoft ~$19 once. Wire the upload step guarded/off
   by default, or document your manual path.
5. **Every native release thereafter:** gate on the §13c checklist in `docs/QA_TEST_PLAN.md`
   (smoke cases per platform + one feature spot-check; full §12–13b regression when native glue or
   the toolchain changed).

Until you need installable apps, web-first costs you nothing — golden rule 5.

---

## The other direction: what NOT to redo

A reminder that saves the most time of all — **none of this is your work anymore**: sign-in flows,
MFA, tenant isolation, invitations, roles, Stripe billing/quotas/trials/dunning, background jobs,
notifications, file storage, GDPR export/erasure, audit logging, the admin console, localization
plumbing, the CI pipeline, or the deploy story. If a feature request looks like one of those,
check `docs/ROADMAP.md` / `docs/PLATFORM_BACKLOG.md` first — it's probably built, config-gated,
or consciously deferred with a design sketch waiting.

| Stuck on… | Read |
|---|---|
| What the platform includes | `docs/OVERVIEW.md` |
| A decision you're about to reverse | `docs/DECISIONS.md` (add an ADR, don't silently change) |
| Slice/PR/commit mechanics | `docs/WAYS_OF_WORKING.md` |
| Rebrand completeness | `docs/REBRANDING.md` (verify section) |
| Deploy details / env vars | `docs/DEPLOYMENT.md` (reference table at the end) |
| Android emulator sign-in | `docs/MOBILE_TESTING.md` |
