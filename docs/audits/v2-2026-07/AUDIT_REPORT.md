# AUDIT_REPORT.md — v2 Phase 1 (Comprehensive Static Audit)

## SUMMARY

- **Commit SHA:** `84c7ad838c8e7cdc8c9bfb0c4cb939646025040e` (branch `audit/v2-phase1-static`, off `develop`)
- **Scope:** post-v1 platform epics (RBAC, FILES, GDPR, MFA, NOTIFY, ADMIN, PUBAPI, HOOKS) + the BILLING/JOBS/OBS foundation — ADRs 006–016. The pre-epic core was settled by v1 (2026-06-22) and is not re-litigated.
- **v1 regressions found: 0.** All 31 resolved v1 remediations (22 CONF + 5 MITI + doc/gate fixes) verified **Held** at this commit; several were actively strengthened by later work. No Regressed, no Superseded.
- **Fresh findings by severity:** 1 Critical · 5 High · 25 Medium · 32 Low · 1 Info.
- **Rules added:** R1–R24 in `FOUNDATION_RULES.md` (16 machine-enforceable, 8 review-enforced).
- **Conflicts logged:** 0 in `RULE_CONFLICTS.md` (Phase 1 is the first pass; no prior rules to contradict).
- **Foundation-readiness verdict:** **Solid-but-not-ready-to-generate.** The architecture is sound and the v1 guarantees held, but **one Critical (GAP-1, default-config unauthenticated cross-tenant write)** must be fixed before any app is generated, and two Highs (SOLID-1 GDPR erasure gap, TR-1 tests depend on the DELETE-ME slice) undermine core promises the template makes.

> Diagnose-only report. No production code was modified. Where intent is inferred it is marked. Empty subsections are valid. Settled v1 REF-*/intentional items are **not** re-filed (list at the end of §STEP 0).

---

## STEP 0 — v1 remediation status (verified before fresh discovery)

Every resolved v1 finding was checked against the **current code**, not the ticked task box. Result: **31 Held · 0 Regressed · 0 Superseded · 0 Unverified.**

### Standing gates (batch B9) — all present and defending

| Gate | Location | Defends |
|---|---|---|
| Arch test: `IgnoreQueryFilters` banned in `src/Api/Features/**` | `tests/Api.Tests/ArchitectureTests.cs:17-27` | MITI-1 |
| Arch test: every `ITenantScoped` entity has a global query filter | `tests/Api.Tests/ArchitectureTests.cs:29-45` | CONF-1 (read half) — auto-covers all 8 post-audit `ITenantScoped` entities |
| Arch test: no inline Blazor in the web app | `tests/Api.Tests/ArchitectureTests.cs:47-63` | golden rule 3 |
| Warnings-as-errors + nullable, solution-wide | `Directory.Build.props:9-13` | B9-2 |
| Migration-drift test (`MigrateAsync` + `HasPendingModelChanges`) | `tests/Api.Tests/MigrationsTests.cs:35-38` | CONF-11, CONF-18 |
| CI: build (WAE) + Core/Api tests + `ef migrations has-pending-model-changes` | `.github/workflows/ci.yml:22-47` | all of the above on every PR |
| Frozen "Definition of Solid" checklist | `CONTRIBUTING.md:7-39` | B9-4 |

### Remediation status table

