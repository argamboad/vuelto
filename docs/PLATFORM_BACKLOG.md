# Platform Backlog — future foundation slices

> Capabilities a SaaS foundation commonly needs that are **not yet built** and **not yet decided**
> into an ADR. This is the parking lot: enough design per item to pick it up later without
> re-discovering the shape. When one is taken on, write its ADR in `DECISIONS.md`, its stories in
> `docs/stories/<epic>.md` (Gherkin), and remove/strike it here.
>
> **For the priority *ordering* of these items (waves + sizes + which deps are already satisfied), see
> `docs/ROADMAP.md`.** This file holds the per-item design detail behind that sequence.
>
> The **three prioritized moves already have ADRs + story files** and are *not* in this list:
> - Billing & subscriptions + entitlements + quotas → **ADR-006**, `docs/stories/billing.md`
> - Outbox / inbox / scheduled jobs → **ADR-007**, `docs/stories/async-jobs.md`
> - Observability + audit log → **ADR-008**, `docs/stories/observability.md`
>
> Everything below depends on nothing here being done first unless noted. Build web-first (ADR-C9),
> vertical slices, TDD (CLAUDE.md golden rules).

## Priority order (suggested)

| # | Item | Epic key | Why it's ranked here | Hard deps |
|---|------|----------|----------------------|-----------|
| 1 | ~~Account & data lifecycle (GDPR)~~ → **✅ DONE** (ADR-011, `stories/gdpr.md`) | `GDPR` | Legal exposure the moment you have EU users; reuses tenant scoping | Audit (ADR-008) for export completeness |
| 2 | ~~RBAC beyond owner/member~~ → **✅ DONE** (ADR-009, `stories/rbac.md`) | `RBAC` | Most B2B asks for an admin tier almost immediately | none |
| 3 | ~~File / blob storage~~ → **✅ DONE** (ADR-010, `stories/files.md`) | `FILES` | Avatars/attachments/exports all block on it | none |
| 4 | ~~MFA / TOTP 2FA~~ → **✅ DONE** (ADR-012, `stories/mfa.md`) | `MFA` | Security baseline; ADR-C15 promised TOTP that was never built | none |
| 5 | ~~In-app notifications~~ → **✅ DONE** (ADR-013, `stories/notify.md`) | `NOTIFY` | Natural follow-on to transactional email | Outbox (ADR-007) ideal |
| 6 | ~~Outbound webhooks (customer-facing)~~ → **✅ DONE** (ADR-016, `stories/hooks.md`) | `HOOKS` | Integration story for *your* customers | Outbox (ADR-007) required |
| 7 | ~~Public API + API keys~~ → **✅ DONE** (ADR-015, `stories/pubapi.md`) | `PUBAPI` | Programmatic access distinct from the user session | RBAC helps |
| 8 | ~~Admin back-office + impersonation~~ → **✅ DONE** (ADR-014, `stories/admin.md`) | `ADMIN` | Support/debugging at scale | Audit (ADR-008) required |
| 9 | Distributed cache (Redis) | `CACHE` | Only once you scale past one node | none (defer hard) |
| 10 | ~~Postgres RLS tenancy backstop~~ (§11) → **✅ DONE** (ADR-020 + addendum) | `RLS` | Was the prod-activation prerequisite; built 2026-07-06 | none |

---

## 1. Account & data lifecycle (GDPR) — `GDPR` → **✅ DONE (ADR-011)**
> **Shipped** — tenant data export (owner-only → signed URL) + account erasure (delete-my-account,
> single-owner-safe, audited). Design in **ADR-011**, slices in `docs/stories/gdpr.md` (GDPR-1/2, merged).
> Sketch below retained for historical context.

