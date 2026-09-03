# CLAUDE.md

> Operating manual for Claude Code on this project. Auto-loaded each session — keep it tight.
> Constant rules are pre-filled; fill the app-specific placeholders during conceptualization.

## What this project is
**¿Y el vuelto?** (code name `vuelto`) — a personal-finance app for Costa Rican **households that
live in two currencies** (₡ CRC + $ USD). Money spent (typed in, or parsed from bank voucher
emails) is captured in both currencies at that day's rate, lands in the **pay-cycle budget month**
containing its date (weeks anchored on a chosen weekday, not the calendar), and the dashboard shows
budgeted vs actual per line / week / bank×method plus income, unplanned essentials, expected
refunds and savings envelopes. Full context in `docs/PROJECT_BRIEF.md`.

**This app is a continuation port** (ADR-V001) of the donor repo `vuelto-legacy/phase2`
(`C:\Users\argam\source\repos\Personal\vuelto-legacy`, frozen, read-only reference) onto this platform.
Until parity: work the port plan slice by slice (P0–P11 — see the plan linked from ADR-V001), port
the donor's tests as the spec, and **never modify platform code in this repo** — extend it through
its seams; a generic gap goes upstream to `perezosoft-platform` first.

## Read before you act
- **Writing or modifying ANY code → `docs/audits/v3-2026-07/FOUNDATION_RULES_v2.md` (v2.0: R1–R35
  carried from v1.0 + R36–R76) is binding.** It encodes the post-audit invariants (tenancy incl. the
  RLS backstop parity, second-factor/event replay, SSRF, fail-closed normalization, atomic quotas +
  single-use credentials, per-user AND per-tenant erasure completeness, injected clocks, slice
  boundaries, host parity, doc/Postman sync) as machine-enforced arch tests + CI gates. Comply; if a
  task seems to require violating a rule, stop and surface it. The frozen quality bar lives in
  `CONTRIBUTING.md` (v1.0 remains at `docs/audits/v2-2026-07/FOUNDATION_RULES.md` as the historical layer).
- **Hardening the template (or a clone) → follow `docs/audits/AUDIT_SUITE.md`.** The single repeatable
  super-audit (5 diagnostic/gate phases + QA-paranoia + docs/course currency) that produced the `audits/v*`
  runs. It's **triggered, not routine** — run it on a structural core change, a new wave of epics, a major
  dependency bump, or before generating a production app (at minimum re-run its Phase 4 adversarial slice
  pass). Between triggers, keep the gates green instead of re-auditing.
- Touching the schema or entities → read **`docs/DATA_MODEL.md`** first.
- Implementing a screen or flow → read **`docs/FEATURES.md`** first.
- Starting a build slice → read **`docs/WAYS_OF_WORKING.md`** (slices, story format, PR/commit
  conventions).
- Adding an app feature → follow the **clean-platform + vertical-slice convention** in
  `docs/WAYS_OF_WORKING.md` (and ADR-004). The platform's `Notes` sample slice was **deleted in the
  rebrand PR** (this repo has no sample); the four-file anatomy in `WAYS_OF_WORKING.md` is the
  reference, and `perezosoft-platform/src/Api/Features/Notes` can be consulted read-only.
- Wondering *why* something is the way it is → check **`docs/DECISIONS.md`** before changing it.
- Changing a settled decision → add a new dated ADR in `docs/DECISIONS.md`; don't silently
  reverse it.
- Rebranding (name, logo, colours, tagline) → follow **`docs/REBRANDING.md`** and complete every
  item. It explicitly covers the **transactional email templates** (`src/Infrastructure/Email/` —
  `BrandedEmail.cs` + `Assets/logo.png`), which are inline and easy to miss; a rebrand that skips
  them is incomplete.

