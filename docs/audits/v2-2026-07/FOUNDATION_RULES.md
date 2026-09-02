# FOUNDATION_RULES.md — v1.0 (FINAL, post-consolidation)

Enforceable rules from the v2 five-phase re-audit of the SaaS template at commit `84c7ad8`. Consolidated and de-duplicated by Phase 5. Each rule is imperative, tied to its finding(s), categorized, and marked **[machine]** (arch test / analyzer / `Directory.Build.props` / `ci.yml`) or **[review]** (PR checklist / `WAYS_OF_WORKING.md`). Machine rules extend the existing `tests/Api.Tests/ArchitectureTests.cs` and `.github/workflows/ci.yml` gates.

> **Status: v1.0 FINAL.** Binding once the `AUDIT_TASKS.md` remediation is approved and implemented. Supersedes the Phase 1–4 drafts. Enforcement mechanisms per rule; the ordered enforcement backlog is in `AUDIT_RECONCILIATION.md` §7 (E1–E22) plus the Phase-4/5 additions below.

## Phase-5 conflict resolutions (recorded)

- **R5 ⊕ R33 (merged).** `QueryAllTenants()` is a first-class tenant-bypass equal to `IgnoreQueryFilters()`; the `Features/**` arch-ban covers **both**, allow-listing `*DataContributor.cs`. R33 is folded into R5 below.
- **TR-8 / DOC-17 doctrine (resolved).** The cross-tenant escape hatch for slice code is: **forbidden in request-path slice code, required in `*DataContributor.cs`** (dissolve/export). Docs must name `QueryAllTenants()` / `EnterTenant` — never `IgnoreQueryFilters()` — as the opt-out. ADR-014's "forbidden" wording is amended to "forbidden in request-path slice code." (Precedence rule (d): a machine-enforced Features-path ban with a contributor allowlist beats the ambiguous prose.)
- **R14 ⊆ R31 (merged).** The Phase-1 TDD rule R14 is subsumed by the stronger Phase-3 R31; R14 is retired, R31 is canonical.
- **ARCH-1 ≡ SOLID-4 (one rule).** Seam-interface placement is a single concern, carried by R24 (review) + the DIP note; no separate rule.
- No Critical-vs-Critical irreconcilable conflict arose. Security/tenancy invariants (R1, R2, R5, R28, R29, R30, R32) take precedence over style/convenience rules where they meet.

---

## Tenancy & auth