**What:** self-serve **data export** ("download my data") and **erasure** (right to be forgotten) at
both user and tenant granularity; a documented data-retention posture.
**Why:** legal requirement with real penalties; also a credible trust/sales feature.
**Sketch / hooks:** the platform already has the machinery — `ITenantDataContributor`
(`HasDataAsync`/`WipeAsync`) is exactly the per-feature enumeration needed. Add an `ExportAsync`
sibling to the contributor so each feature contributes to a tenant export the same way it contributes
to wipe. Erasure of a **user** (vs a whole tenant) needs an owner-reassignment rule (can't erase the
sole owner without dissolving/transferring). **Tension with audit (ADR-008):** erasure vs
legal-hold — export-then-wipe; decide retention windows here.
**Deps:** audit log (ADR-008) so export is complete; the dissolve flow as the wipe backbone.

## 2. RBAC beyond owner/member — `RBAC` → **✅ DONE (ADR-009)**
> **Shipped** — `admin` role + a `Permission`/`RolePermissions` seam (RBAC-1), owner-only role change
> (RBAC-2), admin-aware roster UI (RBAC-3). Design in **ADR-009**, stories + slice plan in
> `docs/stories/rbac.md` (all merged). The sketch below is retained for context; the ADR supersedes it.

**What:** at least owner / **admin** / member, plus a permission-check seam finer than the current
two-role `TenantMembership.Role`.
**Why:** B2B tenants delegate administration; two roles run out fast.
**Resolved (ADR-009):** ordered roles `owner > admin > member`; a `Permission` enum + a static
`RolePermissions` matrix in **Core** as the single source of truth; enforcement via a
`RequirePermission(...)` controller helper **and** a `.RequirePermission(...)` minimal-API filter
(sibling of `RequireEntitlement`, → 403). "Exactly one owner" preserved; role read live from
membership (no JWT claim). Pairs with `ADMIN` and `PUBAPI`.

## 3. File / blob storage — `FILES` → **✅ DONE (ADR-010)**
> **Shipped** — `IFileStorage` (local disk + S3-compatible), tenant-scoped keys, signed download URLs.
> Design in **ADR-010**, slices in `docs/stories/files.md` (FILES-1/2/3, all merged). Sketch retained
> for historical context.

**What:** an `IFileStorage` Core abstraction (put/get/delete/signed-url) with a local-disk dev impl
and an S3-compatible prod impl.
**Why:** avatars, attachments, and the GDPR export artifact all need somewhere to live.
**Resolved (ADR-010):** `IFileStorage` (streaming) mirrors the `IEmailSender` shape; **tenant-scoped
keys** (`{tenantId}/…`) validated server-side (traversal/cross-tenant rejected); **config-gated** impls
(local-disk default, S3-compatible when configured — Stripe-vs-Fake switch); **signed time-limited
download URLs** (native presigned for cloud; `ITimeLimitedDataProtector` token + `GET /api/files/{token}`
for local). Slices FILES-1 (abstraction+local) → FILES-2 (signed download) → FILES-3 (S3).

## 4. MFA / TOTP 2FA — `MFA` → **✅ DONE (ADR-012)**
> **Shipped** — authenticator TOTP enrollment/management + login step-up on **every** sign-in path:
> JSON (MFA-1/2), web OAuth-callback + magic-link redirect (MFA-3), and native (MFA-4). Design in
> **ADR-012**, slices in `docs/stories/mfa.md` (MFA-1..4, merged). Sketch below retained for historical
> context.

**What:** authenticator-app TOTP as a second factor (and recovery codes).
**Why:** security baseline for any serious SaaS. **Note:** ADR-C15 originally claimed TOTP via
`AddDefaultTokenProviders()` — that was **superseded by ADR-002 and never implemented**, so this is a
genuine gap, not a re-do.
**Sketch / hooks:** TOTP secret per user (encrypted at rest via the existing Data Protection setup),
enrollment + verify endpoints on the custom auth stack, a step-up check at login in
[`AuthController`](../src/Api/Controllers/AuthController.cs). Recovery codes are single-use hashes
(reuse the `LoginToken` hashing pattern).
**Deps:** none.