## Golden rules — constant (do not violate)
1. **Tenant-scoped, not user-scoped.** App data belongs to the tenant; never leak across tenants.
   Tenant entities implement `ITenantScoped` and are filtered automatically by a global EF query
   filter (see ADR-003); genuinely cross-tenant/pre-auth reads use the sanctioned escape hatch
   `IRepository<T>.QueryAllTenants()`, and signature-/system-authenticated tenant-scoped writes
   (the billing webhook, admin impersonation) enter their tenant via `ITenantContext.EnterTenant`.
   `IgnoreQueryFilters()` is **banned in `src/Api/Features/**`** (fails CI). Only preferences are
   per-user. **A Postgres RLS backstop (ADR-020) re-enforces this at the DB**: a new `ITenantScoped`
   entity must ship its policy in the same migration (`RlsDdl.StatementsFor` — the
   `RlsMigrationGateTests` parity gate fails CI otherwise), and set-based cross-tenant *writes*
   (`ExecuteUpdate/Delete`) need `EnterTenant` — query tags don't render there.
2. **Clean API boundary.** The UI is a client of the API and never accesses the DB directly.
3. **Blazor UI components live in the shared RCL**, not inline in the web app — keeps non-web
   clients cheap.
4. **Derived values are computed, never stored** as stale flags (confirm the app's specific
   derived rules in `docs/DATA_MODEL.md`).
5. **Web-first for features.** The platform ships MAUI desktop + Android shells with auth wired
   (see `docs/MOBILE_TESTING.md`); build each app feature on web first and extend the native
   shells only once it works there.
6. **Latest stable versions only, never previews.**
7. **Test-Driven Development — always.** Write the failing test before the production code on
   every slice. Unit tests (xUnit) in `Core.Tests` / `Api.Tests`; E2E tests (Playwright/NUnit)
   in `E2E.Tests`. No slice merges without tests that drove it; Gherkin scenarios map 1:1 to tests.
8. **Work in vertical slices.** Each slice is end-to-end and leaves the app working; follow
   `docs/WAYS_OF_WORKING.md` for slices, Gherkin stories, Conventional Commits, and the PR
   template. Don't build sprawling multi-epic chunks — propose a split.

## Golden rules — app-specific
1. **The rate is frozen at creation, never recomputed.** `exchange_rate_used` is set once from the
   resolution chain (live → stale-flagged → last transaction → block); edits re-derive amounts from
   *that* rate. No transaction exists without a rate (ADR-V006).
2. **Resolve the budget month by anchor window, never by calendar month.** A date can belong to
   a neighboring month. Always `WeekBoundaryService` / `GET /api/months/resolve` (ADR-V005).
3. **Months exist only through transactions.** No manual create; auto-delete when emptied; validate
   + resolve the rate *before* get-or-create so a rejected request never leaves an empty month.
   Weeks are materialized at creation and never re-sliced.
4. **Every transaction has a bank and a category; class semantics are fixed.** Five classes
   (`budgeted`, `extraordinary`, `unplanned_essential`, `inflow`, `envelope_contribution`);
   `envelope_contribution` needs an envelope + `bank_account`; inflow/envelope are carved out of
   expenses; refunds are derived, only their status is edited; `received` ⇔ a linked inflow (ADR-V007).
5. **Money is dual-currency fixed-point decimal** — `CurrencyMath`, 2 dp, both currencies stored
   (ADR-V004). Billing money stays Stripe's; the two never mix.
6. **Never mutate the user's mail.** Read-only scopes; idempotency = per-household fingerprint +
   cursor; vouchers stage as inert drafts; **confirm is the only draft→transaction path** and it
   goes through `TransactionService.CreateAsync` (ADR-V010).
7. **`EmailConnection` is user-keyed — the one deliberate exception to "tenant-scoped".** It needs
   an `IUserDataContributor`; the poll job `EnterTenant`s the owner's household per connection
   (ADR-V002). Every other budget entity is `ITenantScoped` with a contributor.
8. **Catalogs soft-delete; names are unique per household case-insensitively; clash = 409
   reactivation offer.** Inactive names still render on history. Cross-household = 404 (ADR-V008).
