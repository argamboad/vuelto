# Decisions (ADR log)

> Lightweight architecture/product decision records: decision + rationale + date. Stops us (and
> Claude Code) from re-litigating settled choices. Append new ones; supersede with a new dated
> entry rather than rewriting.
>
> The **constant ADRs** below (C-prefixed) are pre-decided across all projects from this platform
> — keep them. Add **app-specific ADRs** (number them 001, 002, …) as you make decisions during
> conceptualization.
>
> **Diagrams:** the shapes these decisions produced are drawn (from the code) in
> [`ARCHITECTURE.md`](ARCHITECTURE.md) (component + class diagrams), [`FLOWS.md`](FLOWS.md)
> (sequence diagrams per call stack), and [`DATA_MODEL.md`](DATA_MODEL.md) (ER + lifecycle
> diagrams). Key mappings: ADR-002 → FLOWS §3–7; ADR-003/020 → ARCHITECTURE §3 + FLOWS §2;
> ADR-006 → ARCHITECTURE §6 + FLOWS §8; ADR-007 → ARCHITECTURE §5 + FLOWS §9–10;
> ADR-011 → FLOWS §11; ADR-012 → FLOWS §6.

## Constant decisions (carry forward — do not re-debate)

**ADR-C1 — Multi-tenant SaaS: Tenant ≠ User, multiple users per tenant.**
*Rationale:* shared workspace model; data belongs to the tenant, not the individual.

**ADR-C2 — Tenant-scoped data; only preferences are per-user.**
*Rationale:* users in a tenant collaborate over shared data; enforce scoping on every query;
never leak across tenants.

**ADR-C3 — Backend is ASP.NET Core Web API behind a clean API boundary.**
UI never hits the DB directly. *Rationale:* the API is the durable, client-agnostic asset reused
by every client.

**ADR-C4 — Web frontend is Blazor WebAssembly (not Server).**
*Rationale:* preserves the "frontend is just another API client" boundary; modern Blazor improved
WASM bundle size and AOT. Server couples UI to server / per-user live connection — rejected.

**ADR-C5 — Blazor UI components live in a shared Razor Class Library (RCL).**
*Rationale:* makes future non-web clients (MAUI Blazor Hybrid) a component-reuse exercise, not a
rewrite. Cheap now, expensive to retrofit.

**ADR-C6 — Database is PostgreSQL.**
*Rationale:* free, portable, cheap to host, capable. Chosen over SQL Server for
economy/portability.

**ADR-C7 — ORM is Entity Framework Core (Npgsql provider).**
*Rationale:* default .NET ORM; first-class Postgres; maps data model to migrations.

**ADR-C8 — Auth is ASP.NET Core Identity; tenant scoping layered on top.**
*Rationale:* built-in user/auth; tenant association sits above Identity as a query concern.
> **Superseded by ADR-002 (2026-06-19):** the platform ships a custom JWT + refresh-token auth
> stack instead of ASP.NET Core Identity.

**ADR-C9 — Non-web clients (mobile + Win/macOS desktop) are MAUI Blazor Hybrid: shells scaffolded with auth wired, feature parity DEFERRED (web-first).**
The platform ships MAUI desktop + Android shells with auth already wired (see `docs/MOBILE_TESTING.md`);
build each feature on web first and extend the shells once it works there. *Rationale:* reuses the
Blazor UI via the RCL, not just the API; feature work deferred until it begins (re-check MAUI maturity then). Linux desktop out of scope; if required, tilt to Uno
Platform or Avalonia. The API being client-agnostic means worst case only the frontend is affected.

**ADR-C10 — Target latest STABLE release, never previews.**
Re-verify current stable versions at each project's start. *Rationale:* avoids building on
shifting preview ground; prefer LTS where it coincides with latest stable.

**ADR-C11 — Doc set + per-epic user stories methodology.**
Docs: PROJECT_BRIEF, FEATURES, DATA_MODEL, TECH_STACK, DECISIONS, WAYS_OF_WORKING, REBRANDING,
LOCALIZATION, MOBILE_TESTING, QA_TEST_PLAN, CLAUDE.md, plus per-epic stories under `docs/stories/`.
User stories generated per-epic at build time, not upfront. *Rationale:* lean, persistent context for solo +
Claude Code; stories stay grounded in real screens.

---

## App-specific decisions
<!-- Add ADR-001, ADR-002, … as decisions are made during conceptualization.
     Format: decision + rationale + date. -->

> _Begin numbering at ADR-001 for this app. Date each entry._

**ADR-C12 — Process conventions: vertical slices, per-epic Gherkin user stories, Conventional
Commits, standard PR template.**
Vertical end-to-end slices that keep the app working; stories in `docs/stories/` one file per epic
with Gherkin acceptance criteria; Conventional Commits for branches/commits/PR titles; PRs use
`.github/pull_request_template.md`. Full detail in `docs/WAYS_OF_WORKING.md`. *Rationale:* a
lightweight defined process keeps solo + Claude Code work consistent and mergeable.

**ADR-C13 — Local dev infrastructure via Docker Compose: PostgreSQL 17 + Mailpit. (2026-06-17)**
`docker-compose.yml` at repo root; configuration via `.env` (gitignored; copy from `.env.example`).
All **Compose service** ports (DB, Mailpit) are environment-variable-driven so multiple projects can
run simultaneously without conflicts (the API/Web app ports are fixed in their launch profiles). Both services expose healthchecks; API containers should declare `depends_on: db:
condition: service_healthy`. No pgAdmin in the platform — devs use their own DB client.
*Rationale:* PostgreSQL is always needed; Mailpit traps passwordless + invitation email in dev with
zero config; env-var ports prevent port clashes across projects.

**ADR-C14 — Testing: 100% TDD; unit tests (xUnit) + E2E (Playwright/NUnit). (2026-06-17)**
All production code is test-driven (red-green-refactor). Unit tests (xUnit) in `Core.Tests` and
`Api.Tests` cover domain logic, derived rules, and API behavior. E2E tests (Playwright 1.60,
NUnit) in `E2E.Tests` cover critical user flows through a real browser against the full running
stack; Page Object Model in `tests/E2E.Tests/Pages/`. Gherkin scenarios from user stories map
directly to test cases. No slice merges without passing tests. `playwright install` required once
after first build. *Rationale:* TDD forces clear interfaces and prevents regression; E2E tests
confirm real user flows end-to-end; together they give confidence to ship and refactor continuously.

**ADR-C15 — Auth strategy: OAuth (Google + Microsoft, extensible) + magic links (web) + OTP framework (mobile, deferred). (2026-06-17)**
External OAuth via ASP.NET Core's provider model — new providers added as a single `.AddXxx()`
call, no structural changes. Magic links use a custom `MagicLinkTokenProvider`
(`DataProtectorTokenProvider` subclass, 15-min default) for passwordless web sign-in; tokens are
Data Protection-backed, single-use, and server-bound. OTP infrastructure provided by
`AddDefaultTokenProviders()` — TOTP (authenticator app) and email OTP are ready; SMS OTP deferred
until mobile work begins. `TenantInvitation` entity added for household membership flow. OAuth
credentials stored in user-secrets (dev) / environment variables (prod) — never in appsettings.
Email sent via `IEmailSender` (Core abstraction) → `SmtpEmailSender` (MailKit, `Email:Smtp`
config); dev points to Mailpit. JWT Bearer auth intentionally omitted from this ADR — configured
in the auth story slice to keep it app-specific.
*Rationale:* provider-agnostic OAuth avoids re-architecting for new providers; magic links remove
password friction on web; OTP framework is in place without committing to an SMS provider;
abstracting `IEmailSender` keeps Core independent of sending infrastructure.
> **Superseded by ADR-002 (2026-06-19):** auth is a custom JWT + `LoginToken`/`PasswordlessService`
> stack, not Identity token providers / `MagicLinkTokenProvider` / `AddDefaultTokenProviders`. The
> TOTP/authenticator support described here was never implemented (email OTP is). Secrets moved to
> `.env` per ADR-001.

**ADR-001 — Local-dev secrets/config consolidated in `.env` (DotNetEnv); supersedes user-secrets. (2026-06-19)**
All local-dev secrets and config (`Jwt__Secret`, `Authentication__{Google,Microsoft}__*`,
`Email__Smtp__*`) live in the repo-root **`.env`** alongside the existing docker-compose vars —
one file. The API loads it at startup via **DotNetEnv** (`Env.TraversePath().Load()` before the
host builder; a no-op when absent, e.g. production). Keys use the .NET env-var form (`__` =
section nesting, so `Section__Sub` ≡ the `Section:Sub` config key) so they bind to the same config keys. `.env` stays gitignored; `.env.example`
(committed) documents every key with placeholders. **Production is unchanged** — the same keys
come from real environment variables, never a committed file. This **supersedes the
`dotnet user-secrets`** approach noted in ADR-C15.
*Rationale:* a single, visible local-config file was the explicit preference; `.env` already
existed for docker-compose, so the app secrets join it. Trade-off vs user-secrets: secrets now
sit in the working tree (mitigated by `.gitignore`) rather than the user profile — accepted for
this workflow.

**ADR-002 — Auth is a custom JWT + rotating-refresh-token stack, not ASP.NET Core Identity. (2026-06-19)**
Supersedes ADR-C8 and the Identity parts of ADR-C15. The platform implements its own auth on custom
`User` / `UserLogin` / `RefreshToken` / `LoginToken` entities: JWT access tokens (60 min) + rotating,
hashed refresh tokens (single-use, replay-protected); OAuth (Google + Microsoft) account-linking with
an unverified-email takeover guard; passwordless magic-link + email OTP via `PasswordlessService`
(hashed, single-use, time-limited `LoginToken`s). There is **no** `IdentityUser`, `UserManager`,
`MagicLinkTokenProvider`, or `AddDefaultTokenProviders`; JWT Bearer is configured in `Program.cs`.
*Rationale:* the Identity + cookie approach hit a persistent Blazor WASM client failure; the proven
JWT model (ported and hardened) was chosen over more Identity debugging, and it also gives native
(MAUI) clients clean body-token transport.

**ADR-003 — Tenancy is membership-based and enforced by a global query filter. (2026-06-19)**
A user's tenant lives in a **`TenantMembership`** join entity (unique on `UserId` — one tenant at a
time; `Role` owner/member), **not** a `tenant_id` column on `User`. Tenant-owned entities implement
`ITenantScoped`; `AppDbContext` applies a global EF query filter scoping them to the JWT's `tenant_id`
claim (fail-closed when absent). Genuinely cross-tenant / pre-auth lookups (invitation accept by token
hash) opt out with `IgnoreQueryFilters()`.
*Rationale:* membership models "a user moves between tenants" and the always-in-exactly-one-tenant
invariant cleanly; the global filter turns "never leak across tenants" (ADR-C2) from a per-query
convention into a structural guarantee, so feature slices can't forget to scope.

*Amendment (2026-06-22) — scoping is now structural on BOTH read and write.* The original query
filter scoped reads only; nothing stamped or validated `TenantId` on insert, so a slice that forgot
the stamp (or bound it from request input) could persist a row under the wrong tenant — and the read
filter would then hide that row from its true owner, an invisible data-integrity bug (audit CONF-1).
A **write-side `TenantStampingInterceptor`** (`src/Infrastructure/Persistence/`, wired via
`AppDbContext.OnConfiguring` so every context — including tests — enforces it) now closes that gap:
for each `Added` `ITenantScoped` entity *while a tenant is current*, an unset `TenantId` is stamped
with the current tenant, and a `TenantId` belonging to a **different** tenant **throws** (fail
closed). A context with **no** current tenant (`CurrentTenantId == Guid.Empty`) is a system/seed/
cross-tenant context and is not enforced — the same trust level that may bypass the read filter.
The audited cross-tenant **escape hatch** is named and greppable: `IRepository<T>.QueryAllTenants()`
for reads (replacing ad-hoc `Query().IgnoreQueryFilters()` in feature code; used by dissolve
`ITenantDataContributor`s), and `IgnoreQueryFilters()` on the platform's own teardown
(`TenantRepository.WipeDataAsync`, which targets its argument tenant regardless of who is current).
A build-time ban on `IgnoreQueryFilters` inside `src/Api/Features/**` is planned (audit B9-1) to make
the escape hatch unreachable from slice code.

*Amendment (2026-06-25) — `EnterTenant`: scope system/integration writes instead of bypassing scoping.*
The cross-tenant escape hatch (`QueryAllTenants()`) is right for rare **teardown** (tenant dissolve),
but a frequent, access-*granting* write that runs without a JWT — notably the **Stripe billing
webhook** (BILLING-3) — should not punch a permanent hole in tenant isolation. New primitive
**`ITenantContext.EnterTenant(tenantId)`** (implemented by `HttpCurrentTenant`, registered so one
scoped instance backs both `ICurrentTenant` and `ITenantContext`): after the caller has
**authenticated** the tenant id by other means (a verified webhook signature; an admin authz check),
it makes that tenant *current* for the scope, so the write-stamping interceptor and the global read
filter scope to it — the operation gets the **same structural isolation an authenticated request
gets**, no `IgnoreQueryFilters`. It *scopes*, it does not *authorize*; entering `Guid.Empty` is
rejected. Preferred over the escape hatch for any signature-/system-authenticated tenant-scoped write;
the escape hatch stays for genuine cross-tenant teardown/enumeration. Reused later by ADMIN
impersonation (`docs/PLATFORM_BACKLOG.md`).

**ADR-004 — Clean platform baseline + vertical-slice features (hybrid). (2026-06-19)**
The reusable **platform** stays clean-layered / horizontal — Core, Infrastructure, and the
auth/tenancy controllers (the durable chassis: JWT auth, membership tenancy, the global query
filter, email, persistence). App **features** are organized as **vertical slices**: one
self-contained folder per feature in `src/Api/Features/<Feature>/` — a minimal-API `MapGroup`, a
handler, co-located models, and an `ITenantDataContributor` — reusing the platform via
`IRepository<T>`, `ICurrentTenant`, and `IUnitOfWork`. Feature endpoints are minimal-API groups; the
platform stays controllers. The entity lives in `Core` and implements `ITenantScoped` so tenant
scoping is automatic. Full convention in `docs/WAYS_OF_WORKING.md`; reference slice at
`src/Api/Features/Notes` (marked DELETE-ME).
*Rationale:* the platform is cross-cutting, stable, and shared by every feature — it benefits from
clean layering. Features are independent and churn-y — co-locating each one's endpoint/handler/
models/data makes them easy to add, understand, and delete without touching central code. The
generic repository + global tenant filter let a slice be added without authoring a repository pair
or remembering to scope. This is the architectural convention for app work on top of the platform.