| ID | Title | Status | Evidence |
|---|---|---|---|
| CONF-1 (B1-1) | Write-side tenant stamping interceptor | Held | `TenantStampingInterceptor.cs:25-72` (stamp-or-throw, fail closed); wired `AppDbContext.cs:68-72` |
| CONF-2 (B1-2) | `WipeDataAsync` filter-independent | Held | `TenantRepository.cs:121-129` (`IgnoreQueryFilters().Where(TenantId==arg).ExecuteDeleteAsync`) |
| MITI-1 (B1-3) | Named unscoped read surface | Held | `IRepository.cs:30` + `EfRepository.cs:16` (`QueryAllTenants`); gate `ArchitectureTests.cs:17-27` |
| B1-4 | ADR-003 write-safety amendment | Held | `DECISIONS.md:150-156` |
| CONF-5 (B2-1) | Passwordless rate limiting | Held (extended) | `RateLimiting.cs:32-47`; now also on `mfa/verify` (`AuthController.cs:512`) |
| CONF-5 (B2-2) | Cumulative OTP lockout | Held | `PasswordlessService.cs:104-107`; `LoginTokenRepository.cs:33,49` preserves `AttemptCount` |
| CONF-6 (B2-3) | Enumeration-neutral OTP verify | Held | `PasswordlessService.cs:18-25` (`invalid_code` collapse) |
| MITI-3 (B3-1) | `email_verified` fails closed | Held | `ClaimsExtractor.cs:42-43` |
| CONF-4 (B3-2) | Refresh-token reuse detection | Held | `RefreshTokenService.cs:96-116`; revoke-all `AuthController.cs:161-169` |
| CONF-8 (B3-3) | `TimeProvider` in `JwtTokenService` | Held | `JwtTokenService.cs:33`; zero `DateTime.UtcNow` |
| CONF-7 (B3-4) | Single `TokenValidationParameters` source | Held | `JwtValidation.cs:14` consumed by `Program.cs:194` + `JwtTokenService.cs:119` |
| CONF-9 (B4-1) | Notes slice injected clock | Held | `NotesHandler.cs:17,33` |
| CONF-3 (B4-2) | Shared scaffolding + single authz policy | Held (wider) | `FeatureEndpointExtensions.cs:14-15`; PUBAPI/HOOKS also use `AuthPolicies.TenantApi` |
| CONF-10 (B4-3) | `SingleUseCacheToken<T>` extraction | Held | `SingleUseCacheToken.cs:13` |
| CONF-11 (B5-1) | Migration/model drift | Held | `InitialCreate.cs:54-55` + gate `MigrationsTests.cs:35-38` |
| CONF-12 (B5-2) | Set-based revoke/invalidate | Held | `RefreshTokenRepository.cs:50`, `LoginTokenRepository.cs:49` |
| CONF-13 (B5-3) | SMTP resilience | Held | `SmtpEmailSender.cs:37,47-54` |
| MITI-2 (B5-4) | UnitOfWork boundary documented | Held | `IUnitOfWork.cs:12-22` |
| CONF-14 (B5-5) | Unique token-hash indexes | Held (propagated) | `AppDbContext.cs:112,148`; new `ApiKey.KeyHash` unique `:245` |
| CONF-16 (B6-1) | `Core.Tests` populated | Held | `LoginTokenTests`, `RolePermissionsTests`, `WebhookSignatureTests` |
| CONF-18 (B6-2) | Migrations exercised in a test | Held | `MigrationsTests.cs:35-38` |
| CONF-19 (B6-3) | Structural per-test isolation | Held (fully adopted) | `PostgresTestBase.cs:15`; every epic's tests inherit it |
| CONF-17 (B6-4) | `PLAYWRIGHT_BASE_URL` override | Held | `E2ETestBase.cs:21-23`; `playwright.runsettings:15` |
| CONF-15 (B7-1) | Destructive confirm fails closed | Held (wider) | `JsConfirm.cs:15-16`; adopted by `AdminConsole.razor:178` |
| MITI-4 (B7-2) | Don't swallow cancellation | Held | `AuthController.cs:191,227` |
| MITI-5 (B7-3) | No double reload on locale mismatch | Held | `MainLayout.razor:53-67` (the `:81` forceLoad is the new ADMIN-2 stop-impersonation path) |
| CONF-20 (B8-1) | `SCHEMA.sql` reference removed | Held | repo-wide grep: 0 hits |
| CONF-21 (B8-2) | `magic_link` → `magic-link` | Held | repo-wide grep: 0 `magic_link` |
| CONF-22 (B8-3) | Email `FromName` consistency | Held | `appsettings.json:54` = `"Perezosoft"` |
| B8-4 | Carry-over doc nits | Held | 0 `saas-template` hits |
| B9-1…B9-4 | Enforcement gates | Held | gates table above |

**Two scope boundaries** to carry into fresh discovery (not v1 regressions): (1) the `IgnoreQueryFilters` arch-ban covers only `Features/**` by design — platform code (API-key pre-auth lookup, dissolve contributors, lapse sweep) legitimately queries unscoped outside that path; (2) `WebhookDelivery` and `Notification` are deliberately **not** `ITenantScoped`, so their read paths are convention-filtered and sit outside the arch-test gate — see ARCH-2, GAP-2/GAP-3.

**Settled v1 §5 items — NOT re-filed:** REF-1..13 (refuted or judged-immaterial: controllers-read-via-repository, JwtSettings-built-twice [now DEBT-1], email-format dup, missing `AsNoTracking`, JWT re-parse in WASM, role literals in Razor, SQL-backdated expiry tests, `saas-template` naming). MITI-1..5 all fixed and Held above.

---

## Section 0 — Template-readiness

**Verified sound (no finding):** coupling direction clean (Core has zero project deps; only `Program.cs` references `Features.*` from outside `Features/`); cross-cutting inheritance mostly automatic (a new slice inherits tenancy, authz filters, log enrichment, audit staging, outbox, scheduled jobs, quotas, files, dissolve/export via `ITenantDataContributor`); ADMIN inspects tenants generically with no app-type knowledge; app-flavored Core constants (`PlanCatalog`, `WebhookEvents.Ping`) are marked EXAMPLE.

**TR-1 · High — Platform test suite is load-bearing on the DELETE-ME Notes slice.**
`RepositoryScopingTests.cs:25`, `TenantStampingInterceptorTests.cs:26`, `EnterTenantScopingTests.cs:30`, `Outbox/OutboxProcessorTests.cs:30`, `Gdpr/TenantExportTests.cs:127`, `Gdpr/AccountErasureTests.cs:109`, `Infrastructure/PostgresFixture.cs:62` all use `Note`/`NotesDataContributor` as the tenant-scoped fixture entity. `WAYS_OF_WORKING.md:63-64` tells the downstream dev to "delete it when you ship your first real feature" — doing so breaks the tests guarding the template's single most important invariant (tenant isolation), plus GDPR and outbox tests, on day one. No deletion checklist exists (Core entity, `AppDbContext.cs:62,281-286`, a drop-table migration reversing `20260620015655_AddNotesSample`, `Program.cs:164-167,285-286`, the fixture TRUNCATE list). *Inferred oversight, not a decision.* **Fix:** give the platform tests a test-only `ITenantScoped` fixture entity (harness-owned `TestWidget`), and add a "deleting the Notes sample" checklist to WAYS_OF_WORKING.