9. **Localize chrome, never user data; seed once in the caller's locale.** Stored values are
   English; display labels come from resx (ADR-V009).
10. **Brand gold `#F2CB6E` is never a data/state color.** Green/red mean under/over budget.

## Tech stack (see `docs/TECH_STACK.md`)
- **Versions:** latest stable on the current .NET line — **.NET SDK 10.0.400 (pinned in `global.json`, the single source of truth, with `rollForward: disable` — the 2026-08 drift showed `latestPatch` let runners outrun both the lockfiles and the MCR image catalog), ASP.NET Core / EF Core packages 10.0.11, Npgsql.EF 10.0.3, PostgreSQL 17.** The SDK is **pinned, not floating** (v3 audit DEP-4): CI's `setup-dotnet` reads `global-json-file: global.json`, and both Dockerfile image tags (`sdk:10.0.400` build, `aspnet:10.0.11` runtime) match it — so a runner-image SDK patch can't outrun the committed `packages.lock.json` (the WASM SDK injects patch-sensitive implicit packages → NU1004 in locked-mode restore).
  - **Bump-together playbook** (do all of these in ONE PR when moving the SDK): ① edit `global.json` `version`; ② regenerate every lockfile with the new SDK (`dotnet restore --force-evaluate`); ③ bump the two `Dockerfile` tags — build `sdk:X` and runtime `aspnet:Y` where Y = the SDK's bundled ASP.NET runtime (check `dotnet --list-runtimes`); ④ reconcile the version strings in this file + `docs/TECH_STACK.md` + `docs/DEPLOYMENT.md`; ⑤ re-check the Apple legs' Xcode requirement (the iOS/macCatalyst workload moves with the SDK; `ci.yml` pins the WORKLOAD SET to the image's default Xcode — non-default Xcodes on the runner images can be incomplete, so bump that pin only together with the image's default Xcode).
- **Backend:** ASP.NET Core Web API behind a clean API boundary.
- **Web frontend:** Blazor WebAssembly; UI components in a shared **RCL** (hard rule).
- **DB:** PostgreSQL via **EF Core (Npgsql)**; schema/migrations generated from `docs/DATA_MODEL.md`.
- **Auth:** custom JWT access tokens + rotating refresh tokens — **not** ASP.NET Core Identity
  (see ADR-002); tenant scoping layered on top via a global query filter.
- **Non-web clients:** MAUI Blazor Hybrid (mobile + Win/macOS desktop) — *deferred, don't build now.*

## Auth rules (constant)
- **Secrets are never in appsettings.** In dev they live in the gitignored repo-root **`.env`**
  (loaded by the API via DotNetEnv; the single local source of truth — see ADR-001); in
  production they come from real environment variables. Keys use the `Section__Sub` form.
  `.env.example` (committed) documents them. Never commit `.env`.
- **New OAuth provider = one line.** Add `.AddXxx()` in `ServiceCollectionExtensions`. Don't
  restructure anything else.
- **Passwordless sign-in uses the `LoginToken` entity + `PasswordlessService`** (NOT Identity
  token providers). Magic links and email OTP are single-use, hashed, and time-limited; lifetimes
  are in config (`Auth:MagicLink:TokenLifespanMinutes`, `Auth:Otp:*`).
- **`IEmailSender` (Core abstraction) is the only way to send email.** Never reference MailKit
  directly outside `Infrastructure/Email/`.
- **JWT Bearer auth is configured** in `Program.cs` (validates the app-issued access token, scheme
  `JwtBearerDefaults.AuthenticationScheme`). The token carries a `tenant_id` claim that drives
  tenant query scoping.

## API documentation (constant)
- **The Postman collection mirrors the API — and the repo copy is canonical (ADR-023; the
  `PostmanParityTests` CI gate enforces the floor).** Any change to API
  endpoints (route, verb, path/query params, request/response shape, auth requirements, or error
  codes) must update **`docs/postman/Vuelto.postman_collection.json`** (+ the environment
  files when config/env expectations change) in the same slice. Controllers in
  `src/Api/Controllers/` and slices under `src/Api/Features/` are the source of truth; the
  collection documents them.
