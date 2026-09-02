# AUDIT_RECONCILIATION.md — v2 Phase 2 (Reconciliation with Tools)

## SUMMARY

- **Commit SHA:** `84c7ad838c8e7cdc8c9bfb0c4cb939646025040e` — matches Phase 1. (Audited on branch `audit/v2-phase2-reconcile`; the `src/` tree is identical to Phase 1, only `docs/audits/v2-2026-07/` accumulates.)
- **Tools run:** `dotnet build -c Debug` (warnings-as-error via `Directory.Build.props`); `dotnet test --collect "XPlat Code Coverage"` (Core.Tests + Api.Tests via Testcontainers + E2E.Tests); `dotnet list package --vulnerable --include-transitive`; `dotnet list package --deprecated`; lockfile/CPM inspection; complexity via file-size + ctor-dependency counts. Raw output in `docs/audits/v2-2026-07/tooling/`.
- **Headline tool results:** build **clean, 0 warnings**; **Core 42/42 ✓, Api 335/335 ✓**; **E2E 0/4 — environment failures only** (no running app at `localhost:7008`); **~88% line coverage** (12,196/13,855) on the unit+integration-exercised code; **0 vulnerable packages**; 1 deprecated (`xunit` v2 "Legacy"); **no lockfiles / no central package management**.
- **Reconciliation counts:** Confirmed-by-tools 3 · Contradicted/false-positive 0 · Tool-only 4 (T1–T4) · Unresolvable disagreements 0.
- **Rules:** no Phase 1 rule removed (none tool-disproven). Added **R25–R27** for tool-only findings. Enforcement backlog (all 24+3 rules → exact checks) below.
- **Conflicts logged:** 0 new in `RULE_CONFLICTS.md`.

> Deference rule honored: a Phase 1 rule is removed **only** if a tool proves its finding false (logged in `RULE_CONFLICTS.md`). No finding was tool-disproven, so all R1–R24 stand. Serious static-only findings are retained and flagged as such.

---

## 1. Confirmed by tools