**TR-2 · Medium — The "zero central edits" slice contract is overstated; real contract is ~5 touchpoints, partially documented.**
ADR-004 (`DECISIONS.md:188-189`) claims slices are addable/deletable "without touching central code." Actual per-slice central edits: (1) `Core/Entities/<X>.cs` (additive), (2) `AppDbContext.cs` DbSet + `OnModelCreating`, (3) EF migration + snapshot churn, (4) `Program.cs` DI + `MapX()`, (5) `PostgresFixture.cs:62` TRUNCATE list (TR-3), plus nav + `AppStrings.resx` for the UI side. `WAYS_OF_WORKING.md:47-64` omits steps 2, 3, 5 and the UI side. **Fix:** full mechanical "add a slice" checklist; soften ADR-004; optionally assembly-scan `IEntityTypeConfiguration<>`/endpoint modules to shrink the list.

**TR-3 · Medium — Test fixture holds a hidden, hand-maintained table registry.**
`PostgresFixture.cs:62` TRUNCATEs a hardcoded table list; a slice that forgets the line gets silent cross-test state leakage (flaky failures). **Fix:** derive the list from `AppDbContext.Model.GetEntityTypes()` at fixture init.

**TR-4 · Low — Slice-facing extension points that live as Core registries (deliberate, but they are core edits).**
Adding a permission = edit `Core/Authorization/Permission.cs` + `RolePermissions.cs`; a plan key = `PlanCatalog.cs`; a webhook event = `WebhookEvents.Known` in `WebhookSubscription.cs:42-47`. Documented as single-sources-of-truth (fail-closed centralization — defensible intent). Flagged as merge hotspots contradicting the zero-core-edit ideal; `WebhookEvents` oddly lives inside an entity file. **Fix:** list the three registries in the slice checklist; move `WebhookEvents` to its own file.

**TR-5 · Low — i18n composability is central-file-only, and API-emitted messages aren't localized.**
All UI strings in one `AppStrings.resx` (~182 entries) — no per-feature convention, parallel slices collide. Slice-facing endpoint filters return hardcoded English (`PermissionEndpointExtensions.cs:27-31`, `EntitlementEndpointExtensions.cs:28-34`, `NotesEndpoints.cs` BadRequest). **Fix:** document a per-feature resx pattern; localize or ADR-exempt API error strings.

**TR-6 · Low — The reference slice is API-only; the UI half of the vertical-slice contract has no exemplar.**
`Features/Notes/` has no `Shared.Ui` page/nav/RCL component/resx/E2E, though WAYS_OF_WORKING defines vertical as API→Core→Infrastructure→Shared.Ui→Web. **Fix:** add a minimal DELETE-ME `Notes.razor` + nav + resx, or explicitly scope the sample as API-only.

**TR-7 · Low — Platform surfaces live inside the app-slice folder.**
`Features/ApiKeyEndpoints.cs` (PUBAPI) and `Features/WebhookEndpoints.cs` (HOOKS) are platform chassis but sit loose in `Features/` (the "deletable app features" folder) and inside the Features arch-test blast radius. Overlaps DEBT-6. **Fix:** move to `src/Api/Endpoints/`; amend ADR-004 for the config-gated minimal-API exception.

**TR-8 · Low — Contradictory doctrine on the cross-tenant escape hatch.**
ADR-014 (`DECISIONS.md:677-679`) calls `QueryAllTenants()` "the audited hatch feature slices are **forbidden** from using," while `IRepository.cs:23-30` and `NotesDataContributor` sanction slices using it in contributors, and the arch test bans only raw `IgnoreQueryFilters`. "Audited" is a misnomer — it's greppable, not `IAuditLog`-audited. Overlaps DOC-13/DOC-17. **Fix:** reword ADR-014 ("forbidden in request-path slice code; required in contributors"); rename to "deliberately-named/greppable."

**TR-9 · Medium (flag-only) — Generation-time floor: CI-enforced vs doc-only.**
Enforced by inherited gates: WAE build; Core+Api tests incl. the three arch tests; EF migration drift. **Doc-only (lost by no-upstream clones):** TDD + Gherkin/PR conventions; E2E tests (never run in CI); "`IEmailSender` is the only way to send email" (no MailKit ban); "secrets never in appsettings" (no secret-scan step); no cross-feature-folder references; no scattered `role ==` checks; features-use-minimal-APIs; `ITenantDataContributor.ExportKey` uniqueness; the TRUNCATE-list and slice-checklist steps. (Full hardening is a separate pass; flagged only.)

---

## Section 1 — Architecture