- Keep its conventions: numbered folders per area; `{{baseUrl}}`/`{{accessToken}}` variables with
  collection-level Bearer auth; chaining test scripts that capture shown-once secrets; request
  descriptions stating roles, config gates, and expected error codes; env-specific values live in
  the `*.postman_environment.json` files (one per deploy target), never in the collection.
- Copies in the Postman app/workspace are **mirrors, never the source** (not versioned or
  reviewed). CI keeps the workspace mirror fresh: the `postman-sync` workflow pushes
  `docs/postman/**` to the workspace on every `develop` change (needs `POSTMAN_API_KEY` secret +
  `POSTMAN_WORKSPACE_ID` variable; syncs by name — see `docs/postman/README.md`). Edits made in
  the Postman UI are overwritten on the next sync.

## Scope discipline
Before building anything, check the **"OUT" list in `docs/PROJECT_BRIEF.md`**. Don't implement
deferred items without an explicit decision.

## Conventions
- Code term for the tenant is **tenant**; the reference implementation's app-facing label is
  **Household** (`/api/household`, `HouseholdController`). Rename per app — see `docs/REBRANDING.md`.
- **Tenant label is "Household"** in the UI, routes (`/household`), and docs; "budget" data =
  everything a household owns. The display brand is *"¿Y el vuelto?"*; code, namespaces and DB keep
  `Vuelto` — this split is intentional, do not reconcile it.
- **Stored-value constants live in Core** (`TransactionTypes`, `TransactionSources`,
  `PaymentMethods`, `Currencies`, `RefundStatuses`, `EnvelopeReminderCadences`,
  `SuggestibleClasses`, `PendingVoucherStatuses`, `EmailProviders`, `MonthAnchors`) with `.All`
  whitelists — never string literals in handlers. Labels are resx keys (`Tx_Class_*`, …).
- **Feature route prefixes** (unique per slice, R35): `/api/budget-settings`, `/api/categories`,
  `/api/banks`, `/api/envelopes`, `/api/expenses`, `/api/months`, `/api/transactions`,
  `/api/refunds`, `/api/exchange-rate`, `/api/reports`, `/api/email`, `/api/pending-vouchers`,
  `/api/merchant-mappings`.
- **Error codes carry over from the donor** unchanged (`exchange_rate_unavailable`,
  `refund_status_conflict`, `not_pending`, `*_exists` / `*_exists_inactive`, `invalid_request`,
  `needs_reconsent`) via the platform `ErrorResponse`.
- **Donor cross-references:** stories cite the donor story they port (`from US-015`); new stories
  continue from **US-057**; ADRs cite `donor ADR-00NN`.
- **Pure domain services** (`WeekBoundaryService`, `CurrencyMath`, `DashboardSummaryService`, the
  voucher parsing library) live in `src/Core` with no I/O and are tested in `Core.Tests`.
- **Course material is platform-only.** `docs/tutorial/**` (the Perezosoft course, its lessons,
  diagrams, PDF and generators) is **excluded from downstream apps** and was removed in the first
  docs PR; `AUDIT_SUITE.md` Phase 7 ("course currency") therefore does not apply here. Do not
  re-add it when syncing from the platform.

## Status / not yet decided
- Seed data — **minimal by design** (donor owner decision): `SeedCatalog` holds ~7 example
  categories and Cash + 8 Costa Rican banks (en/es, stable keys), seeded lazily on the first
  catalog read in the caller's locale; **no** default expense lines. The owner's personal data
  loads from a gitignored SQL script after a local DB reset (to be re-authored for this schema).