## 5. In-app notifications — `NOTIFY` → **✅ DONE (ADR-013)**
> **Shipped** — per-user notification center + delivery preferences, fan-out (in-app + email) through the
> outbox. Design in **ADR-013**, slices in `docs/stories/notify.md` (NOTIFY-1/2, merged). The header
> **bell-menu UI shipped** (UI-3: list, unread count, mark-read + Settings preference switches). Sketch
> below retained for historical context.

**What:** a per-user notification center + read/unread + per-user delivery preferences (in-app vs
email).
**Why:** the usual next ask after transactional email; preferences are legitimately per-user (the one
sanctioned per-user data carve-out, ADR-C2).
**Sketch / hooks:** `Notification` entity (per-user, **not** tenant-shared — preference-like);
fan-out via the **outbox** (ADR-007) so a domain event can produce both an email and an in-app
notification through one reliable path.
**Deps:** outbox (ADR-007) strongly preferred.

**Remaining follow-up — retention sweep (known gap, 2026-07-09):** notifications are never
auto-reclaimed — reading only stamps `ReadAt`, and the only deletion paths are the user-initiated
delete/clear endpoints (QA-pass addition) and account erasure. A downstream app that notifies heavily
grows the table without bound. Sketch: extend `ExpiredTokenCleanupJob` (or add a sibling scheduled
job) to delete **read** notifications older than N days (config, e.g.
`Notifications:ReadRetentionDays`, null ⇒ keep forever) and optionally cap rows per user. Cheap slice;
slots into the existing scheduler (ADR-007).

## 6. Outbound webhooks (customer-facing) — `HOOKS` → **✅ HOOKS-1 DONE (ADR-016)**
> **Shipped** (config-gated, default off) — `WebhookSubscription` (encrypted signing secret),
> `IWebhookPublisher.PublishAsync` fan-out → one `"webhook"` **outbox** message per sub → HMAC-signed POST
> with retry/dead-letter via the outbox; owner-only `/api/webhooks` (`Permission.ManageWebhooks`) + send-test.
> Design in **ADR-016**, slices in `docs/stories/hooks.md`. **HOOKS-2 ✅ DONE:** tenant-facing delivery log
> (`WebhookDelivery`, per-attempt) + view/replay endpoints. **HOOKS-3 (optional):** a Blazor management UI.
> Sketch below retained for context.

**What:** let *your* tenants subscribe to events from their data (endpoint registration, signed
deliveries, retries, a delivery log).
**Why:** the integration/extensibility story for customers.
**Sketch / hooks:** this is the **outbox** pointed outward — reuse the dispatcher, add HMAC signing,
per-subscription retry/backoff, and a deliveries table. Tenant-scoped subscriptions.
**Deps:** outbox (ADR-007) — required, don't build a second delivery mechanism.

## 7. Public API + API keys — `PUBAPI` → **✅ PUBAPI-1 DONE (ADR-015)**
> **Shipped** (config-gated, default off) — `ApiKey` (hash-only), a second **API-key auth scheme** that
> mints a `tenant_id`-scoped principal, owner-only `/api/apikeys` management (`Permission.ManageApiKeys`),
> a demo `/api/public` group with `.RequireApiScope`, all behind `PublicApi:Enabled` (off ⇒ routes 404).
> Design in **ADR-015**, slices in `docs/stories/pubapi.md`. **PUBAPI-2 ✅ DONE:** per-key rate limiting +
> leak-free public OpenAPI doc. (Still open: key rotation; scope taxonomies.) Sketch below retained for context.

**What:** programmatic access authenticated by tenant-scoped **API keys**, distinct from the
JWT/cookie user session.
**Why:** scripts, integrations, and CI need non-interactive auth.
**Sketch / hooks:** `ApiKey : ITenantScoped` (store only a hash — reuse the `token_hash` pattern from
`RefreshToken`/`TenantInvitation`), an auth handler that resolves the key to a tenant + scopes, and
an OpenAPI/Swagger surface for the public routes. Scope keys to the same entitlement/quota checks as
the UI (BILLING-1/5). Rate-limit per key (extend [`RateLimiting`](../src/Api/Configuration/RateLimiting.cs)).
**Deps:** RBAC/scopes help; quotas (BILLING-5) for per-key limits.