**ARCH-1 · Low — Slice-facing seams split inconsistently between `Core.Abstractions` and `Api.Services`.**
Most seams live in Core (`IOutbox`, `IAuditLog`, `IQuotaService`, `IEntitlementService`, `IPermissionService`, `IFileStorage`, `ITenantDataContributor`), but `INotificationService` (`NotificationService.cs`) and `IWebhookPublisher` (`WebhookService.cs:128`) live in Api. Means an Infrastructure `IOutboxHandler` can never notify or publish a webhook, and "where does a platform seam go" is undiscoverable. Overlaps SOLID-4. **Fix:** lift the two interfaces (+ small param records) to `Core.Abstractions`, implementations stay in Api.

**ARCH-2 · Low — Two by-convention tenancy exceptions ride on the structural guarantee's reputation.**
`WebhookDelivery` (`AppDbContext.cs:53-55`) and `Notification` are deliberately not `ITenantScoped`; read paths hand-filter (`WebhookService.cs:88-104` `TenantId==currentTenant`, `NotificationService` by `UserId`) — correct today but invisible to the every-entity-has-a-filter arch test. **Fix:** arch test asserting every entity with a `TenantId` property either implements `ITenantScoped` or is on an explicit allowlist.

**ARCH-3 · Low — ADR-004 text has drifted from the implemented hybrid (docs-vs-code).**
"platform stays controllers" vs PUBAPI/HOOKS minimal APIs (TR-7); "without touching central code" vs the 5-touchpoint contract (TR-2); "slices forbidden from the hatch" vs contributor usage (TR-8). Code is coherent; the ADR record lags. Fix by amendment, not code.

**No circular dependencies; no Core→Api / Infrastructure→Api references; no core code anticipating a specific downstream feature beyond marked examples. Coupling-inversion findings — none.**

---

## Section 2 — Documentation ↔ Code Sync

**Verified positively:** every ADR-claimed symbol/entity/endpoint exists; `.env.example` documents every config key incl. `Admin__StaffEmails`/`PublicApi__Enabled`/`Webhooks__Enabled`; package versions match doc claims; all 12 story-file status headers match shipped code; QA PDFs regenerated in the plan's commit. Drift concentrates in ROADMAP, PLATFORM_BACKLOG, DATA_MODEL, FEATURES, and un-amended ADRs.

**DOC-10 · High — `DATA_MODEL.md` (the mandated schema pre-read) is missing four live entities.**
`DATA_MODEL.md:115-146` omits `ApiKey`, `WebhookSubscription`, `WebhookDelivery`, `UsageCounter` (all with migrations); line 144 still calls `ApiKey`/webhooks "future," and the "Pinned model extensions (future, not built)" header contains four "✅ BUILT" entries. **Fix:** document the four entities; retitle/split built-vs-future.

**DOC-17 · Medium — CLAUDE.md golden rule 1 + `DATA_MODEL.md:13-14` tell you to opt out with `IgnoreQueryFilters()` — which fails CI in feature code.**
Both say cross-tenant lookups "opt out with `IgnoreQueryFilters()`"; the sanctioned hatch is `IRepository<T>.QueryAllTenants()`/`EnterTenant`, and `IgnoreQueryFilters` in `Features/**` fails `ArchitectureTests` (B9-1). Following the auto-loaded manual literally breaks the build. **Fix:** name `QueryAllTenants()`/`EnterTenant` in both docs.

**DOC-18 · Medium — WAYS_OF_WORKING slice recipe drifted and won't compile as written.**
`WAYS_OF_WORKING.md:47-57`: (a) says endpoints use `MapGroup(...).RequireAuthorization(...)` but the pinned convention is `MapTenantFeatureGroup(...)`; (b) the contributor bullet mentions only dissolve — `ITenantDataContributor` now requires `ExportKey` + `ExportAsync` (GDPR-1), so a slice built from the recipe won't compile; (c) no mention of `.RequirePermission`/`.RequireEntitlement`. **Fix:** update the three bullets.

**DOC-22 · Medium — The UI epic has no story file.**
UI-1..4 (GDPR export/delete UI, MFA, notifications, admin console — commits `1157ecb`, `bf61aaf`, `c867f01`, `65ea677`) have no `docs/stories/` file and no ROADMAP entry, violating WoW ("story file before an epic"); QA_TEST_PLAN §2 cites "UI-2/UI-3/UI-4" as if defined. **Fix:** add `docs/stories/ui.md` (retrospective) or a dated ROADMAP note.

**DOC-1 · Medium — `ROADMAP.md:76-82` "Recommended next" is wholesale stale** ("nine epics… PUBAPI+HOOKS parked… remaining work is UI-only") — 11 epics shipped, all listed UI exists. **Fix:** rewrite to "all 11 epics + UI pass complete; open: HOOKS-3 UI, key rotation, CACHE."

**DOC-2 · Medium — `ROADMAP.md:38`** MFA row says "MFA-1/2; redirect step-up = follow-up"; MFA-3/4 shipped (`AuthController.cs:121-123`). **Fix:** "MFA-1..4; enforced on every sign-in path."

**DOC-3 · Medium — `ROADMAP.md:44,46`** show only PUBAPI-1/HOOKS-1 DONE; PUBAPI-2 + HOOKS-2 shipped. **Fix:** mark both epics complete (1–2).

**DOC-5 · Medium — `PLATFORM_BACKLOG.md:24,28-29`** RBAC row still "being built"; HOOKS/PUBAPI rows unmarked though sections below say shipped. **Fix:** mark RBAC ✅, strike rows 6–7.