- **R1 [machine]** — No anonymous endpoint may perform a tenant-scoped write unless authenticity is verified by a non-fake verifier outside `Development`. Concretely: `FakeBillingProvider` must not be registered when the environment is not `Development` (throw at startup, or don't map `BillingWebhookController`). *(GAP-1 — Critical)* — startup/arch test on the registration switch.
- **R2 [machine]** — Every EF entity with a `TenantId` property either implements `ITenantScoped` (global filter) or appears on an explicit, commented allowlist (`WebhookDelivery`, `Notification`). *(ARCH-2)* — model scan in `ArchitectureTests.cs`.
- **R5 [machine]** *(merges R33)* — In `src/Api/Features/**`, both `IgnoreQueryFilters()` and `QueryAllTenants()` are banned (they bypass the tenant filter identically), allow-listing only `*DataContributor.cs`. Elsewhere, `QueryAllTenants()` outside `*DataContributor`/admin/sweep-job files must be justified. *(TR-8, DOC-17, ADV-2)* — extend the existing Features string-scan to both substrings.
- **R28 [machine]** — Second-factor verification is single-use: the step-up challenge is consumed on first success, and a TOTP timestep already accepted is rejected (persist last-used step on `UserMfa`). *(LOGIC-S1, S2 — High)* — unit/integration test asserting a replayed `{challenge, code}` fails.
- **R32 [machine]** — The write-side tenant guard covers **UPDATE and DELETE**, not just INSERT. `TenantStampingInterceptor` today inspects only `EntityState.Added`; extend it to reject `Modified`/`Deleted` `ITenantScoped` entries whose `TenantId != currentTenantId` on a tenant context. *(ADV-1 — High)* — integration test: a foreign-tenant UPDATE/DELETE under the wrong current tenant throws.

## Auth/authz boundary

- **R3 [review→machine when seam lands]** — Outbound/server-initiated HTTP to a client-supplied URL passes a single SSRF validator (reject loopback, link-local, RFC-1918/ULA, cloud-metadata; re-check after DNS) and is https-only outside `Development`. HOOKS' sync test and async sender both use it. *(GAP-2 — High, CON-1)* — route all such calls through one `SafeHttpClient` seam; then a string-scan arch test that no raw `HttpClient.Post(userUrl)` exists.
- **R4 [machine]** — Every non-abstract `ControllerBase` in Api derives from `TenantApiControllerBase`/`AdminApiControllerBase` or is on an explicit allowlist (`AuthController`, `FilesController`, `BillingWebhookController`, `NotificationsController`). *(DEBT-5, CON-3)* — reflection scan.
- **R17 [machine]** — Normalization of a security-relevant set (API-key scopes, webhook event types) distinguishes "absent" (default) from "provided but all-invalid" (reject 400); the latter never falls through to "all." *(SOLID-3 — Medium, security-adjacent)* — unit test on both services.
- **R19 [machine]** — Signature/authenticity rejections on system webhooks are logged (warning + optional audit/metric) with source context. *(GAP-5)* — assert a log call in the reject path.

## Slice-boundary

- **R6 [machine]** — Files under `src/Api/Features/` register routes via `MapTenantFeatureGroup(...)`, never a raw `MapGroup(...).RequireAuthorization(...)`. *(CONF-3, DEBT-6, TR-7)* — string scan.
- **R7 [machine]** — No feature folder references another feature's namespace. *(TR-9)* — source scan.
- **R8 [machine]** — Only `Program.cs` references `Perezosoft.Api.Features.*` from outside `src/Api/Features/`. *(ARCH — locks the clean state)* — source scan.
- **R9 [machine]** — Platform tests do not depend on the DELETE-ME `Note` entity / `Features.Notes.*`; the tenancy/GDPR/outbox harness uses a test-only `ITenantScoped` fixture entity. *(TR-1 — High)* — source scan over `tests/` excluding a dedicated `NotesSliceTests`.
- **R34 [machine]** — Adding a tenant-scoped slice must not require editing a hand-maintained central list. Reduce the 5 forced touchpoints: assembly-scan endpoint modules + `IEntityTypeConfiguration<>` (shrinks `Program.cs`/`OnModelCreating`), and derive the fixture TRUNCATE list from `AppDbContext.Model.GetEntityTypes()` (see R11). *(TR-2, TR-3, ADV-3)* — interim arch test comparing the fixture list to the model.
- **R35 [machine]** — Two parallel slices cannot silently collide on a `/api/<x>` route prefix or a table name. *(ADV-4)* — startup/arch test asserting unique route-group prefixes and distinct table names per `ITenantScoped` entity.
- **R10 [review]** — New-slice checklist (entity → DbSet + `IEntityTypeConfiguration` → migration → DI `Add*()`/`Map*()` → `ITenantDataContributor` with `ExportKey`+`ExportAsync` → fixture reset → nav + resx) is ticked in the PR. *(TR-2, DOC-18)* — `WAYS_OF_WORKING.md` + PR template.

## Testing & GDPR

- **R11 [machine]** — Test-fixture reset/TRUNCATE lists are derived from `AppDbContext.Model.GetEntityTypes()`, not hand-maintained. *(TR-3)*
- **R12 [machine]** — After `IUserDataContributor` lands: every entity with a `UserId` property outside the identity-core allowlist has a registered user-data contributor. *(SOLID-1 — High)* — model scan + DI assertion; turns the GDPR-erasure gap into a red build.
- **R13 [machine]** — `ITenantDataContributor.ExportKey` values are unique across DI registrations. *(TR-9)* — build the provider, assert distinctness.
- **R30 [machine]** — Quota/counter consumption is atomic under concurrency (conditional `UPDATE … WHERE count+amount<=limit` / row lock / upsert-on-conflict), honoring the "atomically consumes" contract. *(LOGIC-B7)* — concurrent integration test asserting the cap is never exceeded.
- **R31 [review→partly machine]** *(canonical TDD rule; supersedes R14)* — A failing test precedes production code; every slice ships happy-path + permission-denied + cross-tenant-isolation **read and write** negatives + every new public method per branch/error path before "done"; the shared harness is reused, never re-implemented; `QA_TEST_PLAN.md` is updated in the same PR as the code it covers; the QA run log is append-only. — machine parts: per-epic cross-tenant write-negative presence (once a slice-test manifest exists); run-log append-only (`git diff` additions-only).

## Logic-correctness & clock

- **R15 [machine]** — No ambient `DateTime.UtcNow` / `DateTimeOffset.UtcNow` in `src/**` (allowlist `Migrations/`, WASM client, entity-default helpers if explicitly listed); services take `TimeProvider`. *(GAP-4, LOGIC-B3, CONF-8 convention)* — source-scan arch test (fails today on `S3FileStorage.cs:81`, `CookieService.cs:36`).
- **R29 [machine]** — A projection writer applying external provider events (billing webhook) applies only strictly-newer events (persist a provider sequence/updated timestamp; recency guard); never blind last-writer-wins. *(LOGIC-B1, B2, B6 — High)* — integration test: a stale redelivery does not clobber newer status.
- **R16 [review]** — Client-facing error bodies never contain raw exception text (`ex.Message`); detail stays in server logs. *(GAP-3)*
- **R18 [review]** — All error responses use the shared `ErrorResponse` helpers — no anonymous `{ error, … }`. *(SOLID-7)* — string-scan once the helper exists.

## Config & migration

- **R20 [machine]** — Every `Section:Key` literal read via `IConfiguration` appears (as `Section__Key`) in `.env.example` or `appsettings*.json`, and vice versa. *(CON-2, config drift)*
- **R21 [machine]** — Config-gated features default OFF/closed when their section is absent. *(security posture)* — bind each `*Settings` from empty config, assert disabled.
- **R22 [review]** — Config binding uses one blessed pattern (typed options `.BindConfiguration().ValidateOnStart()`); no hand-rolled `new XxxSettings(config)` or scattered `.Bind()`. *(DEBT-1)* — string-scan once standardized.

## Supply chain

- **R25 [machine]** — Dependencies are centrally managed and lock-restored: one `Directory.Packages.props` (CPM) pins every version, `RestorePackagesWithLockFile=true`, CI runs `dotnet restore --locked-mode`. *(T1, T4)* — build config + `ci.yml`.
- **R26 [machine]** — No copyleft (GPL/AGPL/LGPL/SSPL) package enters the tree; a CI license-scan fails on one. *(supply-chain / commercial SaaS intent)* — `ci.yml` step.
- **R27 [machine]** — A given package resolves to one version solution-wide. *(T4; falls out of R25's CPM)*

## Naming & docs

- **R23 [machine]** — Docs never present `IgnoreQueryFilters()` as the slice cross-tenant opt-out; a story marked "✅" is reflected complete for the same slice numbers in `ROADMAP.md` + the CLAUDE.md doc map; every `src/Core/Entities/*.cs` is named in `DATA_MODEL.md`. *(DOC-10, DOC-17, DOC-1/2/3/16)* — grep-level CI doc-sync checks.
- **R24 [review]** — When an implementation diverges from its ADR's decision points, a dated amendment is added in the same PR; file name matches the primary type (MA0048 analyzer covers this [machine]); seam interfaces callable by slices live in `Core/Abstractions`, DTOs in slice `Models`/`Api/Models`; outbound email always goes through `BrandedEmail`. *(DOC-13/14/15/20/21, DEBT-8/9, SOLID-4, ARCH-1)*

## Retired

- **R14** — retired; folded into R31.
- **R33** — retired; folded into R5.

## Enforcement summary

**Machine-enforceable (24):** R1, R2, R4, R5, R6, R7, R8, R9, R11, R12, R13, R15, R17, R19, R20, R21, R23, R25, R26, R27, R28, R29, R30, R32, R34, R35 *(R3/R18/R22 become machine once their seams land)*. **Review-enforced (8):** R3, R10, R16, R18, R22, R24, R31 (mostly), plus the review halves above. Full check-to-rule mapping: `AUDIT_RECONCILIATION.md` §7 (E1–E22) + R32/R34/R35 arch tests + R28/R29/R30 correctness tests.