*Amendment (v2 audit, 2026-07-01, per v2 decision D7) — config-gated minimal-API PLATFORM surfaces are a
sanctioned exception; "zero central edits" is really a ~5-touchpoint slice contract.* Two clarifications
to reconcile this ADR with what shipped:
1. **Platform is not *exclusively* controllers.** PUBAPI (ADR-015) and HOOKS (ADR-016) are horizontal
   **platform** capabilities but ship as **minimal-API groups** (`ApiKeyEndpoints`, `WebhookEndpoints`),
   not controllers, because their routes must be **conditionally mapped** behind a config gate
   (`PublicApi:Enabled` / `Webhooks:Enabled`) — off ⇒ the routes don't exist (404), which minimal-API
   conditional mapping expresses cleanly. So the rule is: platform HTTP is controllers **by default**,
   with **config-gated minimal-API groups as an explicit exception** for surfaces that must appear/vanish
   by configuration. Downstream *app* vertical features remain `src/Api/Features/<X>/` slices.
2. **"Without touching central code" is a bounded contract, not literally zero edits.** Adding a slice
   still touches a small, fixed set of central seams — roughly: register the `DbSet`/config, add a
   migration, wire DI (`Add*`) + map the group in `Program.cs`, register the `ITenantDataContributor`,
   and reset the table in the test fixture (the "add-a-slice checklist" in `docs/WAYS_OF_WORKING.md`).
   The point stands — you never edit a central *wipe/has-data/export* method or author a repository pair
   — but it's ~5 mechanical touchpoints, not none.

*Amendment (v2 audit B9-6 / DEBT-6, 2026-07-02, per v2 decision D7) — the config-gated platform surfaces
now live in a distinct namespace/folder, separated from vertical-slice features.* The prior amendment
established that PUBAPI/HOOKS are **platform** (not app features); this refines *where* they live so the
distinction is structural, not just narrative:
1. **Config-gated minimal-API PLATFORM surfaces live under `src/Api/Endpoints/`** (namespace
   `Perezosoft.Api.Endpoints`), NOT `src/Api/Features/`. `ApiKeyEndpoints` (PUBAPI) and `WebhookEndpoints`
   (HOOKS) moved there. They may use a raw `MapGroup(...)` because they are platform surfaces, not slices.
2. **The shared endpoint-extension helpers are platform infra and live with Endpoints.**
   `MapTenantFeatureGroup` (`FeatureEndpointExtensions`), `RequirePermission` (`PermissionEndpointExtensions`),
   and `RequireEntitlement` (`EntitlementEndpointExtensions`) moved from `Perezosoft.Api.Features` to
   `Perezosoft.Api.Endpoints`. This is what lets the R8 gate hold: nothing outside `src/Api/Features/` (except
   `Program.cs`, which composes the Notes sample) references `Perezosoft.Api.Features.*`.
3. **Vertical-slice features stay under `src/Api/Features/<X>/`** and register their routes via
   `MapTenantFeatureGroup` — never a raw `MapGroup`. A new build gate (R6,
   `FeatureFiles_RegisterRoutesViaMapTenantFeatureGroup_NotRawMapGroup`) scans `src/Api/Features/**` and
   fails on any raw `.MapGroup(` there, so a future slice can't quietly bypass the shared tenant-API auth.
   This is a pure move + namespace change — no route, behavior, or signature changed.

*Amendment (v3 audit Phase 4, 2026-07-27, T58) — the touchpoint contract is verified-adversarially and
gains one member.* Phase 4 built a real entity-bearing slice against this ADR and measured the central
edits: the "~5 mechanical touchpoints" list is accurate **plus one the list omitted — the RLS policy**.
`dotnet ef migrations add` scaffolds no RLS DDL, so an `ITenantScoped` entity's policy must be appended
to the same migration by hand (ADR-020; step 4 of the add-a-slice checklist in `WAYS_OF_WORKING.md`,
enforced by the `RlsMigrationGateTests` parity gate). Read "without touching central code" as the
bounded ~6-touchpoint contract above — never as literally zero; the durable half of the claim is what
Phase 4 confirmed HOLDS: the EF filter, stamping interceptor, RLS backstop, and the group auth policy
are all inherited with no slice re-implementation.

**ADR-005 — Apple Sign In fits the agnostic provider model; implementation DEFERRED, web-first. (2026-06-24)**
A third OAuth provider (Apple) was assessed against the provider-agnostic auth stack (ADR-002). The
verdict: the **backend absorbs it with small, mechanical additions** — `.AddApple(...)` in
`ServiceCollectionExtensions`, an `Apple` arm in `AuthProviders` (const + `Supported` +
`SchemeFor`), an `Apple => true` arm in `ProviderEmailTrust` (Apple asserts `email_verified`,
private-relay addresses included), a button/glyph in `Login.razor` + `Settings.razor`, and config
keys; `ClaimsExtractor` needs **no** change (`sub`→`NameIdentifier`, `email`→`ClaimTypes.Email`
already map). The fail-closed takeover guard and tenant scoping require no structural change.
However, Apple is **NOT** the single-`.AddXxx()` that Google/Microsoft are (so the CLAUDE.md / ADR-C15
"new provider = one line" claim has a documented exception). The Apple-specific costs are recorded
here so they are not rediscovered later:
1. **No built-in handler** — ASP.NET Core ships Google + MicrosoftAccount but not Apple. Needs the
   community `AspNet.Security.OAuth.Apple` (aspnet-contrib) package; confirm a **stable** .NET 10
   build exists before adopting (no previews — ADR-C10).
2. **The "client secret" is a rotating ES256 JWT**, minted from a downloaded `.p8` key + Team ID +
   Key ID + Service ID, expiring every ≤6 months. This breaks the single-static-secret-in-`.env`
   shape of ADR-001 (the package can generate/cache the JWT from the key material).
3. **Apple forbids `localhost` redirect URIs.** Google/MS redirect to `https://localhost:7160` /
   `http://localhost:5238`, which the QA plan and `MOBILE_TESTING.md` rely on. Apple needs a real
   **HTTPS domain or tunnel** even for local QA — a workflow asterisk, not a code change.
4. **`form_post` callback** (because name/email scope is requested) ⇒ the OAuth correlation cookie
   must be `SameSite=None; Secure`; relevant given the schemeful-same-site cookie history.
5. **Display name is returned only on the first authorization** — `ExtractDisplayName` gets `null`
   thereafter (tolerated; capture on first auth if wanted).
6. **Native (MAUI desktop/Android) Apple is a separate, larger effort** — no native Apple SDK on
   Win/Android, so it reuses the web flow and inherits #3/#4. Web-first per ADR-C9.
Decision: keep the design open to Apple; implement **web-first** as the slice in
`docs/stories/apple-signin.md` when a business need arises; defer until then. External prerequisite:
**Apple Developer Program enrollment ($99/yr)** + portal setup (App ID, Service ID, Sign-in key).
*Rationale:* the architecture doesn't fight a third provider — the cost is Apple's protocol and
account setup, not our code. Recording the constraints now prevents re-scoping later and stops "it's
just one line" from being assumed for Apple.

**ADR-006 — Billing & subscriptions: provider-abstracted (`IBillingProvider`), Stripe reference impl, plan-tier entitlements + quotas. Implementation DEFERRED, web-first. (2026-06-25)**
Monetization enters through a Core abstraction **`IBillingProvider`** (same shape as `IEmailSender`):
a **Stripe** reference implementation in Infrastructure plus an in-memory **`FakeBillingProvider`**
for tests. A tenant has at most one **`Subscription`** (`ITenantScoped`) holding plan tier, status,
and Stripe customer/subscription ids; access is gated by an **`IEntitlementService`** (feature flags
keyed to plan) and an **`IQuotaService`** (countable limits — seats, metered usage). The **plan
catalog is code/config, not tenant data.** Stripe is the **system of record for money**; our DB holds
a **projection** kept current by **webhooks processed idempotently through the inbox** (ADR-007) —
we never treat our own DB as the truth for billing state.
Constraints recorded:
1. **Webhooks are at-least-once and out-of-order** — the handler verifies the Stripe signature,
   dedupes by event id, and is reentrant. This is the reliable-consumer problem ADR-007's **inbox**
   solves, so **BILLING depends on JOBS** (the outbox/inbox slice) landing first.
2. **Entitlement checks are server-side and fail-closed** — no/expired/`past_due` subscription ⇒
   Free tier; never trust the client.
3. **No card data, minimal PCI scope** — money mutations happen on **Stripe Checkout + Customer
   Portal** (redirects); we build no card forms and store no PANs (SAQ-A).
4. **Access is granted on the webhook, not the Checkout redirect** — returning from Checkout does
   not flip the tenant to paid; the `subscription.created/updated` event does.
5. **Quotas ≠ rate limits** — the existing `RateLimiting.cs` is per-IP request throttling (abuse);
   plan quotas are per-tenant **persisted** counters (seats = membership count; usage = a counter).
   Different mechanism — don't conflate.
6. `Subscription` participates in tenant **dissolve** via an `ITenantDataContributor` that cancels
   the Stripe subscription and wipes the projection.