**DOC-7 · Medium — `FEATURES.md:71-72,125`** twice states "TOTP/authenticator apps are **not** implemented" — contradicts the whole MFA epic. **Fix:** delete both claims; reference the MFA flow.

**DOC-8 · Medium — `FEATURES.md:25-72`** sign-in flows omit the MFA step-up that now intercepts every path. **Fix:** add the step-up step.

**DOC-11 · Medium — `DATA_MODEL.md:117-118`** says wire new entities into `ITenantRepository.HasDataAsync`/`WipeDataAsync` — contradicts ADR-004/WoW:59 ("must NOT edit a central wipe method"); the hook is a registered `ITenantDataContributor`. **Fix:** point at `ITenantDataContributor`.

**DOC-13 · Medium — ADR-014 pt 2 vs `AdminController.cs:67,118`** says cross-tenant reads go through `QueryAllTenants()` "only," but the impl uses `EnterTenant`; `docs/stories/admin.md:71` Gherkin still asserts `QueryAllTenants()`. **Fix:** dated ADR-014 amendment + correct the scenario.

**DOC-16 · Medium — CLAUDE.md doc-map PUBAPI row** lists only PUBAPI-1 while HOOKS lists both. **Fix:** add PUBAPI-2.

**Low DOC findings:** DOC-4 (`ROADMAP.md:62-63` B9-1 shown open though it exists), DOC-6 (`PLATFORM_BACKLOG.md` shipped-notes lag MFA-3/4 + UI), DOC-9 (`FEATURES.md:83-98` owner-only invite; admins now hold `ManageMembers`, plus 402 seat-limit), DOC-12 (`DATA_MODEL.md:119-125` Subscription "nullable until BILLING-2" + undocumented `LapseNotifiedAt`), DOC-14 (ADR-016 no HOOKS-2 addendum), DOC-15 (ADR-015 no PUBAPI-2 addendum), DOC-19 (`QA_TEST_PLAN.md:108-115` "no client UI" list still includes GDPR export + erasure, which its own cases test), DOC-20 (ADR-008(b) claims a `SaveChanges` audit interceptor; writes are explicit-only), DOC-21 (ADR-006 lists stripe-mock as test stack; deferred), DOC-23 (`TECH_STACK.md:107-143` missing `Otp.NET`/`AWSSDK.S3`/`Swashbuckle`), DOC-24 (`HouseholdInvitationsController.cs:30,56,70,92` 403 strings say "owner" but the gate is `ManageMembers` which admins hold).

---

## Section 3 — Contradictions

**CON-1 · Medium — Webhook URL validation accepts `http`, but the error message and epic contract promise `https`.**
`WebhookService.cs:107-108` `IsValidUrl` returns true for http OR https, yet the BadRequest (`WebhookEndpoints.cs:38`) says "A valid **https** URL … required" and the epic is a signed delivery. A tenant can register a plain-`http` endpoint, so the HMAC-signed payload travels cleartext. Compounds GAP-2. **Fix:** restrict to https (http only in Development), or align message/docs — make code + message + docs agree.

**CON-2 · Low — `Authentication:Microsoft:Tenant` is read but undocumented (config drift).**
`ServiceCollectionExtensions.cs:181` reads it (default `"consumers"`); absent from `.env.example` and `appsettings.json`. **Fix:** document `Authentication__Microsoft__Tenant`.

**CON-3 · Low — Two idioms for "JWT-only" authorization across controllers.**
`TenantApiControllerBase.cs:18` uses `[Authorize(AuthPolicies.TenantApi)]`; `AdminApiControllerBase.cs:16` and `NotificationsController.cs:18` use `[Authorize(AuthenticationSchemes = JwtBearerDefaults...)]`. Same intent, two patterns; the named policy (CONF-3) was meant to unify this. **Fix:** route the scheme-pinned controllers through the named policy.

*(Error-response shape is consistent — controllers and endpoints both emit `{error, message}`. Contributor tenancy handling is consistent — all use `QueryAllTenants().Where(TenantId==tenantId)`. No contradiction filed for those.)*

---

## Section 4 — Gaps (security-first)

**GAP-1 · Critical (default-config) — Anonymous, always-mapped billing webhook + default fake provider = unauthenticated cross-tenant subscription write. (Verified by parent audit.)**
`ServiceCollectionExtensions.cs:96-99` registers `FakeBillingProvider` whenever `Billing:Stripe:SecretKey` is unset — the template default. `FakeBillingProvider.ParseWebhookEvent` (`FakeBillingProvider.cs:44-52`) treats header `Stripe-Signature: valid` as authentic and deserializes the POST body into a `BillingWebhookEvent`. `BillingWebhookController` (`BillingWebhookController.cs:14-20`) is `[AllowAnonymous]` and always mapped via `MapControllers`. `BillingWebhookHandler.HandleAsync` (`BillingWebhookHandler.cs:52-83`) does `EnterTenant(evt.TenantId)` from the attacker-controlled body and upserts an active `Subscription`. So against a non-Development deployment that enabled billing UI but hasn't configured Stripe, an unauthenticated `POST /api/billing/webhook` with `Stripe-Signature: valid` and `{"TenantId":"<any-guid>","Status":"active","PlanKey":"pro"}` grants/rewrites **any tenant's** subscription — unauthenticated cross-tenant entitlement escalation, the exact class the tenancy model exists to prevent, reachable purely by default/misconfiguration.
**Fix:** refuse to register `FakeBillingProvider` outside `Development` (throw at startup if no Stripe key in Production), and/or don't map `BillingWebhookController` when the fake provider is active; add signature-rejection logging (GAP-5). **This is the must-fix before generating any app.**