## 8. Admin back-office + impersonation — `ADMIN` → **✅ DONE (ADR-014)**
> **Shipped** — config-gated platform-staff surface: cross-tenant inspection (`EnterTenant`, filter never
> loosened) + short-lived, non-refreshable, audited impersonation. Design in **ADR-014**, slices in
> `docs/stories/admin.md` (ADMIN-1/2, merged). The staff-only `/admin` **console UI shipped** (UI-4:
> tenant list/detail + "Sign in as" with an impersonation banner). **Extended 2026-07-09 (ADR-021)** —
> enumerated, audited admin *writes*: targeted + platform-wide announcements (outbox fan-out) and
> subscription comp/revert (409 when Stripe-backed). Known deferred gap: a platform-level cross-tenant
> audit trail (the broadcast's record is its outbox message). Sketch below retained for historical
> context.

**What:** a super-admin surface (cross-tenant, **platform-staff only**) to inspect tenants and
"sign in as" a user for support.
**Why:** support and debugging at scale.
**Sketch / hooks:** a platform-staff role **outside** the tenant model; cross-tenant reads go through
the audited `QueryAllTenants()` escape hatch (ADR-003) — never loosen the global filter. Impersonation
enters the target tenant via **`ITenantContext.EnterTenant`** (ADR-003 amendment 2026-06-25) so the
session is properly scoped rather than bypassing the filter; mint a scoped, **short-lived, audited**
token and **loudly audit-log** it (ADR-008) — this is the highest-blast-radius feature in the platform;
treat it accordingly.
**Deps:** audit log (ADR-008) — required; RBAC.

## 9. Distributed cache (Redis) — `CACHE`
**What:** `IDistributedCache` backed by Redis for hot reads / cross-node shared state.
**Why:** only once you run more than one API node.
**Sketch / hooks:** introduce behind `IDistributedCache` so call sites don't care; keep keys
tenant-prefixed. **Defer hard** — it breaks the "Postgres-only run cost" property (ADR-C13), so don't
add it until horizontal scale actually forces it. The outbox dispatcher's `SKIP LOCKED` design
(ADR-007) deliberately avoids needing it for multi-node correctness.

---

## 10. Platform as NuGet packages — `PKG`
**What:** ship `Vuelto.Core` / `Vuelto.Infrastructure` / `Vuelto.Shared.Ui` as versioned
NuGet packages so downstream apps consume the platform by package reference instead of clone-and-rebrand.
**Why:** only once several apps exist on different upgrade cadences and clone-merge starts hurting.
**Sketch / hooks:** Core/Infrastructure/Shared.Ui pack as-is (RCL static assets flow via `_content/`);
the hard parts are (1) turning `Vuelto.Api` into a referenced library (`AddApplicationPart` +
extracting `Program.cs` composition into extension methods) and (2) the DbContext/migrations seam —
app-owned context deriving from a platform base, entity configs discovered from app assemblies, per-app
migration history interleaving with platform schema changes. Hosts (Web/Maui), CI, Dockerfile, and docs
can never be packages — a thin scaffold repo remains either way. **Defer hard** until the platform API
surface stabilizes; the ADR-019 naming convention deliberately keeps this door open (a downstream app
that never renamed `Perezosoft.*` swaps project references for package references with zero code churn).

---

## 11. Postgres RLS tenancy backstop — `RLS` → **✅ DONE (ADR-020 + addendum, 2026-07-06)**
> **Shipped** — FORCEd fail-closed policies (`RlsTenancyBackstop` migration), `RlsSessionInterceptor`
> GUC propagation, tag/EnterTenant sanctioning, RLS-enforced integration harness, migration-parity
> gate, two-role prod topology + posture guard (`DEPLOYMENT.md` §7). Implementation learnings in the
> ADR-020 addendum. Sketch below retained for context.

**What:** Postgres **row-level security** on every `ITenantScoped` table as an independent,
DB-level second wall under the ADR-003 global query filter — a query that escapes the EF filter
still returns zero foreign rows.
**Why:** cross-tenant leakage is the worst bug class a multi-tenant SaaS has, and the app-level
wall is enforced by review discipline that downstream apps' vertical slices won't inherit; the
backstop multiplies across every clone. Pre-production is the cheap window — roles are provisioning
config now, a data migration with a rollback plan after live tenants exist.
**Sketch / hooks (full decision record in ADR-020):**
- `FORCE ROW LEVEL SECURITY` + one policy per tenant table (6 real + Notes sample):
  `tenant_id = current_setting('app.tenant_id', true)::uuid`, explicit **null ⇒ deny** (fail-closed).
- EF interceptor issues **`SET LOCAL app.tenant_id`** at transaction start off the existing
  `ITenantContext` seam — transaction-scoped is mandatory (Npgsql pooling leaks connection-scoped
  settings across requests).
- **Two DB roles:** migrator/owner for EF migrations (owners bypass RLS — keep out of runtime) vs a
  runtime role subject to policies. System paths (outbox dispatcher, sweeps, admin, GDPR export,
  webhook `EnterTenant`) get an **explicit** bypass; the call sites are already enumerated behind
  `QueryAllTenants()`/`EnterTenant`.
- **Keystone TDD test:** raw SQL as the runtime role, tenant A set, read tenant B's rows with the
  EF filter out of the picture ⇒ 0 rows. Pin with a B11-style arch/CI gate.
- Bulk of the effort is env plumbing: roles in local compose, the CI E2E stack, Neon
  staging/prod, `.env.example`, `DEPLOYMENT.md`.
**Scope guard:** no per-user RLS, no tenant-timezone quota resets, no policy-based admin scoping.
**Deps:** none. **Size:** ~1–2 slices (2–4 days), plumbing-first.

---

## 12. Explicit system scope (`EnterSystem` / `ISystemScope`) — `SYSSCOPE` → **DECIDED: DEFERRED (v3 T43, 2026-07-27)**
Make tenantless system paths (auth endpoints, background jobs, dispatchers, the webhook before
`EnterTenant`) **declare** themselves the way tenant paths declare via `EnterTenant` — so code that
accidentally touches a tenant-scoped table from a tenantless context **throws loudly** instead of
silently seeing 0 rows (the RLS-2/RLS-8 bug shape).
- **Design:** `ITenantContext.EnterSystem()` returns a scope marking "intentionally no tenant";
  grants **no access** — tenant-table reads inside it still require `QueryAllTenants()`, writes
  `EnterTenant`. Repository access to an `ITenantScoped` set outside ANY declared scope throws.
  Pair with an arch scan enumerating entry points so a missed adoption is caught at build, not 500.
- **Why deferred (the v3 T43 decision, user-approved):** every concrete instance the audit found is
  fixed individually with tests; the silent-no-op class is caught structurally today — the whole
  integration harness runs **RLS-enforced as the runtime role**, so a tenantless read of a tenant
  table surfaces as red tests (how the RLS-2 shape was caught). Retrofitting the declaration across
  the most availability-critical paths (sign-in, refresh) inverts risk/benefit: one missed call
  site turns a working auth endpoint into a production 500. Adopt seam-first in a downstream
  greenfield or a future major refactor, not as a retrofit.
- **Deps:** none. **Size:** ~1 slice + an adoption sweep; the sweep is the risk.

## Not planned (explicitly out unless a need appears)
- **Full-text / vector search** — Postgres FTS covers a lot before reaching for a search engine.
- **Marketing email / CRM** — distinct from transactional `IEmailSender`; an integration, not core.
- **Analytics / product telemetry pipeline** — OBS-2 covers ops telemetry; product analytics is a
  separate (often third-party) concern.
- **i18n expansion** — already shipped (EN/ES; FR/DE/PT scaffolded), see `docs/LOCALIZATION.md`.