7. **Sandbox/fake test stack (the answer to "can we test billing without real money": yes):**
   `FakeBillingProvider` for unit; **stripe-mock** (Stripe's official offline mock server) for
   `Api.Tests` request/response; **Stripe test mode + Stripe CLI** (`stripe listen`/`stripe trigger`)
   for webhook E2E; **Stripe Test Clocks** to simulate trial-end/renewal/dunning deterministically.
   This is the Mailpit-for-billing analogue (ADR-C13: trap it locally, zero real charges).
*Rationale:* billing is what makes this a SaaS platform rather than a multi-tenant CRUD app;
abstracting the provider keeps Core clean and the test suite offline; projecting Stripe state (rather
than owning money truth) avoids reconciliation bugs; deferring matches the web-first/business-need
posture (ADR-C9) — there is no app or plan catalog yet (`PROJECT_BRIEF.md` is still TODO).
Stories + slice plan: `docs/stories/billing.md` (epic `BILLING`). Future siblings parked in
`docs/PLATFORM_BACKLOG.md`.

*Amendment (2026-06-25) — billing HTTP surface is a PLATFORM controller, not a feature slice.* The
BILLING-1/2 slice plan said "`Features/Billing` slice (`MapTenantFeatureGroup`)", but **billing is
horizontal platform/chassis** (reusable by every app), and per ADR-004 the platform's HTTP surface is
**controllers**, while `src/Api/Features/<X>/` minimal-API slices are reserved for the *downstream
app's* vertical features. BILLING-2 initially (and wrongly) shipped `/api/billing` as a
`Features/Billing` slice with a hand-rolled owner check; it is now a **`BillingController :
TenantApiControllerBase`** (`src/Api/Controllers/`) next to the household controllers, reusing the
base's `GetMembershipAsync`/`IsOwner`/`Forbid403` gate, with the checkout orchestration in
`IBillingService` (`src/Api/Services/`). The provider/entitlement/catalog/`Subscription` pieces were
already platform and are unchanged; `.RequireEntitlement(...)` stays as `Features/`-root scaffolding
(like `MapTenantFeatureGroup`) that the downstream app's slices call. BILLING-3's webhook lands as a
controller action too. (The only vertical slice in the platform remains the `Notes` 🗑️ DELETE-ME
sample.)

*Addendum (BILLING-5, 2026-07-01) — quotas implemented (mechanism-first, policy as data).* Decision
point on quotas is now built: **`IQuotaService`** (`src/Api/Services/QuotaService.cs`) resolves the plan
exactly like `EntitlementService` (fail-closed to Free) and enforces two limit kinds. **Seats** =
tenant members + **pending invites** (a pending invite reserves a seat, so N invites can't over-provision
past the cap) vs `Plan.SeatLimit`; checked in `TenantInvitationService.CreateAsync` for new invites →
**402 `seat_limit_reached`**. **Metered usage** = `TryConsumeAsync(key)` against a **monthly**
`UsageCounter` (`{tenant, key, yyyy-MM}`) — the calendar-month period key makes it **self-resetting with
no sweep job** (simpler than the point-5 "usage counter + JOBS-3 reset" sketch). Limits live in
`PlanCatalog` as **data**: a `null`/absent limit means unlimited, so the mechanism ships **inert** until a
plan sets a number; the platform ships example numbers (Free 3/3, Pro 10/100) to demonstrate. Confirms
point 5 (quotas ≠ rate limits): these are per-tenant persisted counters, not the per-IP throttle.

*Addendum (BILLING-6, 2026-07-01) — trial/dunning lifecycle (owner-facing reaction, mechanism-first).*
The projection already reflects Stripe's lifecycle (BILLING-3), and entitlements already fail closed on a
lapsed period — so BILLING-6 adds the **reaction**, not new state. **`IBillingNotifier`** notifies the
tenant **owner** through the notification center (NOTIFY: in-app row + outbox email per prefs). The
**webhook handler** compares the pre-event status and, on a **transition into `past_due` or `canceled`**,
notifies once (same-status redeliveries don't re-notify; the inbox dedups by event id). A
**`SubscriptionLapseSweepJob`** (`IScheduledJob`, ADR-007) scans all tenants (`QueryAllTenants`) for
active/trialing subscriptions whose `CurrentPeriodEnd` has passed, sends a one-time "expired" nudge, and
records `Subscription.LapseNotifiedAt` so it fires once per lapse — **without fabricating a status**
(Stripe stays the money-truth; a later webhook corrects the projection). Deliberately **not** built:
Stripe's own retry schedule / card-failure emails (**Smart Retries** owns that), and the advance
"trial-ends-in-N-days" nudge (a small follow-up — needs a Stripe `trial_will_end` event kind).

*Addendum (BILLING-7, 2026-07-01) — billing participates in tenant dissolve (point 6 delivered).* The
design (point 6) always said the `Subscription` should be torn down on dissolve; it's now built.
**`BillingDataContributor : ITenantDataContributor`** wipes the tenant's `Subscription` projection and —
if it has a live provider subscription — **cancels it at the provider**, so a dissolved tenant stops being
billed (the "delete account → Stripe keeps charging" bug). The cancel is **not** an external call inside
the dissolve transaction: it's a `"billing.cancel"` **outbox** message (staged with the teardown, then run
out-of-band with retry by `BillingCancelOutboxHandler` → new idempotent `IBillingProvider.CancelSubscriptionAsync`).
`HasDataAsync` returns **false** — a subscription is billing plumbing, not tenant content, so it never trips
the "would abandon data" guard; it's cleaned up automatically instead. Export (GDPR-1) gains a `billing`
section (plan/status/period — never Stripe ids or card data). **This closes the BILLING epic (1–7).**

*Addendum (BILLING-8, 2026-07-03) — the billing page.* The chassis finally gets its owner-facing UI
(dunning notifications had deep-linked to `/billing` since BILLING-6): **`GET /api/billing`** (owner-only
`ManageBilling`) returns the plan (resolved **fail-closed** exactly like entitlements), raw status,
period end, seat usage vs the plan limit, and `has_subscription` (gates the portal button); a
**`Billing.razor`** page renders it with Upgrade (checkout redirect) and Manage (portal redirect)
actions, plus an owner-only notice for members. Nothing about the money rules changed: access is still
granted only by the webhook, the page just *shows* the projection. The E2E journey proves the whole
loop without Stripe via the `FakeBillingProvider` (stubbed checkout URL + a webhook POSTed exactly as
Stripe would send it, through the real verify/inbox/EnterTenant/projection path).

*Amendment (v2 audit GAP-1, 2026-07-01) — the fake provider is Development-only; production without a key fails fast.* The
original wiring registered `FakeBillingProvider` whenever `Billing:Stripe:SecretKey` was absent — including in
production. Because the fake **trusts a literal webhook signature** (`Stripe-Signature: valid`) and the webhook
endpoint is anonymous and always mapped, a production deploy that hadn't yet configured Stripe would accept
**forged, unauthenticated cross-tenant subscription writes** (an attacker could grant/rewrite any tenant's plan).
`AddInfrastructure` now takes `IHostEnvironment` and registers the fake **only when `environment.IsDevelopment()`**;
outside Development with no key it **throws at startup** (the app cannot boot with the fake). The webhook controller
also **logs a warning with the source IP** on a rejected signature (GAP-5), so a forged-webhook probe is observable
rather than a silent 400. Consequence: a **production/staging deploy MUST configure a real `Billing__Stripe__SecretKey`**
(it no longer silently falls back to the fake). Dev/E2E are unchanged. Tests: `BillingProviderRegistrationTests`,
`BillingWebhookControllerTests`.

*Amendment (v2 audit, 2026-07-01) — the stripe-mock request/response test stack (point 7) was deferred.*
The test stack as built is `FakeBillingProvider` (unit) + Stripe test-mode/CLI for E2E; **stripe-mock**
(Stripe's official offline mock server, sketched in point 7 for `Api.Tests` request/response coverage) is
**not** in the test stack — it was deferred in BILLING-2 over Testcontainers friction (see the note in
`docs/ROADMAP.md` under "Test & hardening debt"). The rest of point 7 holds.

*Addendum (BILLING-9, 2026-07-14) — the seat quota is re-checked when an invitation is accepted.*
BILLING-5 enforced seats only at invitation **creation** (pending invites reserve seats), which holds
while the plan is stable — but nothing sweeps pending invites on a **downgrade** (dunning lapse,
cancellation, or an ADR-021 comp revert), and `AcceptAsync` never re-checked, so invites issued on a
bigger plan could each still join and actively grow the tenant past its new cap (Pro→invite 7→Free
left a 3-seat tenant able to reach 8 members). `AcceptAsync` now refuses when the tenant is **already
over its limit** (`CanAdd(0)` — the accept itself is seat-neutral because the joiner consumes the seat
their invite reserved, so accepts at exactly the cap stay allowed), returning 402 `seat_limit_reached`
(same shape as the create-path gate) rendered on `/join` as a "household is full" state. The check runs
inside `EnterTenant(invitation.TenantId)` (the quota must count the invitation's tenant, not the
caller's old one — same trusted contract as the accept's conditional flip). Deliberately NOT done:
sweeping/revoking pending invites on downgrade (destroys owner-created state; the refused token stays
pending and **self-heals** when the tenant upgrades again) and evicting members (over-cap tenants are
frozen for growth, never shrunk). Tests: `AcceptSeatQuotaTests` (over-cap / at-cap / self-heal) +
the E2E webhook-downgrade journey in `SeatQuotaJourneyTests`.

**ADR-007 — Reliable async work: transactional outbox + inbox + background dispatcher + scheduled jobs. Implementation DEFERRED. (2026-06-25)**
Side effects that must not be lost (email, billing webhooks, future integrations) move off the
request thread through a **transactional outbox**: an **`OutboxMessage`** is written in the **same EF
`SaveChanges`** as the business change (via `IUnitOfWork`), so the effect is atomic with the data —
no "saved the row but lost the email," no "charged but didn't provision." A **`BackgroundService`**
(`OutboxDispatcher`) polls unsent rows (claiming with Postgres `FOR UPDATE SKIP LOCKED`), dispatches
via typed handlers, and retries with backoff into a **dead-letter** state. The **inbox** is its
mirror — same table family with a `direction` discriminator, keyed by an external idempotency id
(e.g. Stripe event id) — giving exactly-once **inbound** processing. **Scheduled/recurring** work
(trial-expiry sweeps, dunning nudges, expired-token cleanup, quota resets) runs via a lightweight
timer hosted service.
Constraints recorded:
1. **In-process on Postgres, no broker** — keeps the platform's run cost "Postgres only" (ADR-C13).
   A distributed scheduler (Hangfire/Quartz) or message broker is a documented **swap-in** when
   multi-node arrives, not a dependency now; the `SKIP LOCKED` claim design keeps a single-table
   approach correct even multi-instance.
2. **`OutboxMessage` is NOT `ITenantScoped`** — it's platform infra and may carry system (non-tenant)
   effects; it stores an optional `TenantId` for handler context but is outside the global filter.
   On dissolve, pending tenant-related outbox rows are drained/cancelled by the relevant contributor.
3. **At-least-once delivery ⇒ all handlers must be idempotent** — the same contract billing webhooks
   need (ADR-006).
4. **First consumer is the existing email path** — passwordless and invitation sends currently call
   `IEmailSender` **inline in the request**; the first slice migrates them to enqueue-to-outbox (the
   SMTP send moves into a handler), proving the path on existing, already-tested behavior.
*Rationale:* the platform already sends email inline during request handling, so a transient SMTP
failure becomes a request error or a silently lost message. A generic outbox makes every side effect
reliable once, and gives billing webhooks a correct idempotent home. In-process keeps infrastructure
minimal until scale actually forces a broker.
Stories + slice plan: `docs/stories/async-jobs.md` (epic `JOBS`).

*Amendment (2026-06-25) — JOBS-1 + JOBS-2 implemented; inbox is a separate dedup ledger, not a
`direction` column.* JOBS-1 shipped the outbox + `OutboxDispatcher` + the email migration as described.
JOBS-2 shipped the **inbox**, but as a **purpose-built `InboxMessage` ledger** (`Id`, `Source`,
`IdempotencyKey`, `ReceivedAt`; unique on `(Source, IdempotencyKey)`) rather than the originally-sketched
"same table + `direction` discriminator." Reasons: (a) inbox rows need none of the outbox's
queue columns (`Type`/`Payload`/`Status`/`AttemptCount`/`NextAttemptAt`), so a shared table would be
half-null; (b) dedup is a **unique-key concern**, so `IInbox.TryClaimAsync` uses
`INSERT … ON CONFLICT DO NOTHING` against the unique index — **race-free by construction** (concurrent
claims of one key serialise on the index; exactly one wins), which is cleaner here than the outbox's
`SKIP LOCKED` queue-claim (that pattern is for *picking work off a queue*, not deduping). The claim runs
on the shared `AppDbContext`, so it enlists in the caller's transaction: claim + guarded work commit
together (or roll back together, freeing the key for the inevitable redelivery). BILLING-3 consumes
`IInbox` for webhook idempotency.

*Amendment (2026-06-25) — JOBS-3 implemented; epic COMPLETE.* The scheduled-jobs host
(`ScheduledJobsHost : BackgroundService`) runs registered `IScheduledJob`s on per-job intervals with
failure isolation (one job's throw never stops the others or the host) and a fresh DI scope per run;
the reference `ExpiredTokenCleanupJob` deletes expired login/refresh tokens hourly. In-process,
single-instance per the baseline; Hangfire/Quartz remains the documented multi-node swap-in. The JOBS
epic (outbox, inbox, scheduler) is now done — **BILLING is unblocked.**

**ADR-008 — Observability (structured logging + OpenTelemetry + health checks) and a tenant-scoped audit log. Implementation DEFERRED. (2026-06-25)**
Two complementary concerns shipped as one slice group.
**(a) Operational observability** — structured (JSON) logging with per-request scopes enriched with
`tenant_id`/`user_id` (from the JWT claim via `HttpCurrentTenant`); **OpenTelemetry** traces +
metrics (ASP.NET Core + EF Core + HttpClient instrumentation) with the request span tagged by
tenant/user; and `/health` (liveness) + `/health/ready` (readiness — DB reachable) endpoints. The
OTLP exporter is **config-gated** (console in dev, OTLP when an endpoint is configured — same
config-presence pattern as the OAuth providers), so the platform runs with **no external telemetry
dependency** by default.
**(b) Audit log** — an append-only, tenant-scoped **`AuditEvent`** (`actor_user_id`, `action`,
`entity_type`, `entity_id`, `metadata` jsonb, `created_at`) for security/compliance-relevant actions
(member invited/removed, role changed, subscription changed, tenant dissolved). Written via an EF
`SaveChanges` interceptor (sibling of `TenantStampingInterceptor`) for declarative cases plus an
explicit `IAuditLog.Record(...)` for semantic events; **append-only** (no update/delete from app
code); `ITenantScoped` so it's auto-filtered per tenant and participates in dissolve.
Constraints recorded:
1. **Audit ≠ logs** — audit is durable, queryable, exportable **tenant data** (compliance); logs and
   traces are operational telemetry (sampled, ephemeral). Neither substitutes for the other.
2. **No secrets/PII in spans or audit metadata** — identifiers only; never tokens or card data.
3. **Health endpoints are unauthenticated and status-only** — must not leak internals.
4. **Dissolve vs retention tension** — wiping a tenant deletes its audit trail; if legal-hold/
   retention is required, the dissolve contributor must **export-then-wipe** (flagged for the
   GDPR/Account-Lifecycle backlog item).
*Rationale:* nothing in the platform currently emits structured telemetry, a health endpoint, or an
audit trail — every downstream app would re-invent all three. Adding them once at the platform layer
means every feature inherits them, and audit slots naturally onto the existing interceptor +
tenant-scoping machinery (ADR-003 amendment).
Stories + slice plan: `docs/stories/observability.md` (epic `OBS`).

*Amendment (v2 audit, 2026-07-01) — (b) the declarative SaveChanges audit-writer was deferred; audit
writes are explicit only.* Decision point (b) above sketched an EF `SaveChanges` interceptor that would
write audit rows declaratively *plus* an explicit `IAuditLog.RecordAsync` for semantic events. As
shipped, only the **explicit `IAuditLog.RecordAsync`** path exists — audit events are always written
deliberately at the call site (member invited/removed, role changed, subscription changed, tenant
dissolved, admin access). The `AuditAppendOnlyInterceptor` is present but **only GUARDS** append-only
(it throws on any tracked update/delete of an `AuditEvent`); it does **not** author audit rows. A
declarative auto-audit-on-SaveChanges interceptor remains an optional future add (also noted in
`docs/ROADMAP.md`).

**ADR-009 — RBAC: a third `admin` role + a permission seam (capability checks, not role checks). (2026-06-30)**
The platform shipped with exactly two tenant roles — `owner` and `member` — enforced by `IsOwner(...)`
boolean checks copied across every tenant controller (`HouseholdController`,
`HouseholdInvitationsController`, `BillingController`). B2B tenants delegate administration almost
immediately, and a copied `role == "owner"` test is both too coarse (no middle tier) and too brittle
(scattered, easy to drift). This ADR adds an `admin` tier **and**, more importantly, a **permission
seam** so call sites ask *"can the caller do X?"* instead of *"is the caller the owner?"*.

**Decision:**
1. **Roles are ordered: `owner` > `admin` > `member`.** `admin` is a new `TenantRoles` constant; the
   "exactly one owner" invariant (ADR-003) is unchanged — owner is conferred only via
   `TransferOwnershipAsync`, never via a role-change endpoint.
2. **A `Permission` enum + a static role→permission matrix** (`RolePermissions`) in **Core** is the
   single source of truth for "what can this role do". Permissions are coarse capabilities
   (`ViewTenant`, `RenameTenant`, `ManageMembers`, `ManageRoles`, `ManageBilling`,
   `TransferOwnership`, `DissolveTenant`), **not** per-entity ACLs. The matrix:

   | Permission | owner | admin | member |
   |---|:--:|:--:|:--:|
   | `ViewTenant` | ✅ | ✅ | ✅ |
   | `RenameTenant` | ✅ | ✅ | ❌ |
   | `ManageMembers` | ✅ | ✅ | ❌ |
   | `ManageRoles` | ✅ | ❌ | ❌ |
   | `ManageBilling` | ✅ | ❌ | ❌ |
   | `TransferOwnership` / `DissolveTenant` | ✅ | ❌ | ❌ |

   **Owner-only by deliberate choice:** billing is **financial** and role/ownership changes are the
   **privilege-escalation surface** — keeping both owner-only stops an admin from minting more admins
   or touching money. Apps that want a different posture edit one matrix, not N call sites.
3. **Two enforcement mechanisms mirror the two API styles (ADR-004):** controllers get a
   `RequirePermission(membership, Permission.X)` helper on `TenantApiControllerBase` (returns the
   standard 403 envelope); feature minimal-API groups get a `.RequirePermission(Permission.X)`
   endpoint filter that mirrors `.RequireEntitlement(...)` (ADR-006) but yields **403 Forbidden**
   (authorization), not 402 (payment). The existing `IsOwner` checks are refactored onto
   `RequirePermission` so there is one enforcement path.
4. **Role is read live from membership, never from the JWT.** A role change takes effect on the
   caller's next request with **no token refresh** — the access token carries `tenant_id`, not the
   role (status quo, made explicit here). This is why the seam is a runtime DB-backed check, not a
   claims policy.
5. **Role changes are audited** (ADR-008) — promote/demote records an `AuditEvent` with the actor,
   target, and old→new role.

**Constraints recorded:**
1. **Exactly one owner, always** — the role-change endpoint moves users only between `admin` and
   `member`; it can never set or clear `owner` (that path stays `TransferOwnershipAsync`), and it can
   never target the owner.
2. **No self-escalation / no lockout** — a caller cannot change their own role; an admin cannot act
   on the owner.
3. **Permissions are coarse capabilities, not resource ACLs** — fine-grained per-record sharing is a
   different (deferred) concern; don't grow this into an ACL system without a new ADR.
4. **The matrix is the only place roles map to capabilities** — no new scattered `role == "admin"`
   checks; add a `Permission` and a matrix row instead.
*Rationale:* every downstream B2B app needs an admin tier and will otherwise re-invent role checks ad
hoc. Centralizing the capability mapping once, behind a seam the existing entitlement-filter pattern
already established, makes the common case (add a permission, gate an endpoint) a one-liner and keeps
the owner-only blast-radius items explicit. Pairs with the `ADMIN` (back-office/impersonation) and
`PUBAPI` backlog items, which build on this seam.
Stories + slice plan: `docs/stories/rbac.md` (epic `RBAC`).

---

**ADR-010 — File/blob storage: `IFileStorage` abstraction, local-disk dev default, config-gated S3-compatible prod impl; tenant-scoped keys; signed time-limited download URLs. (2026-06-30)**
The platform has no way to store binary content. Avatars, attachments, and the GDPR data-export
artifact (backlog) all block on it, and every downstream app would otherwise re-invent file handling
(and likely leak files across tenants). This adds one storage seam, mirroring the `IEmailSender` →
`SmtpEmailSender` shape (Core abstraction + Infrastructure impl, registered by config presence).

**Decision:**
1. **`IFileStorage` (Core) is the only way to store/retrieve blobs** — `PutAsync`/`GetAsync`/
   `DeleteAsync`/`ExistsAsync` + `GetDownloadUrlAsync`. It **streams, never buffers** whole files
   (bound memory; large uploads/downloads). Features depend on this abstraction, never on a cloud SDK
   or `System.IO` directly — the same rule as "never reference MailKit outside `Infrastructure/Email/`".
2. **Keys are tenant-scoped and enforced server-side.** Every object key is namespaced `{tenantId}/…`
   from `ICurrentTenant`; the storage layer **rejects** keys that escape the tenant prefix or contain
   traversal (`..`, absolute/rooted paths, alternate separators). This is the blob equivalent of the
   `ITenantScoped` global query filter (ADR-003): isolation is structural, not by-convention, and the
   client path is never trusted. No `ICurrentTenant` (system context) ⇒ fail closed.
3. **Two implementations, config-gated like the billing provider (ADR-006).** `LocalDiskFileStorage`
   (root dir from config) is the **dev/test default** so the app boots and the suite runs with **zero
   cloud setup**; an **S3-compatible** impl (`S3FileStorage`, AWS SDK — works with AWS S3, MinIO,
   Cloudflare R2, DO Spaces) is selected when `Storage:S3:*` is configured, else local. Same
   config-presence switch as Stripe-vs-Fake.
4. **Signed, time-limited download URLs — never proxy bytes through the API for the common case.**
   Cloud returns a **native presigned GET URL**. Local disk can't presign, so a **platform endpoint**
   `GET /api/files/{token}` verifies a short-lived token minted with `ITimeLimitedDataProtector` (the
   Data Protection stack is already wired, keys persisted to the DB) and streams the file
   tenant-checked. `GetDownloadUrlAsync` returns the right URL per impl — a **uniform contract** so
   feature code never branches on the backend.
5. **Uploads flow through `IFileStorage.PutAsync` from feature services.** The platform ships the
   abstraction + both impls + the download surface; it does **not** prescribe what gets stored or wire
   an upload endpoint to a specific entity (that's a vertical/app concern — horizontal-only platform).

**Constraints recorded:**
1. **Tenant isolation is structural** — keys carry the tenant; the layer refuses cross-tenant or
   traversal keys. A feature passes a logical key; the layer prepends/validates the tenant prefix.
2. **Stream, don't buffer** — `PutAsync`/`GetAsync` take/return streams; never read a whole file into
   memory.
3. **Signed URLs are short-lived and scoped to one key** — never a directory/prefix/wildcard; the
   token encodes key + expiry, signed, opaque.
4. **No content sniffing / AV scanning / image processing** here — out of scope; a downstream concern.
   The declared content-type is stored and served back.
5. **Deletion is best-effort idempotent** — deleting a missing key is not an error (parity with cloud
   semantics).
*Rationale:* one storage seam at the platform layer means avatars, attachments, exports, etc. all get
tenant-safe, backend-agnostic file handling for free, and swapping local→S3 is a config change, not a
code change — exactly the property the email and billing seams already give. The signed-URL contract
keeps large transfers off the API process while staying uniform across dev and prod.
Stories + slice plan: `docs/stories/files.md` (epic `FILES`).

---

**ADR-011 — Account & data lifecycle (GDPR): tenant data export + account erasure, built on the existing contributor + dissolve machinery. (2026-06-30)**
Once the platform has EU users it needs **data portability** ("download my data") and **erasure**
("right to be forgotten") — legal requirements with real penalties, and a credible trust feature. The
platform already has most of the machinery: the `ITenantDataContributor` seam
(`HasDataAsync`/`WipeAsync`) that each feature registers, the transactional **dissolve** flow, the
**audit log** (ADR-008), and now **file storage** (ADR-010) for the export artifact. GDPR is assembled
from these rather than invented.

**Decision:**
1. **Export mirrors wipe — one more contributor method.** `ITenantDataContributor` gains
   `ExportAsync(tenantId)` (+ an `ExportKey` section name) alongside `HasDataAsync`/`WipeAsync`, so each
   feature contributes its data to a tenant export **the same way** it contributes to teardown — adding
   a feature never means editing a central exporter. A platform `TenantExportService` assembles the
   **core** tenant data (tenant, memberships + member emails, pending invitations) plus every
   contributor's section into one JSON bundle.
2. **The export artifact is a stored file with a signed URL (ADR-010).** The bundle is written via
   `IFileStorage` under a tenant-scoped key and handed back as a **signed, time-limited download URL** —
   never streamed inline, never a permanent link. Owner-only (a new `Permission.ExportData`), audited.
3. **Erasure has two granularities.** **Tenant erasure** is the existing **dissolve** (leave-with-confirm
   → contributors wipe + core teardown) — GDPR adds the **export-then-wipe** option so a tenant can take
   its data before deletion. **User (account) erasure** — "delete my account" — removes the user's
   **identity/PII** (`User`, `UserLogin`, `LoginToken`, `RefreshToken`) but **not** tenant app data
   (that belongs to the tenant, not the user).
4. **Account erasure honors the single-owner invariant (ADR-003).** A sole owner of a tenant with other
   members must **transfer ownership first**; a solo owner's tenant is **dissolved** as part of erasure
   (its data wiped via the contributors); a plain member is simply removed (**not** re-homed — the
   account is going away, unlike leave). Then the identity rows are deleted. The whole operation is one
   transaction and is **audited**.
5. **Erasure vs. audit/legal-hold tension is resolved explicitly.** The audit trail keeps **actor ids,
   never PII**, so erasing a user leaves audit events intact (an id that no longer resolves to a person)
   rather than deleting the compliance record. Where a regulatory **legal hold** requires retaining more,
   the contributor path supports **export-then-wipe**; retention windows are a deployment policy, not
   hard-coded.

**Constraints recorded:**
1. **Export is tenant-scoped and owner-gated** — it contains a whole tenant's data; only the owner may
   request it, and it comes back as a signed URL, not inline bytes.
2. **Export contains identifiers + content, never secrets** — no password/OTP/token hashes, no card
   data, no session tokens; the same rule as audit metadata.
3. **Single-owner invariant is never violated by erasure** — transfer-or-dissolve first; erasure can't
   strand a tenant ownerless.
4. **User app data stays with the tenant** — erasing a user removes their identity, not the
   tenant-scoped records they created (those are the tenant's, and are removed only by tenant dissolve).
5. **Audit survives user erasure** — actor ids remain; audit is not a place PII lives.
*Rationale:* every SaaS with EU users hits this, and re-implementing export/erasure per app is both
wasteful and risky (the failure mode is a cross-tenant data leak or an orphaned tenant). Building both
on the contributor seam + dissolve + file storage keeps the common case a one-liner per feature (add an
`ExportAsync`) and keeps the dangerous invariants (single-owner, tenant isolation, no-secrets) in one
audited place. Depends on: audit (ADR-008, ✅), file storage (ADR-010, ✅), the dissolve flow, and the
permission seam (ADR-009).
Stories + slice plan: `docs/stories/gdpr.md` (epic `GDPR`).

---

**ADR-012 — MFA: authenticator-app TOTP as a step-up after primary auth; secret encrypted at rest; hashed single-use recovery codes. (2026-07-01)**
The platform's custom auth stack (ADR-002) has no second factor. ADR-C15 once claimed TOTP via
`AddDefaultTokenProviders()`, but that was superseded by ADR-002 and never built — so this is a genuine
gap, not a re-do. Add authenticator-app **TOTP** (RFC 6238) as an optional second factor, enforced as a
**step-up** after the existing primary auth, reusing the crypto the platform already has.

**Decision:**
1. **TOTP via Otp.NET** (latest stable, no previews — ADR-C10). Per-user secret; enrollment returns an
   `otpauth://…` provisioning URI the client renders as a QR. Verification allows a small time-step
   window (±1) for clock skew; comparisons are constant-time.
2. **The secret is encrypted at rest** with the existing **Data Protection** stack (an `IDataProtector`;
   keys already persisted to the DB). It is **never returned after enrollment and never logged**.
3. **Two user-scoped entities** (identity, not tenant): **`UserMfa`** (`UserId`, `EncryptedSecret`,
   `Enabled`, `EnrolledAt`) — one per user; **`MfaRecoveryCode`** (`UserId`, `CodeHash`, `UsedAt`) —
   single-use, **hashed with the existing `ITokenHasher`** (SHA-256), same pattern as
   `LoginToken`/`RefreshToken`. Both are **wiped by account erasure** (GDPR-2, ADR-011).
4. **Step-up at the auth convergence point.** Every primary-auth path (OAuth callback, magic-link/OTP
   verify, native exchange) resolves a `User` then calls `SessionService.IssueAsync`. When the user has
   MFA enabled, primary auth does **not** issue a full session; it returns an **MFA challenge** — a
   short-lived **signed** token (Data Protection time-limited, like the file-download token) naming the
   user + purpose. `POST /api/auth/mfa/verify` accepts the challenge + a TOTP **or recovery** code and,
   on success, calls `IssueAsync` to complete login. One enforcement path, no per-endpoint duplication.
5. **Recovery codes** are issued once at enrollment (shown once), stored **hashed + single-use**, and
   accepted at the challenge as an alternative to a TOTP code; regenerating invalidates the old set.

**Constraints recorded:**
1. **Secret stays encrypted at rest**, is returned only as the enrollment provisioning URI, and never
   appears in logs or later reads.
2. **Recovery codes are hashed + single-use**, shown exactly once; verification is constant-time.
3. **Step-up is enforced server-side** — the signed MFA challenge is required to complete login; a
   client cannot skip straight to a full session.
4. **MFA is user-scoped PII** — wiped by account erasure (GDPR-2); it is not tenant data.
5. **Enabling requires proving possession** — MFA turns on only after a valid code confirms enrollment
   (never enabled from an unverified secret); disabling likewise requires a valid code.
*Rationale:* MFA is a security baseline any serious SaaS needs, and doing it as a step-up at the single
`IssueAsync` convergence keeps every login path covered without touching each one's transport quirks.
Reusing Data Protection (secret encryption + challenge signing) and `ITokenHasher` (recovery codes)
means no new crypto primitives — only Otp.NET for the standard TOTP math.

**Addendum (MFA-3, 2026-07-01) — redirect paths brought in line with decision point 4.** MFA-2 wired the
step-up only into the **JSON** paths (OTP verify, native exchange); the **web OAuth callback** and
**magic-link verify** still issued a session directly, so an MFA-enabled user could sign in via those and
skip the second factor — an implementation gap against point 4, which always intended *every* primary-auth
path to enforce step-up. MFA-3 routes both redirect handlers through `CompleteOrChallengeAsync`: when a
challenge is returned they redirect to `/login?mfa=<challenge>` instead of `/auth-callback`. The challenge
travelling as a query param is acceptable — it's the same signed, single-use, 5-min Data-Protection token
already returned in JSON elsewhere, carries no secret, and is useless without a live TOTP/recovery code
(same class as an OAuth authorization code in a URL). The client reuses the existing step-up prompt →
`POST /api/auth/mfa/verify`.

**Addendum (MFA-4, 2026-07-01) — native step-up completes the coverage.** The server always challenged the
native OTP/OAuth-exchange paths (point 4), but the MAUI client only understood a tokens response and
treated a challenge as a failure. MFA-4 teaches the client to recognize `{mfa_required, challenge}`
(`AuthService` now returns a `SignInResult`; `VerifyMfaAsync` completes the step-up with tokens in the
body) and reuse the same in-app prompt. Client-only, no API change. **MFA is now enforced on every
sign-in path — web (OTP/OAuth/magic-link) and native (OTP/OAuth) — with no remaining gaps.**

Stories + slice plan: `docs/stories/mfa.md` (epic `MFA`).

---

**ADR-013 — In-app notifications: a per-user notification center + delivery preferences, fanned out through the outbox. (2026-07-01)**
Transactional email exists (`IEmailSender`), but there's no in-app notification center and no per-user
control over how a user is reached. This adds both, reusing the reliable-delivery path the platform
already has (the outbox, ADR-007) rather than a second delivery mechanism.

**Decision:**
1. **`Notification` is per-user, not tenant-scoped.** It's a personal artifact ("your bell menu"), so it
   is keyed by `user_id` and is the sanctioned per-user carve-out (**ADR-C2** — only preferences/personal
   state are per-user; everything else is tenant-scoped). A user reads only their own notifications,
   filtered by the authenticated user id — **not** the tenant filter. Fields: `id`, `user_id`, `kind`
   (stable verb), `title`, `body`, `metadata` (jsonb), `read_at` (nullable), `created_at`.
2. **One fan-out entry point.** `INotificationService.NotifyAsync(userId, kind, title, body, metadata)`
   is the single call a feature makes to notify a user. It creates the **in-app** row **transactionally**
   (a DB write in the same unit of work as the triggering change — no extra reliability machinery needed)
   and, per the user's preferences, dispatches the **email** copy through `IEmailSender` — which is
   already the **outbox-backed** sender (ADR-007), so the out-of-process channel is reliable + retried.
   One domain event → one call → both channels, each delivered by the right mechanism.
3. **Per-user delivery preferences** (`NotificationPreference`, keyed by `user_id`): channel toggles
   (in-app / email), defaulting to on. This is the ADR-C2 per-user preference, alongside `User.Locale`.
   The fan-out consults it; a feature never hard-codes channels.
4. **A user-scoped notification-center API** — list (paginated), unread count, mark-one/all read, and
   get/update preferences. All scoped to the caller (`NameIdentifier` claim), like `/api/auth/me` — never
   tenant-filtered, never another user's notifications.
5. **No new delivery infrastructure.** In-app = a DB row; email = the existing outbox path. The platform
   ships the center + the fan-out seam; a feature calls `NotifyAsync`, it does not wire channels itself.

**Constraints recorded:**
1. **Per-user, never cross-user** — every read/write is scoped to the authenticated user; a notification
   is only ever visible to its owner.
2. **In-app is transactional, email is outbox-reliable** — don't push the in-app insert through the
   outbox (it's a same-DB write); don't send email inline (use the outbox-backed `IEmailSender`).
3. **Preferences gate delivery** — the fan-out reads prefs; no channel is hard-coded at a call site.
4. **Notifications are user PII** — wiped by account erasure (GDPR-2, ADR-011), like the other
   user-scoped identity rows.
5. **No secrets/PII beyond identifiers in `metadata`** — same rule as audit metadata.
*Rationale:* the next ask after transactional email is almost always an in-app center + "how do you want
to be reached." Keying it per-user (ADR-C2) and fanning out through the existing outbox keeps it a thin,
reliable addition — a feature gets multi-channel, preference-aware notification from a single call, with
no new delivery machinery to operate.
Stories + slice plan: `docs/stories/notify.md` (epic `NOTIFY`).

---

**ADR-014 — Admin back-office: a config-gated platform-staff surface for cross-tenant inspection + audited, short-lived impersonation. (2026-07-01)**
Support and debugging at scale need a **platform-staff** surface — outside the tenant model — to inspect
any tenant and, when necessary, "sign in as" a user. This is the **highest-blast-radius** feature in the
platform, so it is built entirely on the guardrails already in place (the audited cross-tenant escape
hatch, ADR-003; the audit log, ADR-008) rather than loosening any of them.

**Decision:**
1. **Platform staff is a config allowlist, not a self-serve role or a DB flag.** The set of staff is
   configured **out-of-band** (`Admin:StaffEmails`, from `.env`/env vars), checked by
   `IPlatformStaffService` and enforced by an **`AdminOnly`** authorization gate (403 for anyone not on
   the list). It is deliberately **not** part of `TenantRoles`/the RBAC matrix (that's tenant-scoped) and
   **not** a toggle reachable from the app — the highest privilege can only be granted by whoever controls
   deployment config.
2. **Cross-tenant reads go through the audited `QueryAllTenants()` escape hatch only (ADR-003).** The
   global tenant filter is **never loosened**; admin read endpoints use the same audited hatch feature
   slices are forbidden from using, re-constrained to the target tenant. Admin is read-only over tenant
   data (inspect, don't mutate).
3. **Impersonation issues a short-lived, non-refreshable, loudly-audited access token for the target
   user.** "Sign in as" mints an access token carrying the target's claims **plus an `impersonated_by`
   claim** (the staff user id) and a **short expiry**, with **no refresh token** — so it auto-expires and
   can't be silently extended. The impersonator acts as the target within that window; the token scopes
   naturally via the target's `tenant_id` claim (no filter bypass).
4. **Every admin action is audited (ADR-008), prominently.** Cross-tenant reads and — especially —
   impersonation start record an `AuditEvent` with the staff actor + target; impersonation is stamped in
   the **target's** tenant so that tenant's owner can see "a platform admin accessed this account."
5. **No standing admin session over tenant data.** Staff authenticate as normal users (their own
   account); the admin surface is gated per-request by the allowlist. There is no separate admin login.

**Constraints recorded:**
1. **The global filter is inviolable** — admin never turns it off; cross-tenant reads use the audited
   hatch, scoped to the target.
2. **Staff membership is out-of-band config** — never settable via the app, never a tenant role.
3. **Impersonation is short-lived + non-refreshable + audited** — no refresh token, minutes-not-hours
   expiry, an `impersonated_by` claim, and a loud audit record in the target's tenant.
4. **Admin is read-only over tenant data** — inspection + impersonation, not direct cross-tenant writes
   (a staff member who needs to change tenant data does it *through* impersonation, which is audited).

**Addendum (2026-07-02, ADMIN-3):** the admin surface gains **staff announcements** —
`POST /api/admin/tenants/{id}/announce` notifies every member of a tenant through the normal NOTIFY
fan-out (ADR-013; per-user prefs decide in-app vs outbox email) and records `admin.announcement.sent`
in the target tenant. This is the one sanctioned admin write: it creates **per-user notification rows
only** — tenant data stays read-only, the filter stays engaged (`EnterTenant`), and the action is as
loudly audited as inspection/impersonation. Also fixed en route: the web client's staff probe
(`AuthService.IsStaffAsync`) ran on the Bearer-less auth HttpClient, making `/admin` unreachable on
web; it now attaches the in-memory access token explicitly.
5. **No secrets/PII in admin responses or audit metadata** beyond identifiers — same rule as elsewhere.
*Rationale:* support tooling is necessary but dangerous; the safe way to build it is to reuse the audited
escape hatch and the audit log instead of adding new privileged paths, and to keep the staff grant in
deployment config where it can't be escalated from inside the app. Impersonation as a short-lived,
non-refreshable, audited token gives support what they need while bounding the blast radius and leaving a
trail the affected tenant can see. Depends on: audit (ADR-008 ✅), the escape hatch (ADR-003 ✅), RBAC
(ADR-009 ✅).
Stories + slice plan: `docs/stories/admin.md` (epic `ADMIN`).

*Amendment (v2 audit, 2026-07-01) — impersonation + tenant-detail reads use `EnterTenant`, not
`QueryAllTenants()` "only".* Decision point 2 above describes cross-tenant reads going through the
audited `QueryAllTenants()` hatch. As shipped, the **tenant list** uses non-scoped tables directly
(`ITenantRepository.ListAllAsync`, no hatch), while **tenant-detail inspection and impersonation
audit-writes enter the target tenant via `ITenantContext.EnterTenant`** (ADR-003 amendment 2026-06-25)
so the scoped reads/writes go through the normal filter engaged — rather than loosening it. The filter
is still never disabled; `EnterTenant` was chosen over the hatch precisely because it keeps scoping
*on*. The `docs/stories/admin.md` Gherkin/prose has been reconciled to name `EnterTenant`.

---

**ADR-015 — Public API + API keys: a config-gated, default-off programmatic surface authenticated by tenant-scoped API keys. (2026-07-01)**
Everything so far serves an interactive human (JWT/cookie session). A **public API** serves *machines* —
a customer's backend, a script, CI, an integration — which need a non-interactive credential. The user
initially parked this (no customer-facing API) and **reversed that on 2026-07-01**; it's built now, but
**off by default** since a public surface is a deliberate, security-relevant opt-in.

**Decision:**
1. **`ApiKey : ITenantScoped`** — store only the **hash** (like `RefreshToken`/`TenantInvitation`; reuse
   `ITokenHasher`), plus a non-secret `Prefix` for display, granted `Scopes`, optional `ExpiresAt`, and a
   `RevokedAt`. The raw key (`pk_…`) is shown **once** at creation, never again.
2. **A second authentication scheme** (`ApiKeyAuthenticationHandler`, scheme `"ApiKey"`) alongside JWT
   Bearer. A key in `X-Api-Key` (or `Authorization: Bearer pk_…`) resolves — **across tenants, pre-scope**
   (the key selects its tenant) — to a principal carrying the **`tenant_id` claim**, so the existing global
   query filter scopes the request with no extra wiring. Bad/expired/revoked ⇒ 401.
3. **Scopes gate routes.** Keys carry example scopes (`read`/`write`); a public route declares its
   requirement with **`.RequireApiScope(...)`** (→ 403 `insufficient_scope`). Scopes are a superset seam —
   an all-scope key = full access — so it's mechanism-first without committing to a scope taxonomy.
4. **Owner-only management.** `/api/apikeys` (create/list/revoke) is JWT-authed and gated by a new
   **`Permission.ManageApiKeys`** (owner-only by construction — owner gets every permission). Keys grant
   programmatic tenant access, so minting them is as sensitive as billing/role changes.
5. **Config-gated, STRONG gating.** `PublicApi:Enabled` (default false). When off, the API-key scheme
   isn't added and neither the management nor public routes are mapped — they return **404, they don't
   exist** (not merely 403). Minimal-API groups (not controllers) make the conditional mapping clean.

**Constraints recorded:**
1. **Only the hash is stored**; the raw key is revealed once. Revoked/expired keys never authenticate.
2. **API-key requests are tenant-scoped exactly like user requests** (same `tenant_id` claim → same global
   filter); a key can never reach another tenant's data.
3. **Default off** — a deployment opts into the public surface deliberately; the attack surface (a
   long-lived credential + published routes) doesn't exist until then.
4. **Reuses existing rails** — token hashing, the tenant filter, RBAC (`.RequirePermission`), and the
   minimal-API feature-group convention. No new crypto.
*Rationale:* a public API is the "others build on this" layer; doing it as a second auth scheme that mints
the same `tenant_id`-scoped principal means the entire tenant-isolation guarantee applies for free, and
strong config-gating means the platform ships the capability **dormant** rather than exposing a surface no
one asked for. HOOKS (outbound webhooks) is the companion outbound half (ADR-016).
Stories + slice plan: `docs/stories/pubapi.md` (epic `PUBAPI`).

*Amendment (v2 audit, 2026-07-01) — PUBAPI-2 shipped: per-key rate limiting + a leak-free public
OpenAPI doc.* Hardening beyond PUBAPI-1: a **per-API-key rate-limit policy** (`RateLimiting.PublicApiPolicy`,
partitioned by key so one tenant's key can't exhaust another's budget) on the public routes, and a
**curated, leak-free public OpenAPI document** served **anonymously** at `GET /api/public/openapi.json`
that emits **only** the public routes (never the internal/management surface). Both live behind the same
`PublicApi:Enabled` gate (off ⇒ absent). Still open: key **rotation** and a real scope taxonomy. Tests:
`RateLimitingTests` (per-key isolation). See `docs/stories/pubapi.md`.

---

**ADR-016 — Outbound webhooks: tenant subscriptions delivered through the transactional outbox, HMAC-signed, config-gated default-off. (2026-07-01)**
The **outbound** half of the integration story (ADR-015 is inbound): let a tenant subscribe to events in
their data so *their* systems are notified (push) instead of polling. Also parked-then-un-parked on
2026-07-01; **off by default** for the same reason as PUBAPI (a new outbound surface is a deliberate opt-in).

**Decision:**
1. **`WebhookSubscription : ITenantScoped`** — a tenant registers a `Url` + the `EventTypes` it wants, with
   a per-subscription **signing secret** stored **encrypted** (Data Protection — the plaintext is needed to
   HMAC-sign, so it can't be hashed; same approach as the MFA secret), revealed once at creation.
2. **Delivery IS the outbox pointed outward** (ADR-007) — don't build a second delivery mechanism.
   `IWebhookPublisher.PublishAsync(eventType, data)` fans out to every active matching subscription,
   enqueuing **one `"webhook"` outbox message per subscription** (staged on the caller's unit of work, so
   it's atomic with the triggering change). The `WebhookOutboxHandler` signs + POSTs each; a **non-2xx
   throws**, so the outbox's existing **retry/backoff + dead-letter** apply for free.
3. **HMAC-SHA256 signatures.** Each POST carries `X-Webhook-Id` (event id, for receiver dedup — deliveries
   are at-least-once), `X-Webhook-Event`, and `X-Webhook-Signature: sha256=<hex>` over the raw body. The
   receiver recomputes with the shared secret to verify authenticity + integrity.
4. **Owner-only management** (`/api/webhooks`, new **`Permission.ManageWebhooks`**): register/list/remove,
   plus a **synchronous "send test"** (`/{id}/test`) that POSTs a `ping` and returns the endpoint's status,
   so the owner gets immediate feedback (real events are async via the outbox).
5. **Config-gated, STRONG gating.** `Webhooks:Enabled` (default false). Off ⇒ the management routes aren't
   mapped (404); the delivery handler is registered but dormant (no subscriptions ⇒ nothing to deliver).

**Constraints recorded:**
1. **Signing secret encrypted at rest**, revealed once; every delivery is signed so receivers can verify.
2. **At-least-once, out-of-order** delivery (outbox semantics) — receivers dedup on `X-Webhook-Id`.
3. **Reuses the outbox** — no bespoke retry/backoff/dead-letter; deliveries are durable + tenant-scoped.
4. **Default off** — the outbound surface doesn't exist until a deployment enables it.
*Rationale:* webhooks are the "our product notifies your systems" half of being a platform; building them
as the outbox pointed outward means durability, retries, and atomicity-with-the-change come for free, and
the only new parts are the subscription model + signed HTTP POST. A tenant-facing **delivery log**
(per-attempt history) is a natural HOOKS-2 follow-up — until then the outbox's own status/attempt/error
columns are the record.
Stories + slice plan: `docs/stories/hooks.md` (epic `HOOKS`).

*Amendment (v2 audit, 2026-07-01) — HOOKS-2 shipped: delivery log + replay.* The "natural follow-up"
above is now built. A **`WebhookDelivery`** record is written per delivery attempt (retries add rows):
event type/id, the exact `Body` sent, `success`, `status_code`, `error`, `created_at`. Owner endpoints
under `/api/webhooks` view the log and **replay** a delivery (re-enqueues the retained body through the
outbox). Like `OutboxMessage`, `WebhookDelivery` is deliberately **not** `ITenantScoped` (it's written
from the tenant-less outbox dispatcher); its `TenantId` is a plain filter column the read side scopes
on. See `docs/DATA_MODEL.md` and `docs/stories/hooks.md`.

**ADR-017 — Hosting: free-tier single-origin deployment — Render (API serving the WASM bundle) + Neon Postgres + Brevo. (2026-07-02)**
Resolves the hosting decision deferred in `docs/TECH_STACK.md` ("pick near deploy"). The driver set:
**$0/mo, no credit card, the refresh-token cookie must stay first-party, and the in-process background
jobs (outbox dispatcher / scheduler / lapse sweep) must not be silently broken.** Decided:

1. **Single origin.** The API container **also serves the published Blazor WASM bundle** (framework
   files + SPA fallback to `index.html`, with `/api/**` excluded from the fallback). One origin means
   the refresh cookie is always first-party — the entire third-party-cookie failure class (Safari ITP,
   Chrome's phase-out) vanishes, and per-environment CORS configuration disappears. **This does not
   weaken the clean-API-boundary rule (golden rule 2 / ADR-004):** the UI still consumes the API over
   HTTP only; the API merely serves its static files. Local dev keeps the separate `src/Web` dev
   server (hot reload), and the `BlazorClient` CORS policy remains for it + native clients.
2. **Render free** hosts the container (512 MB, TLS + subdomain included, deploy hooks, no card
   required). **Accepted trade-off, recorded:** free instances sleep after ~15 min idle — first
   request cold-starts (~30–60 s) and the outbox/scheduler pause while asleep (queued sends resume on
   wake). Acceptable for staging QA; **prod requires an always-on plan (~$7/mo) or equivalent — never
   ship paid users on a sleeping instance.** The image is plain Docker, so the exit cost is nil.
3. **Neon free** is the Postgres (17), used as plain Postgres (Neon Auth stays off — this platform owns
   auth, ADR-002). Chosen over Supabase for this role: it is *just* Postgres (no redundant auth/storage
   platform beside our own), and it **auto-wakes in ~1 s** from autosuspend vs Supabase's 7-day idle
   pause needing a manual unpause. **Connection:** use the **direct** endpoint over TLS — a single
   instance keeps its own Npgsql pool, and this app polls (no `LISTEN/NOTIFY`) and uses no server-side
   prepared statements, so it doesn't need PgBouncer. (Neon's pooled `-pooler` endpoint is
   transaction-mode; the app is compatible with it but only benefits it at many-instance scale.) Bonus
   noted for later: Neon DB branching enables free per-preview-environment databases.
4. **Brevo** (free, 300 mails/day) is staging + prod SMTP through the existing `IEmailSender` — it was
   already the platform's assumed real provider in the `.env` docs. **Consequence:** staging has no
   Mailpit, so email-based QA cases use real (plus-addressed) inboxes there, and the automated
   post-deploy smoke checks health/app-shell only, never email journeys.
5. **Environments follow the git model:** `develop` auto-deploys **staging** (behind CI + a
   post-deploy smoke gate); `main` deploys **prod** behind a required-approval GitHub environment —
   preserving "`main` is deploy-only". The platform proves the machinery on staging; actual prod
   provisioning is each downstream app's first deployment step (runbook: `docs/DEPLOYMENT.md`).
6. **Proxy correctness, gated:** `UseForwardedHeaders` (for/proto) is added **config-gated, default
   off** — required behind Render's TLS-terminating proxy (else the per-IP passwordless rate limiter
   collapses into one shared bucket and OAuth redirect URIs generate as `http`), but an IP-spoofing
   vector if honored when *not* behind a proxy.

**Alternatives rejected:** **Vercel / Cloudflare Pages for the WASM** — split origins make the refresh
cookie cross-site (broken in Safari today, Chrome tomorrow) unless a custom domain unifies the two
hosts; with single-origin hosting a second platform is pure liability. **Railway** — excellent DX but
no longer free ($5/mo Hobby after the one-time trial credit). **Google Cloud Run** — a real free tier,
but CPU is throttled to ~zero between requests, which breaks the in-process outbox *subtly* (worse
than Render's honest sleep), and it requires a card. **Supabase as the DB** — workable, but free
projects pause after 7 idle days (manual unpause) and we would use ~10 % of the platform. **Oracle
Cloud Always Free VM** — the only truly-free *always-on* option; rejected for account-reclamation
risk, noted in the runbook as the self-host escape hatch. A **custom domain** (~$10/yr) is the
deliberate first paid upgrade (pretty URLs + DKIM deliverability); nothing in the architecture
depends on it.
Stories + slice plan: `docs/stories/deploy.md` (epic `DEPLOY`).

*Amendment (2026-07-14; recorded 2026-08-25 — the branch carrying it predated ADR-023's numbering
and was recovered during branch housekeeping) — the platform itself never activates production;
staging is its terminal environment.* Point 5 already assigned prod provisioning to "each
downstream app's first deployment step"; this makes it explicit after `STATUS.md` kept listing prod
activation as a platform to-do (same scope logic as ADR-024): a live prod service for the platform
would be a paid Stripe key, a prod Neon DB, and an always-on Render instance serving an app with
zero users — recurring cost and operational surface proving nothing that the live, auto-deployed,
RLS-enforced staging doesn't already prove. The `main`→prod pipeline (DEPLOY-3), the `STATUS.md`
§5 walkthrough, and `DEPLOYMENT.md` §6–7 stay maintained as the **downstream Phase-8 runbook**
(`NEW_APP_GUIDE.md`). One consequence carried as a Phase-8 note, mirroring ADR-024's signing traps:
the pieces that only run at prod activation — the **RLS two-role topology + posture guard**
(ADR-020, `DEPLOYMENT.md` §7) and the **Production live-Stripe-key startup guard** — get their
first real execution during a downstream app's activation, not here.

**ADR-018 — Native (MAUI) client: commit to full feature parity across Android/Windows/iOS/macOS, incl. automated native tests + signed distribution. (2026-07-02)**
Resolves the deferred "non-web framework commitment" from `docs/TECH_STACK.md`. The platform already ships
**MAUI Blazor Hybrid** shells that reuse the shared RCL (`Shared.Ui`) and have native auth wired (OTP,
OAuth via system browser, MFA step-up MFA-4, secure-storage tokens) — so the native clients already render
every web screen. We commit to closing the remaining gap to **full parity**: verify every feature on
native, fix WebView-vs-browser deltas, test the native build + UI in CI, and produce **signed, shippable
artifacts** for all four platforms. Decided:

1. **MAUI is the native stack** (not Uno/Avalonia/PWA). Rationale: it reuses the exact C# Blazor
   components already built, so parity is verification + glue, not a second UI. Alternatives stay noted in
   `TECH_STACK.md` as fallbacks if MAUI's maturity disappoints.
2. **Parity means "what web does", not more.** OS push notifications, biometrics, and other native-only
   capabilities are **beyond parity** and out of scope for this epic (future epics if wanted). The
   in-app notification center (NOTIFY, polling) is the parity bar, not native push.
3. **Web-first still holds** (golden rule 5): features land + prove on web first; this epic keeps native
   *caught up*, it does not invert the order.
4. **Full-platform scope accepts real, recorded costs** (the user opted into "everything"): a **macOS CI
   runner** (to build/test/sign iOS + macCatalyst), an **Apple Developer account** ($99/yr), and
   **signing material managed as repo secrets** (Android keystore, Windows cert, Apple cert+profile,
   base64-encoded, never committed — same discipline as `.env`, ADR-001). Without the macOS runner +
   Apple account, the Apple-platform slices can't run — so they're sequenced last, after the
   Android/Windows path proves the machinery.
5. **Sequenced in waves** (guardrails → gap-fixes → verification → distribution): a build gate + a
   `docs/NATIVE_PARITY.md` audit first (scopes everything), then WebView-gap fixes, then a manual +
   automated native QA pass, then per-platform signing/packaging. Don't automate or distribute before the
   app is verified working.

**Accepted trade-off:** native UI tests (Appium / .NET MAUI UITest on emulators/simulators) are slower and
flakier than Playwright-web — kept to a small smoke suite with retries; the manual native QA pass is the
broader safety net. **The honest counterweight:** automated native E2E + store distribution are large and
partly per-app; committing the platform to them (vs deferring) is a deliberate choice to make native a
first-class, shippable channel rather than an experiment.
Stories + slice plan: `docs/stories/native.md` (epic `NATIVE`).

*Amendment (NATIVE-1, 2026-07-03) — Apple CI cadence + Maui lockfile.* The build gate shipped with two
free-tier-conscious refinements: (1) the **iOS/macCatalyst CI legs run on develop pushes only**, not per
PR — macOS runners bill at 10× on a private repo, so per-PR Apple builds would drain the 2 000-min/month
quota for little added signal (breakage still surfaces within one merge); Android + Windows legs run on
every trigger. (2) The Maui project sets `RestorePackagesWithLockFile=false` (a documented exception to
the B11-6 lockfile rule): its TFM list is host-OS-conditional, so the resolved graph differs per OS and a
single committed lockfile can never satisfy locked-mode on all three runners — CPM alone pins its
versions.

*Amendment (2026-07-06) — distribution scope is re-confirmed per platform at the NATIVE-8 gate.*
Before starting the signing/distribution slices (NATIVE-8…11), re-confirm **per platform** that
concrete downstream demand justifies the distribution tail (signing material, the Apple
account/hardware, per-release QA columns, store review friction). The parity commitment is about
**capability** — every feature works on every platform, proven by the CI build gate and the QA plan —
not about shipped store artifacts; a platform may therefore hold at "builds green in CI, distribution
deferred per-app" without violating this ADR. This keeps point 4's recorded costs a decision that is
re-made at the gate rather than an autopilot consequence of the original commitment.

**ADR-019 — Platform identity: "Perezosoft Platform"; `Perezosoft.*` code identity; downstream apps rebrand by find/replace. (2026-07-05)**
The repo (formerly "template") is named **perezosoft-platform** and its engineering identity is
**`Perezosoft.*`** end to end: solution `Perezosoft.slnx`, all project/assembly names, the root
namespace, the JWT issuer, and the MAUI `ApplicationId` (`com.perezosoft.platform`). "Template" no
longer appears as an identifier anywhere — it survives only as the English word for the repo's role.
*Rationale:* "Template" collided with ordinary English (docs, comments, third-party API names),
making every downstream rename risky; "Perezosoft" is a made-up word, so standing up a new app is one
unambiguous find/replace of `Perezosoft` → `<Brand>` per `docs/REBRANDING.md`.
*Downstream convention:* apps clone-and-rebrand (fork-and-forget). Keeping `Perezosoft.*` namespaces
in a downstream app (for clean upstream `git merge`) and extracting the platform as NuGet packages
were both considered and deliberately left open — nothing in this rename forecloses either (see
`docs/PLATFORM_BACKLOG.md`).
*Deliberately unchanged:* the DataProtection application name (`"template"`) and the four
`CreateProtector("Template.*.v1")` purpose strings — they feed encryption key derivation, so renaming
them would orphan MFA/webhook secrets already encrypted at rest; they are guarded by comments and may
only change alongside a re-encryption migration. The Render service keeps the name `template-staging`
(Render treats the name as service identity; renaming would mint a new service + URL and churn the
OAuth consoles for zero functional gain — fold into a future console-touching change if desired).

**ADR-020 — Tenancy defense-in-depth: Postgres row-level security as a second, DB-level wall under the EF query filter. (2026-07-06; IMPLEMENTED — see the addenda. Header fixed 2026-07-27, v3 T57: it still read "DEFERRED" long after the backstop merged)**
Tenant isolation is currently enforced entirely in the application layer: the ADR-003 global query
filter, the write-side interceptor (V2-B2), and the arch-test bans. One missed seam in a future
feature — most plausibly a downstream app's vertical slice, written outside this repo's review
discipline — is a cross-tenant leak, the worst bug class a multi-tenant SaaS has. **Decision:** add
Postgres **row-level security** as an independent second wall, so a query that escapes the EF filter
still returns zero foreign rows at the database. Decided:

1. **`FORCE ROW LEVEL SECURITY` + one policy per `ITenantScoped` table** (currently six real ones;
   the Notes sample inherits the pattern). Policy shape:
   `tenant_id = current_setting('app.tenant_id', true)::uuid` with an explicit **null ⇒ deny** — a
   missing setting fails closed (R-rules ethos), never "no setting = see everything".
2. **The setting rides the existing tenant seam.** An EF Core interceptor issues
   `SET LOCAL app.tenant_id = …` at transaction start, fed by the same `ITenantContext` that feeds
   the query filter. `SET LOCAL` (transaction-scoped) is mandatory — Npgsql connection pooling makes
   connection-scoped settings leak across requests.
3. **Two database roles.** A **migrator/owner** role runs EF migrations (table owners bypass RLS
   even with FORCE via ownership semantics — keep it out of the runtime path) and a **runtime** role
   subject to policies. System paths that legitimately cross tenants — the outbox dispatcher,
   scheduled sweeps, admin inspection, GDPR export, the billing webhook's `EnterTenant` — get an
   **explicit** bypass (dedicated role or system GUC settable only by the system context); all such
   call sites are already enumerated behind `QueryAllTenants()`/`EnterTenant`, so the bypass audit
   is a grep, not an investigation.
4. **The keystone test is the feature:** open a raw connection as the runtime role, set tenant A,
   read tenant B's rows with the EF query filter out of the picture ⇒ **0 rows**. Locked in with a
   B11-style arch/CI gate so the fail-closed property cannot silently regress.
5. **Scope guard:** no per-user RLS, no tenant-timezone quota resets, no policy-based admin scoping —
   separate decisions, separate ADRs.

*Rationale:* the marginal cost is unusually low here (single choke-point tenant context, sanctioned
escape hatches already enumerated) and the payoff multiplies — every downstream clone inherits the
backstop. Pre-production is the cheap window: with no live tenants the two-role topology is
provisioning config; after activation it becomes a data migration with a rollback plan. Perf is
negligible (the policy predicate is the same indexed `tenant_id` comparison the filter already
generates, plus one `SET LOCAL` round-trip per transaction).
*Cost accepted:* environment plumbing is the bulk of the slice — roles across local compose, the CI
E2E stack, Neon staging/prod, `.env.example`, and a `DEPLOYMENT.md` section.
*Sequencing:* deferred behind the in-flight NATIVE work; **gates §5 of `STATUS.md` (production
activation)**. Design detail: `docs/PLATFORM_BACKLOG.md` §11.

*Addendum (implemented, 2026-07-06) — three findings from the build sharpened the design:*
1. **The GUCs are set by a separate parameterized command, not `SET LOCAL` prepended to the main
   command** — a prepended statement occupies a result position and corrupts EF's positional
   consumption of SaveChanges batches. `RlsSessionInterceptor` (command + connection + transaction
   facets) asserts session-level `set_config` per connection with change-tracking, invalidated on
   connection open and transaction/savepoint rollback (`set_config` is transactional).
2. **EF does not render query tags into the `ExecuteUpdate`/`ExecuteDelete` pipeline** (pinned by
   test), so tags only sanction cross-tenant *reads* (`QueryAllTenants()`, the enumerated
   Infrastructure sites). Set-based cross-tenant *writes* use `ITenantContext.EnterTenant` — the
   invitation accept now enters the invitation's tenant for the conditional flip (same trusted
   contract as the billing webhook); dissolve wipes already run with the target tenant current.
3. **The HTTP integration harness runs as the non-privileged runtime role** (superuser only
   pre-migrates + provisions), so the full Api.Tests suite exercises the app RLS-ENFORCED — the
   same posture as Neon, where `FORCE` makes even the (non-superuser) owner subject, meaning
   **staging gets live enforcement with no config change**. Also shipped: the optional
   `ConnectionStrings:Migrations` split (startup DDL vs runtime), the config-gated fail-closed
   `Rls:EnforceRuntimeRole` posture guard (mirrors the Stripe-key guard; prod activation enables
   it), `docker/db/provision-rls-runtime-role.sql`, `DEPLOYMENT.md` §7, and the
   `RlsMigrationGateTests` parity gate (a new `ITenantScoped` entity without its policy migration
   fails CI).

*Addendum (v3 audit remediation, 2026-07) — the backstop re-hardened where the audit bit it:* the
parity gate above was proven TAUTOLOGICAL as first shipped (v3 RLS-1: the harness back-filled
model-derived policies into the database the gate inspected) and was fixed to migrations-only
provisioning with a bites-test; dissolve/erasure under a foreign entered tenant (RLS-2) was made
all-or-nothing; and the slice recipe is now documented + enforced end-to-end (the hand-written
policy step in `WAYS_OF_WORKING.md` + the PR-template checkbox + the honest gate — v3 T52/T57).

**ADR-021 — Admin back-office writes: narrow, enumerated, audited mutations (amends ADR-014's "read-only" posture). (2026-07-09)**
ADR-014 point 2 declared admin **read-only over tenant data** ("inspect, don't mutate"), with the
ADMIN-3 announcement as the one sanctioned write (per-user notification rows via the normal fan-out).
The 2026-07 manual QA pass surfaced legitimate operator needs that are writes: sending targeted (not
whole-roster) announcements, messaging **every** user (maintenance/incident notices), and putting a
tenant on a paid plan without a checkout (comps, QA, support goodwill). Doing these by impersonation
would be worse — broader power, weaker attribution; doing them off-platform (SQL) breaks the API
boundary. So the read-only posture is **amended, not abandoned**: admin writes exist, but only as an
**enumerated list**, each riding existing seams with existing guardrails.

**Decision:**
1. **Read-only remains the default posture.** Any new admin write must be added to this enumeration by
   a future ADR/amendment — "staff can mutate tenant data" is never a general capability.
2. **Enumerated writes (as of this ADR):**
   a. **Announcements** — per-tenant (all members or an explicit `user_ids` subset, **intersected with
      the actual roster** so a stray id cannot reach outside the tenant) and **platform-wide broadcast**
      (`POST /api/admin/announce-all`). All delivery rides the ADR-013 notification fan-out
      (preference-respecting, per-user rows only — never tenant data).
   b. **Subscription comp/revert** (`PUT|DELETE /api/admin/tenants/{id}/subscription`) — writes the same
      `Subscription` projection a completed checkout produces (active, no period end, **no provider
      ids**); revert deletes the projection (absence ⇒ Free, the ADR-006 fail-closed default). Refused
      **409** whenever a live provider subscription exists (`StripeSubscriptionId` present): Stripe
      remains the sole source of truth for real money (ADR-006) — a staff override must never mask or
      fight provider state.
3. **Every write is scoped and attributed.** In-tenant writes go through `ITenantContext.EnterTenant`
   (ADR-003; the filter stays engaged, RLS satisfied) and are audited in the affected tenant
   (`admin.announcement.sent`, `admin.subscription.comped`, `admin.subscription.reverted`).
4. **The platform-wide broadcast is asynchronous and outbox-recorded.** The unbounded fan-out never runs
   in the HTTP request: the endpoint enqueues one outbox message (202) and `AdminBroadcastOutboxHandler`
   delivers out-of-band, idempotent by construction (handler + status flip commit in one transaction,
   ADR-007). It has **no in-tenant audit row** — `AuditEvent` is tenant-scoped and the action spans all
   tenants; the durable outbox message is the record. A platform-level (cross-tenant) audit trail is a
   known gap, deliberately deferred until a second cross-tenant action needs it.

**Addendum (2026-07-10) — enumerated write (c): staff MFA reset.** MFA had **no recovery path**: the
self-serve disable (`MfaService.DisableAsync`, ADR-012) requires a valid TOTP or recovery code — the
possession proof a user who lost **both** the authenticator and the codes cannot provide — and every
sign-in path steps up (MFA-2/3/4), so such a user was locked out permanently. The escape hatch is
operator-mediated: **`DELETE /api/admin/users/{userId}/mfa`** (staff-gated) wipes the target's
`UserMfa` + `MfaRecoveryCode` rows via a new **`IMfaService.ResetAsync`** that skips code verification —
callable only from the admin path, never exposed on a user-facing endpoint. Preconditions and
guardrails: **identity is verified out-of-band** (support process, not the app) before the reset; the
action is **audited in the target's tenant** (`admin.mfa.reset`, like `admin.impersonation.started`);
and the affected user is **notified through the normal fan-out** (in-app + email,
`security.mfa_reset`), so a malicious or mistaken reset cannot be silent. The reset only removes the
second factor — primary auth is untouched, and the user re-enrolls from Settings. No MFA state is an
idempotent no-op 204 (no audit/notification noise). Console UI: a confirm-gated **Reset MFA** button
on the tenant-detail member row. QA-ADMIN-07.

**ADR-022 — Per-user preference sync: adopt-on-sign-in, reconcile on every sign-in path, "system" stored explicitly (PREFS-1; amends THEME-1's null-mapping and B7-3's no-reload reconcile). (2026-07-14)**
The 2026-07 QA pass failed QA-I18N-02: locale/theme didn't follow the user across browsers. Root
causes: (a) the only `LanguageSwitcher` lived on the login page where the user is always anonymous,
so `PUT /api/auth/locale` was unreachable from the UI and `User.Locale` was never written; (b) the
server→device reconcile ran only in `MainLayout.OnInitializedAsync`, so soft-navigation sign-ins
(OTP/MFA/native) applied nothing until a manual reload; (c) the B7-3 in-process culture switch never
loads WASM satellite resource assemblies (fetched per boot culture), so even a reconcile that ran
left the strings in English; (d) theme "system" was stored as null, indistinguishable from "never
chose", so returning to System on one device never propagated. A centralized preferences table/
endpoint was **considered and declined** (2026-07-14): storage stays per-column on `Users`
(ADR-C2 carve-out), because locale/theme must be readable at token-issue time (JWT claims) and
pre-paint, and the columns already serve that; only the sync behavior changes.

**Decision:**
1. **Preferences get a signed-in home**: a Preferences card on `/settings` hosts the existing
   `LanguageSwitcher`/`ThemeSwitcher` (which already PUT when authenticated). The login-page
   switchers remain as pre-auth, device-local conveniences.
2. **Reconcile runs on every sign-in, not just cold starts**: `AuthService` raises `SignedIn` on
   the unauthenticated→authenticated transition (all body-flow and cookie-refresh paths; NOT on
   mid-session rotation or impersonation), and `MainLayout` re-runs its idempotent
   `ReconcilePreferencesAsync` on it.
3. **Two-way sync**: a set server value wins (apply + cache on device); a never-set server value
   adopts an explicit device choice via the normal PUTs — so a pre-auth login-page pick becomes
   the account preference on sign-in. Never adopts while impersonating (an admin's device must not
   rewrite the impersonated user's preferences).
4. **Locale mismatch = one full reload** (persist first; in-process culture set for the MAUI
   WebView, whose reload doesn't re-run `MauiProgram`). This deliberately reverts B7-3's in-process
   no-reload approach — it couldn't work on WASM (satellite assemblies) — while keeping its actual
   goal: the reload happens only on a real mismatch, never as a guaranteed double load. Theme
   applies live (`data-bs-theme`), no reload.
5. **"system" is stored verbatim** (amends THEME-1): `User.Theme` null now means "never chose",
   which is what makes adoption (3) well-defined and lets System propagate across devices like the
   other two values. No schema change — same nullable column, same endpoints.

**ADR-023 — API documentation governance: the repo Postman collection is the canonical, machine-enforced API contract; the workspace is a one-way mirror. (2026-07-27)**
The platform documents its API as a Postman collection rather than a spec-first OpenAPI document.
This was practice (CLAUDE.md rule + `docs/postman/README.md`) without a recorded decision; the v3
audit (TR-6/TR-10, T55/T57) found the gap and this ADR closes it. Decided:

1. **`docs/postman/Perezosoft.postman_collection.json` is canonical.** Any change to an API
   endpoint (route, verb, params, request/response shape, auth, error codes) updates the
   collection **in the same slice** — the PR-template checkbox and review enforce the habit; the
   `PostmanParityTests` CI gate (T55) enforces the floor: every endpoint the app actually maps
   under `/api` must have a matching request, or an inline exclusion rationale. Browser-flow
   endpoints are documented as annotated **`(doc-only)`** requests.
2. **The Postman workspace is a disposable one-way mirror.** The `postman-sync` workflow pushes
   `docs/postman/**` to the workspace on every `develop` change (sync-by-name); edits made in the
   Postman UI are overwritten on the next sync and are never pulled back. The repo copy is the only
   reviewed, versioned artifact.
3. **Why not spec-first OpenAPI:** the collection *is* executable documentation — chained
   auth flows (OTP via Mailpit, token rotation, shown-once secrets), per-environment files, and
   test scripts double as a manual API harness, which a generated spec can't replace. The one
   OpenAPI surface that exists stays: the leak-free public-API document at
   `/api/public/openapi.json` (ADR-015) serves *external* consumers of the config-gated PUBAPI
   only. Revisit if a downstream app needs full-API OpenAPI for client generation — that would be
   a new ADR, generating *from* the code, with this collection remaining the human-facing harness.
4. **Rebrand note:** the collection, environments, and the sync workflow's file path all rename
   with the app (`docs/REBRANDING.md` already lists them).

**ADR-024 — Native distribution (signing, packaging, store submission) is downstream-app work, not platform scope (resolves ADR-018's NATIVE-8 gate; NATIVE-8..11 leave the platform roadmap). (2026-07-14; recorded 2026-08-25)**
*Recording note:* this decision was made and drafted 2026-07-14 — before ADR-023 (2026-07-27) took
that number — but the branch carrying it never merged; it was recovered during branch housekeeping
and renumbered here. It predates the v3 audit in substance.

ADR-018's 2026-07-06 amendment made distribution a decision **re-made at the NATIVE-8 gate** rather
than an autopilot consequence of the parity commitment. That gate is now decided: signed artifacts
and store listings are **per-app deliverables**. This repo is horizontal chassis only (ADR-019
posture); a release artifact is the ultimate vertical.

*Rationale:*
1. **Signing identity is inherently per-app.** The Android keystore *is* the app's identity; Apple
   certs/profiles bind to a bundle id + team; the MSIX publisher must match a specific manifest.
   The platform has no shippable app — anything it signed would be `com.perezosoft.platform`, an
   artifact nobody ships, so the resulting workflow would be **untested plumbing** the moment a
   downstream app swapped in its own identity.
2. **The costs are recurring and buy the platform nothing.** The Apple Developer account is
   $99/**yr** and lapses certs when it stops; per-release QA columns and store-review friction are
   exactly the distribution tail the 2026-07-06 amendment flagged.
3. **Capability is already proven without artifacts.** The parity commitment is held by the CI
   build gate (NATIVE-1, all four TFMs), the four boot smokes (NATIVE-7), and the QA plan
   (§12–13b + the per-release §13c checklist).

*Decision:*
1. **NATIVE-8, -9, -10, -11 are removed from the platform roadmap** and reclassified as the
   downstream **first-native-release checklist**. Canonical actionable home:
   `docs/NEW_APP_GUIDE.md` Phase 9. The detailed scoping knowledge (keystore discipline,
   tag-triggered release-workflow shape, signing reality per platform, store-account costs) stays
   in `docs/stories/native.md` Wave 4, re-badged as downstream reference — it is **hardening for
   the apps**, not slices this repo will build.
2. **Epic NATIVE closes at verification**: NATIVE-6 (the manual device QA pass) remains the last
   platform slice; all-pass on §13c completes the epic. ADR-018's parity commitment is otherwise
   unchanged (capability on all four platforms, web-first, parity ≠ more).
3. **Two platform-code risks that only manifest under a real signing identity transfer as hard
   checklist items** (they cannot be verified here, precisely because verification needs the
   identity only a downstream app has):
   (a) **packaged MSIX runs containerized** — Preferences/SecureStorage/file paths must be
   re-verified in the packaged flavor before shipping Windows (same failure class as the Catalyst
   keychain gap, PR #125);
   (b) **properly-provisioned Apple builds can claim `keychain-access-groups`** — re-verify
   SecureStorage under the real identity and re-evaluate retiring `DebugFileSessionStore` (the
   `MACCATALYST && DEBUG` fallback from PR #125).
4. **Pre-scoped sub-decisions carry with the checklist** (decided 2026-07-07, still good): enroll
   in Play App Signing (the keystore becomes the upload key); let the MS Store sign the MSIX (no
   purchased cert / HSM); signing material lives only in repo secrets, base64, ADR-001 discipline.

**Accepted trade-off:** every downstream app pays its own signing bring-up — there is no
ready-made release workflow to inherit. Accepted because an unverifiable workflow is a liability,
not an asset; the checklist and scoping notes transfer the knowledge instead. This supersedes
ADR-018's "honest counterweight" (store distribution as a platform commitment) in the downstream
direction the 2026-07-06 amendment anticipated.

---

## App decisions — ¿Y el vuelto? (`ADR-V…`)

> **Numbering.** ADR-001–024 above are the platform's own decisions and are inherited verbatim
> (treat them as constant here — they are re-decided upstream, never in this repo). This app's
> decisions use the **`ADR-V`** prefix so they can never collide with a future upstream ADR-025.
> Donor decisions are cited as `donor ADR-00NN` (`vuelto/docs/decisions.md`).
>
> **Donor decisions absorbed by the platform (not re-recorded):** donor ADR-0001/0002/0003
> (OAuth middleware, in-memory JWT + hashed rotating refresh cookie, verified-email account merge)
> → platform ADR-002; donor ADR-0004 (repository-layer tenant isolation) → platform ADR-003/020;
> donor ADR-0016's membership/invitation/departure mechanics + ADR-0023 (hash-at-rest invitation
> tokens, accept moves membership) → platform ADR-003/009/011 + `HouseholdInvitationsController`;
> donor ADR-0025 (I/O in Infrastructure, Api = composition) → platform ADR-004; donor ADR-0026
> (transactional email) → platform `IEmailSender` + outbox + `BrandedEmail`; donor ADR-0022
> (removed Flutter stack) — historical, moot.

**ADR-V001 — Entry mode: this app is a continuation port of `vuelto/phase2`; the donor is frozen, its tests are the spec, and the platform is extended, never modified. (2026-09-02)**
`y-el-vuelto` was cloned from `perezosoft-platform@d09c60f` (commit `0f7d1dc`) and carries the
**entire** platform (billing, jobs, notifications, files, GDPR, admin, MAUI shells) even where
Vuelto has no immediate use for a subsystem. The donor repo `vuelto` (branch `develop`, 2026-09-02)
is **frozen as a read-only reference**; no product work continues there. The donor's shipped
behavior (Slices 1–6, 8 and the two audit-remediation tracks) is re-homed as vertical slices in the
order P0–P11 of the port plan; only then does the donor's unfinished roadmap resume here (data-driven
bank definitions first). **Rules:** (1) the donor's ~330 domain unit tests + 6 integration classes
port as the specification; its ~40 foundational-behavior tests (invitations, membership lifecycle,
concurrency) are run once against the platform as acceptance checks, then discarded; (2) the
platform is **extended through its seams** (`IRepository<T>`, `ITenantDataContributor`,
`IUserDataContributor`, `IScheduledJob`, `IEmailSender`, `ITenantContext.EnterTenant`, DI) — never
modified in this repo; a genuinely generic gap goes upstream as a `perezosoft-platform` PR first,
and anything Vuelto-specific is solved here; (3) stories live one file per epic in `docs/stories/`,
each scenario citing the donor story it ports (`from US-015`), and new work continues the donor's
numbering from **US-057**.
*Rationale:* the donor is an unfinished project, not a finished product to migrate — the goal is to
stop paying twice for auth/tenancy/email/jobs and to inherit the platform's hardening. Freezing the
donor prevents divergence; porting behind its tests is the only honest parity proof.

**ADR-V002 — Household is the tenant; platform roles owner/admin/member apply; budget data is shared; an email connection is user-keyed. (2026-09-02; from donor ADR-0016, port decision D7)**
The platform `Tenant` is labelled **Household**. All budget data (settings, catalogs, expense lines,
months, weeks, transactions, refunds, envelopes, merchant mappings, pending/ingested vouchers) is
`ITenantScoped`. The platform's three roles apply unchanged: **any member** reads and edits budget
data (no new `Permission` — that is the member baseline); management capabilities (rename, invite,
remove, roles, transfer, dissolve, billing, export) follow `RolePermissions`. The donor's
owner-only matrix is a strict subset, so nothing is lost; `admin` is a gain. **`EmailConnection`
is user-keyed** (not `ITenantScoped`): it is a member's mailbox credential, it survives leave /
remove / dissolve, it is erased with the account (`IUserDataContributor`, R12), and the vouchers it
produces land in the household the member is in **at poll time** (the poll job resolves the
membership and `EnterTenant`s it). A member who switches households therefore re-routes their
inbox — documented, accepted.
*Rationale:* a household genuinely runs one budget from several identities; the credential is the
person's, the data is the household's. The platform's per-user carve-out (ADR-C2) exists for
exactly this shape.

**ADR-V003 — Budget structure settings (week start, month anchor, income defaults) are per household, in their own `BudgetSettings` row. (2026-09-02; port decision D2)**
The donor kept six budget columns on `User`; months and transactions were household-scoped, so a
two-member household could tile months by *whoever entered the transaction*. The port introduces a
tenant-scoped `BudgetSettings` entity (one row per household, created with the donor's defaults on
first use) and `TransactionService`/`MonthService` read it from the ambient tenant instead of
taking a `User`. `User` stays a pure platform entity (locale, theme only).
*Rationale:* budget structure is not a preference (ADR-C2); it fixes a latent donor bug and removes
the largest foundational↔domain coupling in the donor code.

**ADR-V004 — Domain money is dual-currency fixed-point decimal; Stripe remains the source of truth for billing money only. (2026-09-02)**
The platform models no money (its `Subscription` is a projection). This app does: amounts are
`decimal` mapped to `NUMERIC(12,2)`; the per-transaction rate `NUMERIC(10,4)`; refund percentage
`NUMERIC(5,2)`; rounding is half-away-from-zero to 2 dp in `CurrencyMath.Round2`; every stored
amount carries its currency (`CRC` | `USD`) and its CRC/USD pair. Billing (plans, seats, Stripe)
is untouched by this — the two never mix.
*Rationale:* the platform's "no decimals" is a billing statement, not a domain rule; a finance app
needs precise, currency-tagged money. Recorded so the platform's ADR-006 is not misread as a ban.

**ADR-V005 — Pay-cycle months: anchor-window resolution, weeks materialized at creation, months auto-created from transactions and auto-deleted when empty, two incomes per month. (2026-09-02; from donor ADR-0006, 0007, 0012, 0013)**
A date belongs to the month whose anchor window contains it (may be a neighboring calendar month —
never resolve by calendar). `week_count` (4|5) and the weeks are computed once and **stored** so a
later settings change never re-slices history. Months exist only through transactions: created on
the first transaction in an uncovered window (income snapshotted from `BudgetSettings` by week
count), deleted with their weeks when the last transaction goes; there is **no manual month path**.
Month income is two incomes, each amount + currency, editable per month.
*Rationale:* budget periods follow pay cycles; auto-lifecycle removes an entire class of "empty
month" and "forgot to create the month" bugs; stored weeks give historical stability.

**ADR-V006 — The live exchange rate is the source of truth for projections; each transaction freezes its rate forever. (2026-09-02; from donor ADR-0011)**
Months store no rate. Projections resolve: live quote (cached < 1 h counts as live — quota) →
stale cache flagged "as of …" → most recent transaction's rate → block (`exchange_rate_unavailable`).
`exchange_rate_used` is set at creation and never recalculated on edit. No transaction is created
without a rate; a provider rate ≤ 0 is unavailable. The provider (exchangerate-api, free tier) is
an `HttpClient` against a fixed host, allowlisted for the platform's outbound-URL guard (R76).
*Rationale:* actual spend must reflect the rate at purchase; projections must reflect today.

**ADR-V007 — Five transaction classes; payment method and a required bank live on the transaction; category required; refunds are derived from unplanned essentials and realize as inflows; envelopes are transactional. (2026-09-02; from donor ADR-0009, 0010, 0014, 0018, 0019, 0020)**
Classes: `budgeted`, `extraordinary` (label "Discretionary"), `unplanned_essential` (label
"Unplanned"), `inflow` (money in, folded into income), `envelope_contribution` (requires an
envelope and `bank_account`; carved out of expenses/balance). The first three count as expenses.
Every transaction has a `payment_method` (`credit_card` default | `bank_account`) and a **required**
`bank_id` (Cash is a bank) and `category_id`. A `Refund` is derived from an `unplanned_essential`
transaction's refund-expected % (amounts = % × frozen amounts), re-synced on every edit, and only
its `status` is edited directly; `pending → received` creates a linked derived `inflow`
(`source = refund_realization`, read-only), symmetric on flip-back, guarded by a conditional update
so concurrent flips yield exactly one inflow. Envelopes have an annual target + reminder cadence
(`monthly` | `five_week_months`) and no static contribution.
*Rationale:* matches how the household models spend; a refund is a fraction of one transaction; a
realized refund is a real inflow; a bank-less transaction is meaningless for reconciliation.

**ADR-V008 — Catalogs are soft-deleted with the 409 reactivation offer; cross-tenant rows are uniformly invisible (404). (2026-09-02; from donor ADR-0008; port decision D6)**
Categories, banks and envelopes use `is_active`, never hard delete; names are unique per household
case-insensitively; a clash returns 409 `*_exists` (active) or `*_exists_inactive` + id (so the UI
offers Reactivate). Inactive names still render on historical rows. With the platform's global
filter + RLS, a row from another household **does not exist** from the caller's side: every
missing-or-foreign lookup is **404**. This retires the donor's 404-missing / 403-foreign split
(donor ADR-0004 as-built, US-062) — the platform bans the `…UnscopedAsync` reads that produced it (R5).
*Rationale:* deleting a catalog entry must never blank history; uniform 404 kills the existence
oracle the donor had already closed for opaque ids (US-037).

**ADR-V009 — Localization: localize chrome, never translate user data, seed once in the user's locale; English is the stored-value baseline. (2026-09-02; from donor ADR-0017 + 0021; port decision D4)**
UI chrome comes from the platform's `AppStrings` resx (EN base + ES, feature-prefixed keys,
parity-tested). User-entered names are never translated. Starter categories/banks are seeded
**once**, in the locale carried by the caller's JWT, and then are ordinary user data. The donor's
post-hoc `SeedRetranslationService` (rename untouched seeds on language switch) is **dropped** —
the platform owns the locale write and a slice cannot hook it without a cross-feature reference.
Stored values and column names are English (donor ADR-0021); the two deliberate value/label
mismatches (`extraordinary` → "Discretionary", `unplanned_essential` → "Unplanned") move from the
donor's `EnumLabels` into resx keys.
*Rationale:* translating user data corrupts intent; seed-then-freeze avoids silent rewrites of
history; retranslation was a nicety with a real coupling cost.

**ADR-V010 — Email ingestion: read-only mailbox scopes, user-keyed connections, staged review drafts with per-household dedup, confirm through the ordinary transaction path, polled by a platform scheduled job. (2026-09-02; from donor design D1–D6, ADR-0024, US-025–038, WU-3/WU-5)**
Consent uses the platform's Microsoft/Google OAuth client credentials with **read-only** mail
scopes (`Mail.Read` / `gmail.readonly`, `offline_access`, `openid email`) and an HMAC-signed state;
the pipeline **never marks mail read**. Tokens are encrypted with Data Protection (replacing the
donor's AES key). Readers (Graph, Gmail) push every filter into the provider query, **page** until
exhausted, refresh once on 401 then flip to `needs_reconsent`, and skip the poll on 429/5xx.
Parsing is data-routed (`BankVoucherMap`: `(sender, subject) → extractor`), fail-soft, and pure.
Vouchers stage as inert `PendingVoucher` drafts with a per-household `IngestedVoucher` tombstone
(SHA-256 of bank + auth|ref + amount + date, else message id; never silently dropped); tombstones
outlive confirm/discard. Suggestions are copied from merchant mappings (longest pattern wins),
never auto-applied. **Confirm is the only draft→transaction path**: it calls the same
`TransactionService.CreateAsync` as manual entry (`source = email`) inside one transaction with a
conditional `pending → confirmed` flip (donor ADR-0024 — the platform's `EfUnitOfWork` provides the
same savepoint nesting). The poller is an `IScheduledJob` (1-minute tick, connections due by their
own interval, "Sync now" on demand) running in **system context**: per connection it resolves the
owner's membership and `EnterTenant`s it, so staging writes get the same structural isolation as a
request. Outbound `HttpClient`s to Graph/Gmail/token endpoints are fixed hosts, allowlisted (R76).
*Rationale:* the user's mail is never mutated; idempotency comes from data; a misparse is a fixable
blank in a queue, never corrupt budget data; the platform already provides the scheduler,
encryption, isolation and unit-of-work this needs.

**ADR-V011 — UI is rebuilt in the platform's Bootstrap RCL; MudBlazor is not brought over; brand tokens are indigo + gold. (2026-09-02; port decision D1)**
The donor's 6,382 razor lines (MudBlazor 9) are rewritten page by page, in slice order, as
Bootstrap 5.3 components in `Shared.Ui` — the platform deliberately carries no component library,
its shell/theme/MAUI hosts are Bootstrap, and the R68 "identical script set" gate makes a second
UI framework a permanent tax. The rewrite also retires the donor's client debt (43 duplicated inline
DTOs, 1,000-line pages) by decomposing pages into components. Brand: primary indigo `#5A67D8`, the
colón ₡ in gold `#F2CB6E` (**brand only — never a data/state color**), Nunito 800 wordmark, semantic
green/red for under/over budget, CRC/USD tints (`brand/BRAND-SPEC.md` in the donor is the source).
*Rationale:* one design system across web + native; no theme bridging; consistency with the platform
the app is now part of. Costs roughly a third more UI effort than keeping MudBlazor — accepted.

**ADR-V012 — API conventions follow the platform: minimal-API slices under `/api/<feature>`, camelCase JSON records, the shared `ErrorResponse`. (2026-09-02; port decision D5 — supersedes donor ADR-0005 snake_case)**
Every feature is a `MapTenantFeatureGroup` slice with a handler and co-located DTOs; no controllers,
no `[JsonPropertyName]`, no `api/v1` prefix. Error bodies are the platform's
`ErrorResponse(code, message)` — the same shape as the donor's `ApiError`, so the donor's error
codes (`exchange_rate_unavailable`, `refund_status_conflict`, `not_pending`, `*_exists_inactive`, …)
carry over unchanged. The Postman collection is the canonical API doc (ADR-023).
*Rationale:* the client is rewritten anyway, so the snake_case contract has no consumer left;
one convention across platform and app.

**ADR-V013 — Hosting follows the platform runbook: Render (single origin) + Neon + Brevo via SMTP; the donor's Railway + Supabase + Vercel topology and Brevo HTTP sender are retired. (2026-09-02; port decision D3 — supersedes donor ADR-0027 and the ADR-0026 amendment)**
Email leaves through the platform's outbox → MailKit SMTP sender (Brevo as the relay). Railway
blocked outbound SMTP, which is why the donor grew a Brevo HTTP sender; Render does not, and the
platform's `render.yaml` + CI deploy hooks + version-gated smoke are proven. The single-origin
deployment also removes the donor's cross-origin cookie topology (SameSite=None, CORS from
`CLIENT_URL`). Donor ADR-0027's Supabase pooler / Railway builder gotchas are kept as history only.
*Rationale:* the platform's deploy story is the tested one; fewer moving parts than the donor's
three-host split. If a host that blocks SMTP is ever preferred, an HTTP email provider is a
**platform** feature (upstream), not an app fork.

**ADR-V014 — Critical state transitions use conditional updates + savepoint-nested transactions (inherited). (2026-09-02; from donor ADR-0024)**
Voucher confirm, refund status flips, month get-or-create retry and membership transitions gate
on a conditional `ExecuteUpdateAsync (… WHERE status = current)` and nest unit-of-work scopes; the
platform's `EfUnitOfWork` creates a savepoint when a transaction is already open and rolls back to
it on failure — identical semantics to the donor's. The donor's real-Postgres proofs
(`PendingVoucherConfirmIntegrationTests`, `RefundConcurrencyIntegrationTests`,
`EfTransactionScopeRollbackTests`) port unchanged and now run with RLS enforced.
*Rationale:* the in-memory value read earlier in a request is never the authority; only the
conditional write is.