**GAP-2 · High — HOOKS outbound webhooks have no SSRF protection on the tenant-supplied URL.**
Only check is scheme + absolute-URI (`WebhookService.cs:107-108`). A tenant owner can register (or synchronously "send-test", `WebhookEndpoints.cs:46-73`) a webhook at `http://169.254.169.254/…` (cloud metadata), `http://localhost:<port>`, or any RFC-1918 host, and the server POSTs to it — the sync test returns status/error, an internal port-scanner / metadata-exfil primitive. Config-gated off limits blast radius, but any tenant owner can exploit it once HOOKS is enabled. **Fix:** resolve the host and reject loopback/link-local/RFC-1918/ULA/metadata (re-check after DNS to defeat rebinding); https-only; apply in both the sync test and async `WebhookSender.cs:20-32`.

**GAP-3 · Low — Webhook test/delivery surface raw exception text to the tenant.**
`WebhookEndpoints.cs:69-72` returns `error = ex.Message`; `WebhookOutboxHandler.cs:49,65` stores raw `transportError`, readable by the tenant — leaks internal DNS/connection detail, compounding GAP-2. **Fix:** store/return a generic failure string; keep detail in server logs.

**GAP-4 · Low — `S3FileStorage.GetDownloadUrlAsync` uses ambient `DateTime.UtcNow` instead of the injected clock.**
`S3FileStorage.cs:81` — every sibling takes `TimeProvider` (the CONF-8 convention); presigned-URL expiry is untestable and inconsistent. **Fix:** inject `TimeProvider`.

**GAP-5 · Low — No logging/observability on rejected billing-webhook signatures.**
`BillingWebhookController.cs:26-29` / `BillingWebhookHandler.cs:33-36` return bare 400, no log/audit — a forged-webhook probe (GAP-1) is invisible. **Fix:** log a warning (+ audit/metric) on `InvalidSignature` incl. source IP.

*(No TODO/FIXME/dead code in the new surfaces. MFA-secret encryption, API-key hash-only storage, webhook-secret encryption all confirmed correct. `PublicApi:Enabled`/`Webhooks:Enabled`/`Admin:StaffEmails` default OFF/empty and fail closed. Tenancy preserved across ADMIN `EnterTenant`, GDPR export/erasure, PUBAPI API-key principal, HOOKS fan-out — each scopes by explicit `tenantId` or enters the tenant so the global filter applies.)*

---

## Section 5 — Debt & Code Smells

**DEBT-1 · Medium — Config binding solved four different ways** (`Program.cs:87-91` hand-rolled `new JwtSettings(config)`, `:142` `Configure<PlatformAdminSettings>`, `:199-211` manual `new` + `Bind()` + `AddSingleton`, `ServiceCollectionExtensions.cs:50,95,105` `Configure<T>`), and `JwtSettings` is constructed **twice** (`Program.cs:87` and `:189`). Every downstream config-bound feature has four precedents. **Fix:** one pattern (typed options `.BindConfiguration().ValidateOnStart()`); reuse the line-87 `JwtSettings`.

**DEBT-2 · Medium — "Current user id from claims" parsed in 6 places** (`AuthController.cs:657`, `TenantApiControllerBase.cs:30`, `AdminApiControllerBase.cs:21`, `NotificationsController.cs:21`, `ApiKeyEndpoints.cs:71`, `WebhookEndpoints.cs:86`), three shapes. **Fix:** one `ClaimsPrincipalExtensions.GetUserId()`; delete the copies.

**DEBT-3 · Medium — `Program.cs` is the per-epic registration dumping ground** (`:96-213`, ~35 loose `AddScoped`, PUBAPI gate spans `:199-205,256-257,290-304`). Merge-conflict magnet; deleting an epic means hunting scattered lines. **Fix:** per-epic `Add*()`/`Map*()` extension pairs.

**DEBT-4 · Medium — `AppDbContext.OnModelCreating` is one 230-line method every epic edits** (`:74-299`, 22 entities inline). **Fix:** `IEntityTypeConfiguration<T>` classes + `ApplyConfigurationsFromAssembly`, keeping the reflection tenant-filter loop (`:292-298`).

**DEBT-5 · Medium — Two RBAC enforcement mechanics with drifting 403 payloads.** Minimal-API `.RequirePermission(...)` (`PermissionEndpointExtensions.cs:18-32`) emits `{error, permission, message}`; controllers repeat a 4–6-line manual preamble (`HouseholdController.cs` ×6, `BillingController.cs:25-29,43-47`) emitting `{error, message}`. A copied action can silently drop the check. **Fix:** a `[RequireTenantPermission(...)]` filter sharing the JSON writer.