- Concrete schema (EF Core migrations) — generated from `docs/DATA_MODEL.md`.
- **User stories: generated per-epic at build time**, under `docs/stories/` (one file per epic).
- Non-web framework: **decided and built** — MAUI Blazor Hybrid ships all four native shells
  (epic `NATIVE` ✅ complete 2026-07-14, ADR-018; signing/stores are downstream per ADR-024, resolving the 2026-07-06 amendment's gate). Hosting is
  likewise **decided** (ADR-017: Render free single-origin + Neon + Brevo) — built by epic `DEPLOY`.

## Doc map
| File | Purpose |
|------|---------|
| `CLAUDE.md` (root) | This file — operating manual, auto-loaded |
| `_PLATFORM_PRIMER.md` (root) | Conceptualization primer — paste into a NEW project chat to pre-load the constant decisions and jump straight to what the app does |
| `docs/NEW_APP_GUIDE.md` | **The onboarding spine** — every phase from idea to production, in order, linking the detailed doc per step |
| `docs/OVERVIEW.md` | Friendly platform tour (PM/power-user/developer/architect) — no codebase knowledge assumed |
| `docs/PROJECT_BRIEF.md` | Why/what/scope (lean PRD) + OUT list |
| `docs/FEATURES.md` | User flows & behavior |
| `docs/DATA_MODEL.md` | Entities, relationships, derived rules |
| `docs/TECH_STACK.md` | Stack choices + rationale |
| `docs/DECISIONS.md` | ADR log (the "why") |
| `docs/ARCHITECTURE.md` | Mermaid diagram layer — solution map, seams, per-subsystem class diagrams; drawn from the code, ADR-cross-linked |
| `docs/FLOWS.md` | Sequence diagrams for the core call stacks (auth, tenancy, outbox, billing webhook, dissolve) + the OTP line-level walkthrough |
| `docs/WAYS_OF_WORKING.md` | Slices, story format, commit/PR conventions |
| `docs/audits/AUDIT_SUITE.md` | **The repeatable super-audit** — 5 diagnostic/gate phases + QA-paranoia + docs/course currency; triggered, not routine; `audits/v1..v3` are its worked runs |
| `docs/REBRANDING.md` | Every brand touchpoint to replace per app — **incl. the email templates** |
| `docs/LOCALIZATION.md` | i18n setup (EN/ES live) + how to add a language |
| `docs/MOBILE_TESTING.md` | Run/sign-in on the Android emulator (adb reverse, OAuth) |
| `docs/QA_TEST_PLAN.md` | Manual QA plan — step-by-step tests across web + all four native platforms (161 cases: smoke + regression + §14a v3-audit adversarial/tenant-isolation + §13c native release checklist) |
| `docs/ROADMAP.md` | Sequenced plan — pillars done (JOBS/BILLING/OBS) + the next waves (RBAC, files, GDPR, MFA, …) |
| `docs/STATUS.md` | 2026-07-04 status snapshot + operator guides — native QA pass (✅ 2026-07-14), Apple first-run smoke (MacBook walkthrough), prod activation (⤵ downstream Phase-8 runbook, ADR-017 amendment); SaaS-readiness assessment |
| `docs/PLATFORM_BACKLOG.md` | Per-item design sketches for the future foundation slices (the detail behind ROADMAP) |
| `docs/stories/` | User stories per epic — generated at build time |
| `docs/stories/budget-settings.md` | epic `BUDGET` — app slice P1 (ADR-V001/V003): BUDGET-1 household budget settings (week start, month anchor, income defaults; `/api/budget-settings`; `WeekBoundaryService` in Core) — from donor US-003 / US-015 |
| `docs/stories/catalog.md` | epic `CATALOG` — app slice P2 (ADR-V008/V009): CATALOG-1 categories + CATALOG-2 banks (soft delete, case-insensitive uniqueness, 409 reactivation offer, seed once in the caller's locale; `/api/categories`, `/api/banks`; `SeedCatalog` in Core) — from donor US-010 / US-013 / US-019 / US-047 |
| `docs/stories/exchange-rate.md` | epic `FX` — app slice P3 (ADR-V006): FX-1 live USD→CRC rate with the fallback chain (live → stale "as of" → last transaction → 503 `exchange_rate_unavailable`; `GET /api/exchange-rate`; Core seams `IExchangeRateService` / `IExchangeRateResolver` / `IRecentRateSource`; Home badge) — from donor US-014 / US-034 |
| `docs/stories/envelopes.md` | epic `ENV` — app slice P4 (ADR-V007/V008): ENV-1 savings envelopes (annual target ₡/$, reminder cadence `monthly` \| `five_week_months`, soft delete, 409 reactivation offer, never seeded; `/api/envelopes`, `/envelopes`) — from donor US-012 (envelope half) / ADR-0018 |
| `docs/stories/ui.md` | epic `UI` ✅ COMPLETE — **retrospective** (v3 T59, closing v2 DOC-22): the four 2026-07 web-UI slices that shipped without a story file — UI-1 GDPR export/erasure UI, UI-2 MFA UI, UI-3 notification bell/prefs UI, UI-4 staff `/admin` console; defines what QA §2 + the traceability matrix cite |
| `docs/stories/billing.md` | epic `BILLING` ✅ COMPLETE — entitlements + Checkout + webhook + Portal (1–4) + seat/usage quotas (5, `IQuotaService`) + trial/dunning (6, `IBillingNotifier` + lapse sweep via NOTIFY) + dissolve cleanup (7, `BillingDataContributor` cancels the provider sub + wipes the projection) + billing page (8, `GET /api/billing` summary + `/billing` UI, fake-provider E2E upgrade loop) + seat re-check at invitation accept (9, 2026-07-14: downgrade left stale invites joinable past the cap → 402 `seat_limit_reached` + `/join` "household full" state, self-heals on upgrade); ADR-006 |
| `docs/stories/async-jobs.md` | epic `JOBS` ✅ COMPLETE — outbox+dispatcher, inbox, scheduler (ADR-007) |
| `docs/stories/observability.md` | epic `OBS` ✅ COMPLETE — logging, OpenTelemetry, health, append-only audit log (ADR-008) |
| `docs/stories/rbac.md` | epic `RBAC` ✅ COMPLETE — `admin` role + permission seam (RBAC-1) + owner-only role change (RBAC-2) + admin-aware roster UI (RBAC-3); ADR-009 |
| `docs/stories/files.md` | epic `FILES` ✅ COMPLETE — `IFileStorage` local/S3, tenant-scoped keys, signed URLs (FILES-1 abstraction, FILES-2 download, FILES-3 S3); ADR-010 |
| `docs/stories/gdpr.md` | epic `GDPR` ✅ COMPLETE — tenant data export + account erasure on the contributor/dissolve/file-storage machinery (GDPR-1 export, GDPR-2 erasure); ADR-011 |
| `docs/stories/mfa.md` | epic `MFA` ✅ COMPLETE — authenticator TOTP; Otp.NET, secret encrypted, hashed recovery codes (MFA-1 enroll/manage, MFA-2 JSON-path step-up, MFA-3 OAuth/magic-link redirect step-up, MFA-4 native step-up — enforced on **every** sign-in path); ADR-012 |
| `docs/stories/notify.md` | epic `NOTIFY` ✅ COMPLETE — per-user in-app notification center + delivery prefs, fan-out via the outbox (NOTIFY-1 center, NOTIFY-2 prefs+email); 2026-07-09: caller-scoped delete/clear (`DELETE /{id}`, bulk `?read=true`/all) + bell trash/clear-read/clear-all UI; ADR-013 |
| `docs/stories/admin.md` | epic `ADMIN` ✅ COMPLETE — config-gated platform-staff surface: cross-tenant inspection + short-lived audited impersonation + staff announcements via NOTIFY fan-out (ADMIN-1 gate/inspect, ADMIN-2 impersonate, ADMIN-3 announce — audited in-tenant, per-user rows only); 2026-07-09 (ADR-021 — enumerated admin **writes**): announce `user_ids` targeting + platform-wide `announce-all` (202 → outbox fan-out) + subscription comp/revert (409 when Stripe-backed) w/ console UI; ADR-014 |
| `docs/stories/pubapi.md` | epic `PUBAPI` — public API + tenant API keys, **config-gated default-off** (PUBAPI-1 ✅ — hash-only keys, API-key auth scheme → `tenant_id`-scoped principal, owner mgmt, scoped `/api/public`; PUBAPI-2 ✅ — per-key rate limit + anonymous public OpenAPI doc `/api/public/openapi.json`); ADR-015 |
| `docs/stories/hooks.md` | epic `HOOKS` — outbound webhooks, **config-gated default-off** (HOOKS-1 ✅ — `WebhookSubscription` encrypted secret, `IWebhookPublisher` fan-out → outbox → HMAC-signed POST w/ retry, owner `/api/webhooks` + send-test; HOOKS-2 ✅ — delivery log + replay); ADR-016 |
| `docs/stories/theme.md` | epic `THEME` ✅ COMPLETE — per-user dark mode (THEME-1): Light/Dark/System on Bootstrap `data-bs-theme`; pre-paint `theme.js` bootstrap in BOTH hosts' index.html (parity), `IThemePersistence`/localStorage (one impl serves web + MAUI — no Preferences bootstrap needed, unlike culture), header + login `ThemeSwitcher`, `User.Theme` + `PUT /api/auth/theme` + `theme` JWT claim + `MainLayout` reconcile; dark token block in `app.css`; E2E `ThemeJourneyTests` (suite 30→31), QA-SET-08/DSK-15/AND-14. **Amended by PREFS-1** ("system" now stored verbatim; reconcile on every sign-in) |
| `docs/stories/prefs.md` | epic `PREFS` ✅ COMPLETE — per-user preference sync (PREFS-1, ADR-022): Settings → Preferences card (language + theme switchers, signed-in home); `AuthService.SignedIn` event → `MainLayout` reconciles on EVERY sign-in path (not just cold starts); two-way sync (server wins; never-set server value adopts the device choice — a pre-auth login-page pick becomes the account pref; never while impersonating); locale mismatch = persist + ONE reload (WASM satellite assemblies — reverts B7-3's in-process switch); "system" stored verbatim (null = never chose). Fixes QA-I18N-02 (was: locale unreachable from UI) + theme-after-OTP-sign-in instability; E2E `LocaleChoice_FollowsTheUser_AcrossBrowsers` (suite 31→32), QA-I18N-02 now ⚙️ automated |
| `docs/stories/e2e.md` | epic `E2E` ✅ COMPLETE — Playwright journeys (suite 7→26 tests): E2E-1 RBAC roster; E2E-2 billing seat-quota 402 UX; E2E-3 notification bell/prefs (list/mark-read covered via ADMIN-3 announcements); E2E-4 magic-link sign-in (happy + single-use); E2E-5 membership lifecycle (transfer/leave/dissolve/delete-account); BILLING-8 added the fake-provider upgrade-loop journey. Health = DEPLOY-3 smoke, not a browser test |
| `docs/stories/deploy.md` | epic `DEPLOY` ✅ COMPLETE — staging/prod on the free tier: DEPLOY-1 single-origin (API serves the WASM) + config-gated forwarded headers; DEPLOY-2 Dockerfile + compose parity + `render.yaml` + `docs/DEPLOYMENT.md` (staging live, all 4 sign-in paths verified); DEPLOY-3 CI deploy pipeline (develop→staging auto + version-gated smoke, main→prod gated) + QA §1.5; ADR-017 |
| `docs/DEPLOYMENT.md` | Deployment runbook (DEPLOY-2/3) — Render + Neon + Brevo free-tier bring-up; `Dockerfile` + `render.yaml` reference; required env incl. the Production Stripe-key guard; §6 CI-gated auto-deploy |
| `docs/stories/native.md` | epic `NATIVE` ✅ COMPLETE (2026-07-14 — NATIVE-6 device pass green; distribution moved downstream, ADR-024) — full MAUI parity (Android/Windows/iOS/macOS): NATIVE-1 ✅ CI build gate (all 4 TFMs; Apple legs on develop pushes — 10× macOS minutes; Maui lockfile excluded by design) + NATIVE-2 ✅ parity audit → gaps G1–G6 + NATIVE-4b ✅ join-by-invite-code on /join (G5 closed; E2E suite 26→28) + NATIVE-5 ✅ culture bootstrap (G6 closed: ICulturePersistence seam, MAUI Preferences + MauiProgram bootstrap; Windows-verified via WebView2 CDP) + NATIVE-3 ✅ downloads (G1 closed: Content-Disposition attachment + IFileDownloadLauncher — web same-tab download, native OS share sheet; E2E suite 28→29); + NATIVE-4 ✅ (G2 refresh-on-resume AppResumeNotifier + G3 Android back handler) — Wave 2 COMPLETE, all six gaps closed; NATIVE-6 QA plan authored (117 cases: DSK-08..14, AND-07..13, iOS/mac first-run smoke, release checklist) + G7 Apple-boot fix (iOS/macCatalyst crashed at startup — WebAuthenticator initiator generalized + Info.plist schemes); **Apple column UNPINNED 2026-07-06** — §13b run on the maintainer's MacBook: QA-IOS-01/02/04 + QA-MAC-01/02 + OAuth PASS (two gaps fixed in PR #125: SMTP revocation knob + Catalyst Debug session store); NATIVE-7 ✅ COMPLETE smokes for all four platforms in CI (Windows WebView2-CDP + Android emulator playwright-core _android + iOS-simulator & Mac Catalyst boot-to-login canaries in one `native-smoke-apple` job — WKWebView has no CDP, so they assert process-alive + provider-probe-200, no UI driving; native-paths gate skips Apple/smoke legs on docs-only pushes; deploy-staging concurrency); **NATIVE-6 ✅ FULL PASS 2026-07-14** — the maintainer completed the entire manual QA process as the plan stood then (125 cases incl. the leftover §13b spot-checks), no open findings; post-pass additions (§14a re-runs + QA-AND-15) are the open device items; **NATIVE-8..11 (signing/installers/stores) ⤵ MOVED DOWNSTREAM (ADR-024)** — per-app work, checklist in `NEW_APP_GUIDE.md` Phase 9, Wave 4 kept as reference incl. the two signing-identity re-verify traps (packaged-MSIX SecureStorage + Apple keychain-access-groups); ADR-018 |
| `docs/NATIVE_PARITY.md` | NATIVE-2 audit — WebView-vs-browser deltas × platform × screen (✅/⚠️/🔍 verdicts); gap register G1–G6 → Wave-2 slices; maintainer rules (index.html sync, emailed links land on web, forceLoad = leaves the app) |
| `docs/postman/` | **Postman collection for the whole API** (collection + per-env environments: local, staging/Render + README) — chained OTP sign-in w/ Mailpit auto-fetch, token rotation, all surfaces incl. config-gated PUBAPI/HOOKS and the admin writes. **CI-mirrored to the Postman workspace** (`postman-sync` on develop; one-way, repo canonical); rename on rebrand (incl. the workflow's file path) |
| `.env.example` | **Config catalog** — every configurable key + its default (CONFIGURATION REFERENCE block) + the compiled-in "not configurable" limits; CI-enforced source of truth (`ConfigKeys_ReadInCode_AreDocumented`) |
| `.github/pull_request_template.md` | PR checklist (auto-loaded by GitHub) |
| `src/Infrastructure/Persistence/Migrations/` | Concrete schema — EF Core migrations generated from DATA_MODEL.md |
