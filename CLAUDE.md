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
the donor's tests as the spec.

**The platform comes as packages** (`Perezosoft.Platform.*` at `PlatformPackagesVersion` in `Directory.Packages.props`,
ADR-034; converted 2026-11, Pkg V.1–V.6): there is no platform code in this repo to modify. vuelto is the host
(`src/Host`: `AppComposition.cs`, its slices under `Features/`, its entities, domain logic — `Budget/`, `Mail/`,
`Vouchers/`, `ExchangeRate/` — and `Persistence/` with every migration), the UI seed (`src/Shared.Ui`, `src/Web`,
`src/Maui`) and the tests of what it owns (`tests/Api.Tests/App/`, `tests/Ui.Tests`, `tests/E2E.Tests`). Extend the
platform through its seams; a generic gap goes upstream to `perezosoft-platform` first, then arrives with a release
(`dotnet platform port -To <version>`). The platform's tooling is `dotnet platform …` (`dotnet tool restore` first).

## Read before you act
- **This app owns a dev PORT BLOCK, and it is not the platform's.** `perezosoft-platform` seeds every
  new app with its own ports, so out of the box an app cannot run beside the platform *or* beside a
  sibling app — same Web port, same API port, first one wins and the second dies. The convention is
  **+100 per downstream app** on all four. This repo's block:

  | | https | http |
  |---|---|---|
  | Web | **7108** | **5269** |
  | API | **7260** | **5338** |

  (Platform keeps 7008/5169 + 7160/5238; the next app takes 7208/5369 + 7360/5438.) The http API leg
  is the one the Android emulator reaches through `adb reverse tcp:5338 tcp:5338`, so **provider
  redirect URIs must name `http://localhost:5338/signin-{provider}`** — see the platform's [MOBILE_TESTING](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/MOBILE_TESTING.md).
  `docs/qa-runs/**` still records the old ports on purpose: it logs what a run actually used.
- **CI is the platform's reusable workflow** (`.github/workflows/ci.yml` calls
  `argamboad/perezosoft-platform/.github/workflows/app-ci.yml@v<the platform version>`; bump the tag with
  `PlatformPackagesVersion`). The gates, the change classifier, the device legs and the deploys are the platform's;
  this repo adds one job of its own, `ui-js` (node over `tests/ui-js`). ⚠️ A green PR run is NOT a green Apple smoke.
- **GitHub is home (ADR-030, 2026-10-02; Forgejo retired).** `origin` is `argamboad/vuelto` (public).
  Branches, PRs, merges and issues happen on GitHub. CI (ADR-031): a pull request runs the web gates only;
  the device legs (MAUI builds, native smokes) run from *Run workflow* → `devices`, and deploys from
  *Run workflow* → `deploy` (staging from develop, prod from main) behind every web gate. The repo is public
  (since 2026-10-09), so its Actions minutes are free; still say what a run costs before starting one. Runbook: the platform's [DEPLOYMENT](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/DEPLOYMENT.md) §6.
- **Writing or modifying ANY code → the platform's [`FOUNDATION_RULES_v3.md`](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/audits/v4-2026-09/FOUNDATION_RULES_v3.md) (v3.1: R1–R35
  from v1.0, R36–R76 from v2.0, R81–R162 minus the retired R94, R116, R117, R138 and R156; R77–R80 retired) is
  binding.** It encodes the post-audit invariants (tenancy incl. the RLS backstop parity, second-factor/
  event replay, SSRF, fail-closed normalization, atomic quotas + single-use credentials, per-user AND
  per-tenant erasure completeness, injected clocks, slice boundaries, host parity, doc/Postman sync — and
  since v4: refresh-grace one-shot, durable outbox accounting, redirect-refusing webhooks, web-only PR CI with on-request device legs (ADR-031),
  Release-build guards) as machine-enforced arch tests + CI gates; each
  `[machine]` rule names the test that holds it. Gates guard behaviour and the truth of documents, never
  process (ADR-033): a slip gets a test for the thing that slipped. Comply; if a task
  seems to require violating a rule, stop and surface it. The frozen quality bar lives in the platform's [CONTRIBUTING](https://github.com/argamboad/perezosoft-platform/blob/develop/CONTRIBUTING.md)
  (v2.0 and v1.0 remain in the platform's `docs/audits/` as the historical layers).
- **Hardening the template → the platform's [`AUDIT_SUITE.md`](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/audits/AUDIT_SUITE.md).** The single repeatable
  super-audit (5 diagnostic/gate phases + QA-paranoia + docs/course currency) that produced the `audits/v*`
  runs. It's **triggered, not routine** — run it on a structural core change, a new wave of epics, a major
  dependency bump, or before generating a production app (at minimum re-run its Phase 4 adversarial slice
  pass). Between triggers, keep the gates green instead of re-auditing.
- Touching the schema or entities → read **`docs/DATA_MODEL.md`** first.
- Implementing a screen or flow → read **`docs/FEATURES.md`** first.
- Starting a build slice → read **the platform's [WAYS_OF_WORKING](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/WAYS_OF_WORKING.md)** (slices, story format, PR/commit
  conventions).