**DEBT-6 · Medium — PUBAPI/HOOKS break the documented "platform = controllers, features = minimal APIs" rule** and hand-roll `MapGroup(...).RequireAuthorization(AuthPolicies.TenantApi)` — the exact re-spelling CONF-3 forbids (`ApiKeyEndpoints.cs:23-24`, `WebhookEndpoints.cs:22-23`). Template now shows three route-registration styles. Overlaps TR-7. **Fix:** move into `Features/ApiKeys/`+`Features/Webhooks/` shaped like Notes and route through `MapTenantFeatureGroup`, or promote to controllers; record in WAYS_OF_WORKING/ADR-004.

**DEBT-7 · Medium — Tenant dissolve orchestration duplicated.** `TenantService.LeaveAsync` (`TenantService.cs:203-218`) and `AccountErasureService.EraseAsync` (`AccountErasureService.cs:70-79`) both re-implement begin-tx→foreach-contributor→wipe→re-home→commit with the invariants re-derived. **Fix:** extract `TenantDissolutionService.DissolveAsync(...)`.

**DEBT-8 · Low — Notification emails bypass the branded template system.** `NotificationService.cs:73` sends `$"<p>{HtmlEncode(body)}</p>"` unbranded/unlocalized while everything else goes through `BrandedEmail.cs` — the exact miss REBRANDING.md warns about. **Fix:** `BrandedEmail.Notification(title, body, culture)` using the recipient's locale.

**DEBT-9 · Low — File/type naming and DTO-placement drift.** `WebhookService.cs` contains no `WebhookService` type; `AuthController.cs:664-675` declares 5 DTOs at file bottom while siblings live in `Api/Models/` and PUBAPI/HOOKS inline theirs — three DTO homes. **Fix:** rename/split; state the rule in WAYS_OF_WORKING.

**DEBT-10 · Low — Supported-locale list duplicated** (`AuthController.cs:45` vs `LanguageSwitcher.razor:23` vs resx); server already advertises fr/de/pt the docs call deferred. **Fix:** one shared constant.

**DEBT-11 · Low — `HouseholdController` Get/Rename duplicate the `TenantResponse` assembly block** (`:38-45` vs `:65-73`). **Fix:** private `BuildTenantResponseAsync`.

**DEBT-12 · Info — Temporal coupling, contained.** Middleware order (`Program.cs:272-277`) and migrate-on-startup (`:243-248`) are ordering-sensitive but well-commented; the interceptor statics are genuinely stateless. No action; listed so a later audit doesn't re-litigate.

**Top complexity hotspots:** `AuthController.cs` (675 lines/18 deps — SOLID-2); `Shared.Ui/Auth/AuthService.cs` (427 lines, client-side god object, watch); `TenantInvitationService.AcceptAsync` (`:161-224`, 7 outcomes); `AppDbContext` (`:74-299`, size); `Program.cs` (310-line composition root); the twin dissolve flows (DEBT-7); `OutboxProcessor.cs:41-101`.

---

## Section 6 — CLEAN & SOLID

**SOLID-1 · High — `AccountErasureService` violates OCP; the one place the contributor pattern was needed and skipped.**
`AccountErasureService.cs:34-46,90-97`: 12 ctor deps, hard-coded deletes for every epic's per-user tables (refresh tokens, logins, login tokens, MFA rows, recovery codes, notifications, prefs). The platform solved the identical tenant-side problem with `ITenantDataContributor` — but there is no per-user equivalent, so **any future epic or downstream slice that stores user-keyed PII silently escapes GDPR erasure** unless its author edits this class. Highest pattern-copying cost in the review. **Fix:** `IUserDataContributor { WipeAsync(userId, …) }` in `Core.Abstractions`; move MFA/NOTIFY deletes into contributors; `AccountErasureService` keeps identity-core rows + the loop. Machine-enforceable adjunct (R-below).

**SOLID-2 · High — `AuthController` is a god controller (SRP).**
675 lines, 18 ctor deps, ~20 endpoints across six concerns (web OAuth, token lifecycle, profile+GDPR-delete, MFA mgmt, locale, account linking, passwordless, native OAuth). Any auth test drags 18 mocks. **Fix:** split along the existing `── section ──` comments into `MfaController`, `AccountController`, `NativeAuthController`; routes unchanged.

**SOLID-3 · Medium — Fail-open normalize defaults: unknown scopes/event-types silently grant everything.**
`ApiKeyService.NormalizeScopes` (`ApiKeyService.cs:95-103`) and `WebhookSubscriptionService.NormalizeEventTypes` (`WebhookService.cs:111-119`) filter to known values, and **if nothing survives, return ALL**. `POST /api/apikeys {"scopes":["raed"]}` (typo) mints a full-access key with no error. "Absent ⇒ all" is fine; "all-invalid ⇒ all" is a hidden security-relevant side effect duplicated in both services. **Fix:** distinguish `null` (default all) from "provided but nothing valid" (reject → 400); unit-test it. *(Security-adjacent — Phase 2/3 may promote.)*