| Phase 1 finding | Tool corroboration | Note |
|---|---|---|
| **SOLID-2** — `AuthController` god class | File-size scan: `AuthController.cs` = **675 lines**, the largest hand-written file in `src/` (all larger files are auto-generated migration `.Designer.cs`). `AuthService.cs` = 427 (the #2 hotspot Phase 1 named). | Severity **stands** (High). Objectively the two densest hand-written units. |
| **B9 gates / warnings-as-error (STEP 0)** | `dotnet build` = **0 warnings, 0 errors** under `Directory.Build.props` WAE+nullable; `dotnet test` ran the three `ArchitectureTests` (filter-per-entity, no-`IgnoreQueryFilters`-in-Features, no-inline-Razor) and `MigrationsTests` drift check — **all green** in the 335-pass Api suite. | The v1 gates are not just present but **passing** at this commit. Confirms STEP 0's "Held." |
| **TR-9 / roadmap E2E debt** — E2E is doc-only, not CI-enforced | `dotnet test` E2E.Tests = **4/4 failed**, every failure a Playwright connection timeout to `https://localhost:7008` (no app running). E2E cannot run headless without a hosted app and is absent from `ci.yml`. | Confirms E2E is **unenforced**. Not a code defect — an infrastructure gap. Feeds Phase 3's E2E spec + the enforcement backlog. |

---

## 2. Contradicted / false positives

**None.** No tool disproved any Phase 1 finding. In particular, the clean 0-warning build does **not** contradict the design findings (SOLID/DEBT/GAP): those are architecture/security issues the compiler and analyzers cannot see, so a green build is expected and consistent. GAP-1 was independently re-verified against source by the Phase 1 parent (fake-provider registration switch `ServiceCollectionExtensions.cs:96-99` + anonymous always-mapped controller + `EnterTenant` from request body) — tools neither add nor remove; it stands **Critical**.

---

## 3. Tool-only findings (missed by the static pass)

| ID | Severity | Location | Finding | Confidence |
|---|---|---|---|---|
| **T1** | **Medium** | repo-wide (no `packages.lock.json`, no `Directory.Packages.props`, no `RestorePackagesWithLockFile`) | **No dependency lockfile and no central package management.** Restores float to whatever NuGet resolves at build time; there is no lockfile integrity to verify and no single pinned version list. Every generated app inherits non-reproducible restores and per-project version drift (already visible: `Microsoft.NET.Test.Sdk` is **17.14.0 in one test project, 17.14.1 in another**; `Microsoft.Extensions.Logging.Debug` 10.0.0 vs the 10.0.9 ASP.NET line). For a template whose whole value is a trustworthy inherited baseline, this is the highest-value supply-chain gap. | Confirmed-by-tools |
| **T2** | **Low** | `tests/Api.Tests`, `tests/Core.Tests` (`xunit 2.9.3`) | `dotnet list package --deprecated`: **xunit v2 flagged "Legacy"** (alternative `xunit.v3`). Not vulnerable and still fully supported; a forward-looking cleanup, not urgent. E2E uses NUnit 4 (unaffected). | Confirmed-by-tools |
| **T3** | **Low** | coverage report | **~88% line coverage overall, but sharply uneven.** The cobertura fragments show the Api/Infrastructure/Core code the integration suite exercises at ~0.88, while a UI/host assembly fragment sits at **0.17 (38/225)** and the E2E-attributed fragment at 0. Corroborates that business logic is well-covered but the **UI/Blazor layer and the E2E journeys are the coverage holes** — hands the precise low-coverage targets to Phase 3. | Confirmed-by-tools |
| **T4** | **Low** | `Microsoft.NET.Test.Sdk` 17.14.0 vs 17.14.1 across test projects | Version skew within the same solution (a symptom of T1's no-CPM). Harmless today; the kind of drift a lockfile/CPM prevents. | Confirmed-by-tools |

*(No tool surfaced a vulnerability, a nullable hole, or a complexity violation beyond what Phase 1 already named. `dotnet list package --vulnerable --include-transitive` = clean on all 9 projects — a genuine positive worth recording for the generated-app baseline.)*

---

## 4. Unresolvable disagreements (static vs tools)

**None.** Nothing to escalate to Phase 5 from Phase 2.

---

## 5. Reconciled worklist (merged, de-duplicated, re-prioritized)

Severity · location · one-line · confidence · foundation-reusability impact. Phase 1 IDs retained; tool-only prefixed `T`.

| # | Sev | Finding | Location | Confidence | Foundation impact |
|---|---|---|---|---|---|
| 1 | **Critical** | GAP-1 default fake-billing + anonymous webhook → unauth cross-tenant sub write | `ServiceCollectionExtensions.cs:96`, `BillingWebhookController.cs`, `BillingWebhookHandler.cs:52` | Static (source-verified) | Every generated app ships the breach until Stripe is configured |
| 2 | High | SOLID-1 no `IUserDataContributor` → future per-user PII escapes GDPR erasure | `AccountErasureService.cs:34-97` | Static | Erasure completeness silently degrades per slice |
| 3 | High | GAP-2 SSRF on tenant webhook URL (metadata/RFC-1918), incl. sync test probe | `WebhookService.cs:107`, `WebhookSender.cs:20` | Static | Inherited once HOOKS enabled |
| 4 | High | TR-1 platform tenancy/GDPR/outbox tests depend on DELETE-ME Notes slice | `PostgresFixture.cs:62`, `*ScopingTests`, `Gdpr/*` | Static | Deleting the sample breaks the isolation guard on day one |
| 5 | High | DOC-10 `DATA_MODEL.md` missing 4 live entities (schema pre-read) | `docs/DATA_MODEL.md:115-146` | Static | Mandated pre-read misleads every schema change |
| 6 | High | SOLID-2 `AuthController` god class (675 lines/18 deps) | `AuthController.cs` | **Confirmed-by-tools** | Densest unit; copied as the auth pattern |
| 7 | **Medium** | **T1 no lockfile / no CPM → non-reproducible restores + version skew** | repo-wide | **Confirmed-by-tools** | Every clone inherits floating deps |
| 8 | Medium | SOLID-3 all-invalid scopes/events fail open to full access | `ApiKeyService.cs:95`, `WebhookService.cs:111` | Static | Security-adjacent; copied into future key features |
| 9 | Medium | DOC-17 CLAUDE.md tells devs to use `IgnoreQueryFilters()` (fails CI) | CLAUDE.md gr1, `DATA_MODEL.md:13` | Static | Auto-loaded manual breaks the build |
| 10 | Medium | DOC-18/DOC-22 slice recipe won't compile; UI epic has no story file | `WAYS_OF_WORKING.md:47`, `docs/stories/` | Static | Recipe copiers hit a wall |
| 11 | Medium | DEBT-1/3/4 config-binding ×4 + `Program.cs`/`AppDbContext` per-epic dumping grounds | `Program.cs`, `AppDbContext.cs:74` | Static | The composition patterns every slice copies |
| 12 | Medium | DEBT-5 two RBAC 403 payload shapes; copied action can drop the check | `PermissionEndpointExtensions.cs`, `HouseholdController.cs` | Static | Auth-enforcement consistency |
| 13 | Medium | DEBT-6/TR-7 PUBAPI/HOOKS break the documented route-registration rule | `Features/ApiKeyEndpoints.cs`, `WebhookEndpoints.cs` | Static | Three route styles in the exemplar base |
| 14 | Medium | CON-1 webhook accepts `http` though message/contract say `https` | `WebhookService.cs:107` | Static | Cleartext signed payloads |
| 15 | Medium | DEBT-2 claim-parsing duplicated ×6; DEBT-7 twin dissolve flows | multiple | Static | Shotgun surgery on auth/teardown |
| 16 | Medium | DOC-1/2/3/5/7/8/11/13/16 stale ROADMAP/BACKLOG/FEATURES/DATA_MODEL/ADR markers | docs | Static | Onboarding + trust in docs |
| 17 | Low | ARCH-1/2/3, TR-2/4/5/6/8, CON-2/3, GAP-3/4/5, DEBT-8..12, SOLID-4..8, DOC (low), **T2/T3/T4** | see §6 / Phase 1 | Mixed | Polish + generation-floor |

---

## 6. Supply-chain depth (beyond CVEs — every generated app inherits these)

- **Vulnerabilities:** `dotnet list package --vulnerable --include-transitive` → **clean on all 9 projects.** Record as a positive baseline.
- **Lockfile / reproducibility (T1, Medium):** **no `packages.lock.json` anywhere, no `Directory.Packages.props`, no `RestorePackagesWithLockFile=true`.** No lockfile integrity to check; restores are non-deterministic. **Recommend:** adopt Central Package Management (one `Directory.Packages.props`) **and** `RestorePackagesWithLockFile` + `--locked-mode` in CI so a generated app has a pinned, verifiable dependency set.
- **Version skew (T4):** `Microsoft.NET.Test.Sdk` 17.14.0 vs 17.14.1; `Microsoft.Extensions.Logging.Debug` 10.0.0 vs the 10.0.9 ASP.NET line — the exact drift CPM prevents.
- **Deprecated (T2):** `xunit` v2 "Legacy." Low; plan a v3 migration eventually.
- **Maintenance/abandonment risk:** load-bearing packages are all first-party Microsoft (ASP.NET/EF/DataProtection 10.0.9), or actively-maintained majors — `Npgsql.EFCore` 10.0.2, `Stripe.net` 52.1.0, `MailKit` 4.17.0, `AWSSDK.S3` 4.0.100, `OpenTelemetry` 1.16.0, `Testcontainers` 4.12.0, `Playwright` 1.60.0. The two smallest single-purpose deps — **`Otp.NET` 1.4.1** (MFA TOTP) and **`DotNetEnv` 3.2.0** (the ADR-001 `.env` loader) — are the notable single-maintainer/low-velocity packages; both are load-bearing (auth secret math; the entire local secrets story). **Recommend:** note them in `TECH_STACK.md` as watch-items with a swap path (Otp.NET → a hand-rolled RFC-6238, well-trodden; DotNetEnv → any dotenv parser or native env only).
- **License compatibility (commercial SaaS — High if any copyleft):** the inventory is MIT/Apache-2.0/BSD across the board (Microsoft MIT; Npgsql PostgreSQL-license/BSD-style; Stripe.net Apache-2.0; MailKit/MimeKit MIT; AWSSDK Apache-2.0; OpenTelemetry Apache-2.0; Otp.NET MIT; DotNetEnv MIT; Testcontainers MIT; Playwright MIT). **No GPL/AGPL/LGPL/SSPL detected** — clean for commercial redistribution. *Recommend a CI license-scan step (below) so a future transitive add can't silently introduce copyleft.*

---

## 7. Enforcement backlog (ordered; each mapped to its rule)

Ordered keystone-first (highest security/reproducibility leverage), enforcement-tests last. "How" is exact against the existing stack (`ArchitectureTests.cs`, `Directory.Build.props`, `ci.yml`).

| # | Rule | Check to add | Kind |
|---|---|---|---|
| E1 | **R1** | Startup/arch test: `FakeBillingProvider` is registered only when `IHostEnvironment.IsDevelopment()`; assert a throw otherwise. | new test |
| E2 | **R21** | Unit test: bind `PublicApiSettings`/`WebhooksSettings`/`PlatformAdminSettings` from empty config → assert disabled/empty. (Largely holds today — pin it.) | new test |
| E3 | **R17** | Unit test on `NormalizeScopes`/`NormalizeEventTypes`: `["garbage"]` → **reject**, not all. | new test |
| E4 | **R2** | `ArchitectureTests`: every `IMutableEntityType` with a `TenantId` property implements `ITenantScoped` or is on the `{WebhookDelivery, Notification}` allowlist. | extend arch test |
| E5 | **R4** | `ArchitectureTests`: reflection scan — every concrete `ControllerBase` derives from a tenant/admin base or is allow-listed. | extend arch test |
| E6 | **R6** | `ArchitectureTests`: source scan — no `MapGroup(`+`RequireAuthorization(` under `src/Api/Features/`. | extend arch test |
| E7 | **R7/R8** | `ArchitectureTests`: source scan — no cross-feature `using`; only `Program.cs` references `Features.*` from outside `Features/`. | extend arch test |
| E8 | **R9** | `ArchitectureTests`: `tests/` (excl. a dedicated `NotesSliceTests`) contains no `Note`/`Features.Notes` reference — after TR-1's test-only fixture entity lands. | extend arch test |
| E9 | **R15** | `ArchitectureTests`: source scan — no `DateTime.UtcNow`/`DateTimeOffset.UtcNow` in `src/**` outside `Migrations/` (would catch GAP-4). | extend arch test |
| E10 | **R5** | `ArchitectureTests`: warn on new `QueryAllTenants()` call sites outside `{*DataContributor, Admin*, *SweepJob}`. | extend arch test |
| E11 | **R13** | Test: build the DI provider, assert `ITenantDataContributor.ExportKey` values are distinct. | new test |
| E12 | **R12** | Test (after `IUserDataContributor`): every `UserId`-bearing entity outside the identity-core allowlist has a registered user-data contributor. | new test |
| E13 | **R11** | Replace `PostgresFixture` hardcoded TRUNCATE list with `ctx.Model.GetEntityTypes()` derivation (removes the hazard); interim test compares the two. | fixture change |
| E14 | **R19** | Test: forged-signature POST to `/api/billing/webhook` asserts a warning log is emitted. | new test |
| E15 | **R20** | CI script: scan `configuration["A:B"]`/`GetSection("A:B")` literals vs `.env.example` keys; fail on either-way mismatch. | `ci.yml` step |
| E16 | **R23** | CI doc-sync script: every `src/Core/Entities/*.cs` named in `DATA_MODEL.md`; no `IgnoreQueryFilters` as an opt-out in CLAUDE.md/docs; story-✅ ⇒ ROADMAP/doc-map complete. | `ci.yml` step |
| E17 | **R24 (naming half)** | Add `Meziantou.Analyzer` MA0048 (file name matches type) via `Directory.Build.props`. | analyzer |
| E18 | **R-mail (TR-9)** | `ArchitectureTests`: no `MailKit`/`MimeKit` reference outside `src/Infrastructure/Email/`. | extend arch test |
| E19 | **T1 → R25** | Adopt CPM (`Directory.Packages.props`) + `RestorePackagesWithLockFile=true`; add `dotnet restore --locked-mode` to `ci.yml`. | build+CI |
| E20 | **T-license → R26** | Add a license-scan CI step (e.g. `nuget-license`/`dotnet-project-licenses`) failing on GPL/AGPL/LGPL/SSPL. | `ci.yml` step |
| E21 | **R3, R16, R18, R22** (review→machine as fixes land) | After the `SafeHttpClient`/shared `ErrorResponse`/options-pattern seams exist, convert to string-scan arch tests. | deferred |
| E22 | **E2E in CI (TR-9/T3)** | Wire a `dotnet test E2E.Tests` job that boots the app (or mark E2E explicitly out-of-CI in docs with rationale). | `ci.yml` step / doc |

---

## 8. QA generation & run-log (suite-requested checks)

- **`gen_qa_guide.py` determinism:** *(to confirm during Phase 5 implementation)* — the memory note "Regenerate QA PDFs" + `qa-pdfs-regenerate` and DOC-19 indicate the guide PDF is generated from `QA_TEST_PLAN.md`. **Proposal (unchanged from suite):** move `QA_TEST_GUIDE.pdf` generation fully into CI so it is deterministic and never hand-edited; assert the PDF changes in the same commit as the plan (a doc-sync CI check, folds into E16).
- **`gen_qa_runlog.py` append-only:** the run log must **append** SHA-stamped entries, never rewrite. Add a CI/review check that the run-log file only grows (diff shows additions only). Folds into the R14/append-only review rule; mechanizable as a `git diff` check in CI.

*(These two are recorded as enforcement items; no code was run against the Python generators in this phase — they are Phase 5 implementation confirmations.)*

---

## Rules delta (see `FOUNDATION_RULES.md`)

- **Removed:** none (no rule tool-disproven).
- **Added:** **R25** (lockfile + CPM + locked-restore — T1/T4), **R26** (CI license-scan, no copyleft — supply-chain/commercial), **R27** (per-project package versions are single-sourced — T4, subsumed by R25's CPM but stated for review). Each mapped to an enforcement item above.