- Adding an app feature → follow the **clean-platform + vertical-slice convention** in
  the platform's [WAYS_OF_WORKING](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/WAYS_OF_WORKING.md) (and ADR-004). The platform's `Notes` sample slice was **deleted in the
  rebrand PR** (this repo has no sample); the four-file anatomy in the platform's [WAYS_OF_WORKING](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/WAYS_OF_WORKING.md) is the
  reference, and `perezosoft-platform/src/Host/Features/Notes` can be consulted read-only.
- Wondering *why* something is the way it is → check **`docs/DECISIONS.md`** before changing it.
- Changing a settled decision → add a new dated ADR in `docs/DECISIONS.md`; don't silently
  reverse it.
- Rebranding (name, logo, colours, tagline) → follow **the platform's [REBRANDING](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/REBRANDING.md)** and complete every
  item. The **transactional emails** take vuelto's brand through `PlatformBrand` in
  `src/Host/AppComposition.cs` (name, palette, tagline, `src/Host/Email/Assets/logo.png`, and
  `src/Host/Email/EmailStrings*.resx` for the strings that name the app); a rebrand that skips them is incomplete.

## Golden rules — constant (do not violate)
1. **Tenant-scoped, not user-scoped.** App data belongs to the tenant; never leak across tenants.
   Tenant entities implement `ITenantScoped` (or `ISharedOrTenantScoped` for shared-or-owned catalog rows, Arch A4)
   and are filtered automatically by a global EF query
   filter (see ADR-003); genuinely cross-tenant/pre-auth reads use the sanctioned escape hatch
   `IRepository<T>.QueryAllTenants()`, and signature-/system-authenticated tenant-scoped writes
   (the billing webhook, admin impersonation) enter their tenant via `ITenantContext.EnterTenant`.
   `IgnoreQueryFilters()` is **banned in `src/Host/Features/**`** (fails CI). Only preferences are
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
   (see the platform's [MOBILE_TESTING](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/MOBILE_TESTING.md)); build each app feature on web first and extend the native
   shells only once it works there.
6. **Latest stable versions only, never previews.**
7. **Test-Driven Development — always.** Write the failing test before the production code on
   every slice. Unit tests (xUnit) in `Api.Tests` (vuelto's under `App/`) / `Ui.Tests`; E2E tests (Playwright/NUnit)
   in `E2E.Tests`. No slice merges without tests that drove it; Gherkin scenarios map 1:1 to tests.
8. **Work in vertical slices.** Each slice is end-to-end and leaves the app working; follow
   the platform's [WAYS_OF_WORKING](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/WAYS_OF_WORKING.md) for slices, Gherkin stories, Conventional Commits, and the PR
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

## Tech stack (see the platform's [TECH_STACK](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/TECH_STACK.md))
- **Versions:** the platform's, through its packages — **.NET SDK 10.0.403** pinned in `global.json` (`rollForward:
  disable`), the platform at `PlatformPackagesVersion` in `Directory.Packages.props`, which brings its own ASP.NET
  Core / EF Core / Npgsql line; PostgreSQL 17. A platform release moves them together: `dotnet platform port` writes
  the version, the stamp and the starter's files, and the platform's NEW_APP_GUIDE lists what follows (the lockfiles,
  the Dockerfile tags, the Apple legs' Xcode). vuelto's own packages (QuestPDF, HtmlAgilityPack, PdfPig for the
  tests) are pinned beside them; `docs/README.md` lists its stack beyond the platform's.
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
  files when config/env expectations change) in the same slice. The platform's controllers (in the
  Perezosoft.Platform.Api package) and the slices under `src/Host/Features/` are the source of truth; the
  collection documents them.
- Keep its conventions: numbered folders per area; `{{baseUrl}}`/`{{accessToken}}` variables with
  collection-level Bearer auth; chaining test scripts that capture shown-once secrets; request
  descriptions stating roles, config gates, and expected error codes; env-specific values live in
  the `*.postman_environment.json` files (one per deploy target), never in the collection.
- Copies in the Postman app/workspace are **mirrors, never the source** (not versioned or
  reviewed). CI keeps the workspace mirror fresh: the `postman-sync` workflow pushes
  `docs/postman/**` to the workspace on every GitHub `develop` change (needs `POSTMAN_API_KEY` secret +
  `POSTMAN_WORKSPACE_ID` variable set on GitHub — both are; syncs by name — see `docs/postman/README.md`). Edits made in
  the Postman UI are overwritten on the next sync.

## Scope discipline
Before building anything, check the **"OUT" list in `docs/PROJECT_BRIEF.md`**. Don't implement
deferred items without an explicit decision.

## Conventions
- Code term for the tenant is **tenant**; the reference implementation's app-facing label is
  **Household** (`/api/household`, `HouseholdController`). Rename per app — see the platform's [REBRANDING](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/REBRANDING.md).
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
  voucher parsing library) live in `src/Host/Budget/`, `src/Host/Vouchers/` with no I/O and are tested under
  `tests/Api.Tests/App/` (`Budget/`, `Vouchers/` — Core.Tests folded in, 2026-11).
- **Course material is platform-only.** `docs/tutorial/**` (the Perezosoft course, its lessons,
  diagrams, PDF and generators) is **excluded from downstream apps** and was removed in the first
  docs PR; `AUDIT_SUITE.md` Phase 7 ("course currency") therefore does not apply here. Do not
  re-add it when syncing from the platform.

## Status / not yet decided
- Seed data — **minimal by design** (donor owner decision): `SeedCatalog` holds ~7 example
  categories and Cash + 8 Costa Rican banks (en/es, stable keys), seeded lazily on the first
  catalog read in the caller's locale; **no** default expense lines. The owner's personal data
  loads from the gitignored `my-seed.sql` at the repo root after a local DB reset (re-authored for
  this schema 2026-09-04 — P11: fixed user/household ids, 61 categories, 9 banks, 18 budget lines,
  5 merchant rules; `docker exec -i vuelto-db-1 psql -U dev -d dev_db < my-seed.sql`).
- **Carry the household to staging/prod (hidden operator tool):** `ops/snapshot-household.sql` — snapshot one
  household (identity + every budget table, ids preserved, FK order, idempotent) from the source DB as a
  SQL file and restore it once on the target as the owner role; inboxes are reconnected there (tokens are
  key-ring bound). the platform's [DEPLOYMENT](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/DEPLOYMENT.md) §9; `HouseholdSnapshotTests` = round-trip + table-list gate.
- **Two-role RLS posture on a hosted DB (hidden operator tool):** `ops/staging-rls.ps1 -OwnerUrl <neon url>
  -RuntimePassword <new>` — provisions `app_runtime` via the compose container's `psql`, proves read-OK /
  DDL-denied, prints the `DefaultConnection` / `Migrations` / `Rls__EnforceRuntimeRole` values for the host.
  the platform's [DEPLOYMENT](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/DEPLOYMENT.md) §7; click-by-click in `ops/README.md`.
- **Installable phone/desktop builds against a host:** `dotnet platform publish-native -ApiBaseUrl <host> [-Out …]` (host required) — Release
  Android APK (apksigner v2+v3 with the debug key, upgrades over a VS Code install) + Windows unpackaged exe, the API
  base compiled in. Recipe + the signing/`RuntimeIdentifier`/callback-scheme gotchas: the platform's [DEPLOYMENT](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/DEPLOYMENT.md) §9;
  owner-level steps in `ops/README.md`.
- **Port parity reached (P0–P10b merged, PRs #1–#17).** P11 closed the repo side 2026-09-04:
  localization sweep (566 keys, EN/ES parity, zero missing — `LocalizationKeyCoverageTests` gate),
  app-slice QA pass recorded in `docs/qa-runs/2026-09-run-log.md` (29/32 ✅; FX-01, EMAIL-02,
  EMAIL-04 await the owner's credentials), `render.yaml` + the platform's [DEPLOYMENT](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/DEPLOYMENT.md) carry the app's keys
  (`ExchangeRate__ApiKey`, Microsoft consent, the mail-consent callback URI). **Staging itself is the
  owner's external checklist** (Neon, Render blueprint, Brevo, OAuth apps, the deploy hook + base URL
  secrets) — until it is green the old Vercel/Railway/Supabase deployment stays up. Then the roadmap
  resumes as ordinary slices (data-driven bank definitions, deployment follow-ups, platform freebies).
- Concrete schema (EF Core migrations) — generated from `docs/DATA_MODEL.md`.
- **User stories: generated per-epic at build time**, under `docs/stories/` (one file per epic).
- Non-web framework: **decided and built** — MAUI Blazor Hybrid ships all four native shells
  (epic `NATIVE` ✅ complete 2026-07-14, ADR-018; signing/stores are downstream per ADR-024, resolving the 2026-07-06 amendment's gate). Hosting is
  likewise **decided** (ADR-017: Render free single-origin + Neon + Brevo) — built by epic `DEPLOY`.

## Doc map
| File | Purpose |
|------|---------|
| `CLAUDE.md` (root) | This file — operating manual, auto-loaded |
| `docs/README.md` | **Index of this repo's documents + links to the platform's** (NEW_APP_GUIDE, WAYS_OF_WORKING, ARCHITECTURE, FLOWS, DEPLOYMENT, MOBILE_TESTING, LOCALIZATION, REBRANDING, TECH_STACK, DECISIONS, the foundation rules) — and vuelto's specifics: ports, staging, OAuth redirect URIs, mail consent, its settings, staging/prod RLS, the household snapshot and the data-copying deploy runbooks, its stack beyond the platform's |
| `docs/PROJECT_BRIEF.md` | Why/what/scope (lean PRD) + OUT list |
| `docs/FEATURES.md` | User flows & behavior |
| `docs/DATA_MODEL.md` | Entities, relationships, derived rules |
| `docs/DECISIONS.md` | vuelto's decisions, `ADR-V…` (the "why"); the platform's `ADR-…` are in [its log](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/DECISIONS.md) |
| `docs/QA_TEST_PLAN.md` | Manual QA plan — step-by-step tests across web + all four native platforms (225 cases: smoke + regression + §14a v3-audit adversarial/tenant-isolation + §14c pre-launch gates + §14d v4-audit adversarial, deploy drills and regressions + §13c native release checklist) |
| `docs/UI_REDESIGN_PLAN.md` | **MERGED — PR #63 (2026-09-14): all 11 slices + the additive API fields + the fix set. Follow-ups merged the same day: budget lines carry no bank (PR #64), refund `case_number` column dropped (PR #66), native OAuth trailing-slash fix + Cancel while the browser is out (PR #65)** — the `UI Handout.pdf` presentation-layer redesign read against this codebase: epic `SKIN`, eleven app-side slices shipped as ONE PR with a commit each (11 conforms the fourteen screens the handout never drew), five stale claims in it (incl. three silent removals of shipped behaviour), what it costs in strings/tests/QA, the three design decisions (each with a proposal, each due at its own slice gate), the per-slice QA-plan impact map (§5b), and the show-then-green-light rule before every slice |
| `docs/REPORTS_PDF_INCOME_PLAN.md` | **Plan (owner-confirmed 2026-09-16)** for the report PDF (REPORTS-7, built), "Email me this report" (REPORTS-8, built), income lines with a pay period (INCOME-1, built) and income by member (INCOME-2, built): decisions D1–D9, confirmed assumptions A1–A10, the data-safety rules for the income migration (additive only, parity query, restore point before merge; dropping the old columns is a later owner-gated INCOME-3), and the one-branch / one-commit-per-slice / one-PR working mode |
| `docs/stories/` | vuelto's user stories per epic (the platform's epics are the platform's) |
| `docs/stories/budget-settings.md` | epic `BUDGET` — app slice P1 (ADR-V001/V003): BUDGET-1 household budget settings (week start — the payday for weekly pay — and month anchor; the income defaults moved to INCOME-1; `/api/budget-settings`; `WeekBoundaryService` in Core) — from donor US-003 / US-015 |
| `docs/stories/catalog.md` | epic `CATALOG` — app slice P2 (ADR-V008/V009): CATALOG-1 categories + CATALOG-2 banks (soft delete, case-insensitive uniqueness, 409 reactivation offer, seed once in the caller's locale; `/api/categories`, `/api/banks`; `SeedCatalog` in Core) — from donor US-010 / US-013 / US-019 / US-047; CATALOG-3/CARDS-1 the household's cards — brand + last four from the voucher, alias by the household, `VISA-1234` on first sight (`/api/cards`, `/cards`, optional `card_id` on transactions, ADR-V021); CARDS-4 a half-hidden number (`XXXXXXXXXXX8755X`) is asked about in Review and remembered (`CardPattern`, `card_id` / `skip_card` on confirm, ADR-V027, #210) |
| `docs/stories/exchange-rate.md` | epic `FX` — app slice P3 (ADR-V006, ADR-V019): FX-1 the day's USD→CRC **buy/sell pair** (BCCR via the Finance Ministry mirror, no key; the one-rate world feed selectable) with the fallback chain (live → stale "as of" → last transaction → 503 `exchange_rate_unavailable`; `GET /api/exchange-rate` with `buy`/`sell`); the direction rule in Core `FxRates` (spend USD → sell, spend CRC → buy, income the mirror; a transaction freezes the side its currency selects); seams `IExchangeRateService` / `IExchangeRateResolver` / `IRecentRateSource`; badge + form prefill — from donor US-014 / US-034 |
| `docs/stories/envelopes.md` | epic `ENV` — app slice P4 (ADR-V007/V008): ENV-1 savings envelopes (annual target ₡/$, reminder cadence `monthly` \| `five_week_months`, soft delete, 409 reactivation offer, never seeded; `/api/envelopes`, `/envelopes`) — from donor US-012 (envelope half) / ADR-0018 |
| `docs/stories/income.md` | epic `INCOME` — owner request (ADR-V023): INCOME-1 household income lines (member, currency, fixed \| variable, pay period `weekly` \| `biweekly` \| `monthly`, amount per payment, biweekly pay days; soft delete, 409 reactivation offer, ordered; `/api/incomes`, `/incomes`), each new month snapshots them into editable `MonthIncome` rows (`IncomeSnapshot` in Core; `PUT /api/months/{id}/income` takes the full list), additive `AddIncomeLines` migration with `ops/backfill-income-lines.sql` + `ops/check-income-parity.sql`, old income columns kept until INCOME-3; INCOME-2 income by member (`IncomeByMember` in Core → `income_by_member` on the category analysis, a table in the PDF; no chart); INCOME-4 income lives on the Budget page (an Income card above the lines, Edit income → `/incomes`; no Settings entry, #203) |
| `docs/stories/ledger.md` | epic `LEDGER` — app slice P5 (ADR-V005/V006/V007): LEDGER-1 months & weeks that exist only through transactions (anchor-window resolve, auto-create with income snapshot, auto-delete) + LEDGER-2 transactions (frozen rate, derived amounts, required bank/category, envelope rules; `/api/months`, `/api/transactions`; `/months`, `/transactions/new`) — P5a; LEDGER-3 expected refunds derived from unplanned essentials, realized as read-only inflows under a conditional flip (`/api/months/{id}/refunds`, `/api/refunds/{id}`) — P5b — from donor US-004/006/007/008/009/012/015, WU-2; LEDGER-5 a discretionary purchase can expect a refund too, never a budgeted one (ADR-V025, #201); LEDGER-6 a refund is stored as its amount (typed as ₡ or %), and a received one is locked until put back to pending (ADR-V026, #202); LEDGER-7 the month's refunds show received / pending / expected totals (`{ refunds, totals }`, #206); LEDGER-8 the `/refunds` page — every refund across months, filtered, grouped, several marked received at once (`GET /api/refunds`, #208) |
| `docs/stories/expense-lines.md` | epic `EXPENSES` — app slice P6 (ADR-V007/V008): EXPENSES-1 fixed & variable budget lines (`IExpenseLine`, single-currency budget, required category backing ≤ 1 active line across both lists, optional bank, soft delete + 409 offer, atomic reorder, never seeded; `/api/expenses/{fixed\|variable}`; `/budget`) — from donor US-016 / US-017 / US-054 |
| `docs/stories/dashboard.md` | epic `DASH` — app slice P7 (ADR-V004/V006/V007): DASH-1 the month dashboard (pure `DashboardSummaryService` in Core: frozen actuals vs live projections, lines vs actuals, other spending, weekly rows, the "This month" waterfall (income → class spend → left now → still planned → forecast; ADR-V018 replaced the Expenses/Balance cards, `spent_*` on the summary), unplanned + pending refunds, envelope reminders, bank × method; `GET /api/months/{id}/summary`, `rate_unavailable` blocks projections; `/dashboard`, `/dashboard/{id}`) — from donor US-005 / US-012 / US-018 / US-055; DASH-2 the "Show in" ₡ · $ · both preference — device copy + account copy in the user-keyed `UserDisplaySettings` (`/api/display-settings`, ADR-V020); DASH-3 the week cut's Unplanned column (`weekly_unplanned`, #211); DASH-4 the Show in switch on the month page, Months and Budget (#207) |
| `docs/stories/reports.md` | epic `REPORTS` — app slice P8 (ADR-V004/V005/V006, platform ADR-010): REPORTS-1 category analysis by class for a month (anchor window, budgets decorated) or a date range (`GET /api/reports/category-analysis`, pure `CategoryAnalysisCalculator`); REPORTS-2 CSV export of a period stored via `IFileStorage` behind a 15-min signed link and delivered through `IFileDownloadLauncher` (`POST /api/reports/transactions/export`, `TransactionCsvWriter`, 4-dp rate); `/reports` + month-page Export button — from donor US-043 / US-044 / WU-4; REPORTS-3 charts (Table/Chart view, ₡/$ switch, inline-SVG `BarChart`/`DonutChart`, "Spend by class" + "Income vs spend" + "Income vs budget" donuts — on the shared `Core` `IncomeCalculator` / `BudgetTotals`, `income` + `budget_total` on the analysis response, null for a range / no rate); REPORTS-4 pace `LineChart` (`spend_by_day`, plan line, today marker), "Month by month" bars (`GET /api/reports/months-trend`, pure `MonthTrendCalculator`), "Spend by bank" / "Card vs account" donuts (`by_bank` / `by_method`, any period); REPORTS-7 the report as a PDF — `POST /api/reports/pdf` rendered by QuestPDF from the page's own figures (pure `ReportPdfModelBuilder`, own `PdfCharts` SVG builders, Nunito embedded) + a landscape appendix of the CSV's rows (`ReportHandler.ExportRowsAsync`), delivered through the CSV's signed link; PDF dialog on `/reports` (ADR-V022); REPORTS-8 "Email me" — `POST /api/reports/pdf/email` queues the same PDF as an attachment to the caller's own address (10/day per person, `ReportEmailRateLimit`); both PDF endpoints follow the language saved in the account; REPORTS-9 the appendix's columns are the reader's choice (`appendix_columns`: category, class, amount, rate, method, bank, source, card, notes — date and payee always; absent = all; the dialog ticks all each time) |
| `docs/stories/email-ingestion.md` | epic `EMAIL` — port slices P9/P10 (ADR-V002/V010/V016): EMAIL-1 ✅ voucher parser library (`Vuelto.Core.Vouchers` + `Vuelto.Infrastructure.Vouchers`, BAC/BN extractors, routing-as-data, real fixtures); EMAIL-2 ✅ user-keyed `EmailConnection` + consent round-trip on the platform's OAuth apps with Data-Protection tokens/state (`/api/email/connections`, anonymous callback, `/email` page, `IUserDataContributor`); EMAIL-3 ✅ Graph + Gmail readers (`IEmailReader`, filters pushed into the provider query, refresh-once, paging/saturation, folder pickers); EMAIL-4 ✅ staging & dedup (`PendingVoucher` + `IngestedVoucher` tombstone with RLS, `VoucherFingerprint`, `VoucherStagingService` with the tenant hop + cursor rules, `EmailPollJob` on the platform scheduler, Sync now); EMAIL-5 ✅ merchant → category rules (`MerchantCategoryMapping` with RLS + unique lower-cased `PatternKey`, pure `MerchantMatcher` longest-wins shared with staging, `/api/merchant-mappings`, `/merchant-mappings` page, learn-on-confirm never overwrites); EMAIL-6 ✅ review queue (`/api/pending-vouchers` list/count/confirm/discard; confirm books through the Core `ITransactionService` contract implemented by the Ledger handler — R7 — inside one unit-of-work scope with a conditional flip, `source = email`; discard is a guarded flip; `/review` page with the nav count badge + dashboard banner; only category/class edited, parser blanks opened) — **epic COMPLETE**; EMAIL-7 clear the queue; EMAIL-8 a voucher keeps the rate of the day it arrived (live buy/sell pair frozen on the draft at staging, confirm books its side; ADR-V024); from donor US-025–030 |
| `docs/stories/ui.md` | epic `UI` — UI-5 cards fold and stay folded per device (`CollapsibleCard`, #205); UI-6 no page scrolls sideways on a phone (`PhoneLayoutTests`, #209); ✅ the original four are a **retrospective** (v3 T59, closing v2 DOC-22): the four 2026-07 web-UI slices that shipped without a story file — UI-1 GDPR export/erasure UI, UI-2 MFA UI, UI-3 notification bell/prefs UI, UI-4 staff `/admin` console; defines what QA §2 + the traceability matrix cite |
| `ops/README.md` | **Operator tools runbook** (hidden — no page/route): `snapshot-household.sql` carries one household to another server (what comes across, prerequisites, step by step, dry run, troubleshooting); `staging-rls.ps1` switches a hosted database to the two-role RLS posture in one command (what the two roles are, prerequisites, step by step, troubleshooting) |
| `docs/qa-runs/` | Filled QA run logs — `2026-08-run-log.md` (the platform's 2026-08 full pass, inherited) and `2026-09-run-log.md` (this app's slice pass) — plus `helpers/`, the small scripts a co-pilot run uses (mint a JWT, fetch links from Mailpit, MFA codes, the adversarial drills). A new pass adds a new dated log; the plan itself is `docs/QA_TEST_PLAN.md` |
| `docs/postman/` | **Postman collection for the whole API** (collection + per-env environments: local, staging/Render + README) — chained OTP sign-in w/ Mailpit auto-fetch, token rotation, all surfaces incl. config-gated PUBAPI/HOOKS and the admin writes. **CI-mirrored to the Postman workspace** (`postman-sync` on develop; one-way, repo canonical); rename on rebrand (incl. the workflow's file path) |
| `.env.example` | **Config catalog** — every configurable key + its default (CONFIGURATION REFERENCE block) + the compiled-in "not configurable" limits; CI-enforced source of truth (`ConfigKeys_ReadInCode_AreDocumented`) |
| `.github/pull_request_template.md` | PR checklist (auto-loaded by GitHub) |
| `src/Host/Persistence/Migrations/` | Concrete schema — EF Core migrations generated from DATA_MODEL.md |