**SOLID-4 · Medium — Seam-interface placement is inconsistent (DIP/discoverability).** Overlaps ARCH-1. `INotificationService`, `IWebhookPublisher`, `IApiKeyService`, `IBillingNotifier` live in `Api/Services` while peer seams live in `Core/Abstractions`; `IWebhookPublisher` couples to Infrastructure internals (`WebhookOutboxHandler.MessageType`, `WebhookOutboxPayload`). **Fix:** move feature-facing interfaces to `Core.Abstractions`; define the payload/message-type next to the contract; write the rule into WAYS_OF_WORKING.

**SOLID-5 · Low — `MfaLoginService.CompleteOrChallengeAsync` tuple + overloaded `provider` string.** `(AccessSession? Session, string? Challenge)` with an unstated exactly-one invariant forces `session!` at 4 call sites; the `provider` param carries both OAuth names and `LoginTokenPurpose` constants, so `RefreshToken.Provider` means "provider-or-purpose." **Fix:** a small result type; rename/clarify.

**SOLID-6 · Low — Webhook test-send does domain work inline in the endpoint lambda** (`WebhookEndpoints.cs:46-73` unprotects the secret, builds payload, maps exceptions) while siblings delegate to the service; payload shape duplicates `WebhookPublisher`'s (`WebhookService.cs:152-158`). **Fix:** `SendTestAsync(id)`; shared `BuildEventBody`.

**SOLID-7 · Low — `IErrorResponseFactory` is a needless abstraction half the API bypasses.** `ErrorResponseFactory.cs:8` wraps `new ErrorResponse(...)` while minimal-API code hand-rolls anonymous `{error, message}` (`NotesEndpoints.cs:22`, `ApiKeyEndpoints.cs:87-92`, etc.) — same shape by luck. **Fix:** drop the interface; static helpers on `ErrorResponse` used by both controllers and filters.

**SOLID-8 · Low — `ExternalScheme` constant lives on a DI-extensions class.** `AuthController` imports `Infrastructure.ServiceCollectionExtensions.ExternalScheme` (`:75,99,115,556,567`). **Fix:** move to a tiny `AuthSchemes` class.

**Reference-slice wart:** `NotesHandler.CreateAsync` (`Features/Notes/NotesHandler.cs:30-31`) returns `null` for both "no tenant on token" and "missing title," both mapped to 400 "A note title is required" (`NotesEndpoints.cs:21-23`) — a misleading error in the file every slice copies. **Fix:** a small result enum.

---

## Prioritized top-10

1. **GAP-1 (Critical)** — default fake billing provider + anonymous always-mapped webhook → unauthenticated cross-tenant subscription write. Fix before generating any app.
2. **SOLID-1 (High)** — `AccountErasureService` OCP gap: no `IUserDataContributor`, so future per-user PII silently escapes GDPR erasure.
3. **GAP-2 (High)** — SSRF on tenant-supplied webhook URLs (metadata/RFC-1918), incl. a synchronous test probe.
4. **TR-1 (High)** — platform tenancy/GDPR/outbox tests depend on the DELETE-ME Notes slice; deleting it per the docs breaks the isolation guard.
5. **DOC-10 (High)** — `DATA_MODEL.md` (mandated schema pre-read) missing four live entities.
6. **DOC-17 (Medium, correctness-of-manual)** — CLAUDE.md tells devs to use `IgnoreQueryFilters()`, which fails CI in feature code.
7. **SOLID-2 (High)** — `AuthController` god class (675 lines / 18 deps).
8. **SOLID-3 (Medium, security-adjacent)** — all-invalid scopes/events fail open to full access (PUBAPI + HOOKS).
9. **DEBT-1/3/4 (Medium)** — config-binding four ways + `Program.cs`/`AppDbContext` as per-epic dumping grounds; the patterns every slice copies.
10. **DOC-18/DOC-22 (Medium)** — slice recipe won't compile as written; the UI epic has no story file.

## Foundation-readiness verdict

**Solid-but-not-ready-to-generate.** The post-epic architecture is genuinely clean — coupling direction holds, cross-cutting concerns are inherited automatically, all 31 v1 guarantees survived, no Regressed items. But the template ships **one Critical default-config tenancy breach (GAP-1)** and two Highs that break promises it makes about itself — GDPR erasure completeness (SOLID-1) and the "delete the sample slice" instruction that breaks the isolation tests (TR-1). Remediate the Critical + the five Highs, then Phase 4's adversarial slice pass can confirm generate-readiness.

## Unknowns / needs human decision

- **GAP-1 fix shape:** fail-fast at startup (throw if no Stripe key outside Development) vs. don't-map-the-controller when fake — the first is safer but changes the "boots with zero setup" promise for non-Dev. Human call on the intended non-Dev-without-Stripe posture.
- **TR-7/DEBT-6:** are PUBAPI/HOOKS meant to be controllers (amend the epics) or minimal-API "platform features" (amend ADR-004)? Decision drives whether it's a code move or a doc change.
- **SOLID-3:** is "all-invalid ⇒ all scopes" intended convenience or an oversight? If intended, it needs an ADR; the audit reads it as an oversight.
- **TR-9 doc-only floor:** which practices become inherited CI gates now vs. in the separate inheritance-hardening pass (explicitly out of scope for this suite).
