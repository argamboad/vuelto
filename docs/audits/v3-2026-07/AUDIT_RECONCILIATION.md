# v3 Delta Audit — Phase 2: Reconciliation Pass (with tools)

> **Status: COMPLETE.** Diagnose-only. Reads Phase 1 `AUDIT_REPORT.md` + `FOUNDATION_RULES.md` (candidate
> R36–R76) and reconciles them against tool output. **Deference rule honored:** no Phase 1 rule was removed
> (a Phase 1 rule is removed only if a tool *proves* its finding false — none did); tool-only findings and
> their rules are added; disagreements are logged, not silently resolved.

## SUMMARY

- **Commit SHA (pinned):** `5fc1762dc5487de26af0e515c34c264efaaa11a7` (audited on branch
  `audit/v3-2026-07-phase2` at `9327288`, which is `5fc1762` + the Phase 1 audit docs — **code identical**).
  Matches Phase 1's SHA.
- **Tool runs (raw output in `tooling/`):** `dotnet build` (locked-mode restore, warnings-as-error) ·
  `dotnet test` + XPlat coverage · `dotnet list package --vulnerable --include-transitive` · `--deprecated` ·
  locked-mode restore of the DEP-4-implicated `src/Web` · dependency/license inventory · a size-based
  complexity proxy over the delta.
- **Headline results:** **build clean (0 warnings under WAE)** · **all 532 tests green** (Core 49/49,
  Api 483/483 — including the RLS-enforced Testcontainers integration tests) · **0 vulnerable packages** ·
  **0 deprecated packages** · overall line coverage **93.9%** (Infrastructure 98.3%, Core 94.3%, **Api
  75.6%**), overall branch **63.5%**.
- **Reconciliation counts:** Phase 1 findings **Confirmed/consistent with tools: all** (no static finding
  contradicted outright) · **1 refined** (DEP-4 — its NU1004-in-CI reading does not reproduce, but the tools
  **reproduced a live lockfile-churn/silent-downgrade trap** on SDK 10.0.301) · **Tool-only new findings: 4**
  (TOOL-1…TOOL-4) · **Unresolvable disagreements: 0.**
- **Rules:** none removed. Candidate rules **R77–R81** added (tool-only findings + the QA run-log
  append-only machine check); the enforcement backlog below orders every candidate check (R36–R81).

---

## 1. Confirmed — tools corroborate Phase 1

| Phase 1 finding | Tool evidence | Effect |
|---|---|---|
| **RLS-1 (High)** — parity gate tautological | Full suite **green incl. `RlsMigrationGateTests`** (Api 483/483). The gate *passing* is exactly consistent with "it cannot fail" — the green run is corroboration, not refutation. Phase 1 also code-traced the mechanism. | **Confirmed.** Severity unchanged (High). |
| **UX-1…UX-5, ADM-8, ADM-9** (client-side reload/race/pref logic) | Coverage: **`Perezosoft.Shared.Ui` = 1.2% line** (the RCL holds `AuthService`, `MainLayout`, `ThemeSwitcher`, `LanguageSwitcher`, `NotificationBell`, `Join`, `Billing`). This code is exercised **only by E2E**, not unit/integration — so these logic bugs live in code no fast test guards. | **Confirmed + amplified.** Corroborates why the cluster exists; feeds Phase 3 harness-readiness. |
| **ADM-1…ADM-7** (auth/admin server paths) | Coverage: **`Perezosoft.Api` = 75.6% line** (vs Infra 98.3% / Core 94.3%) and overall **branch 63.5%** — the API layer's controllers/services are the least-covered, and branch coverage is where untested error/lockout paths hide (ADM-3 brute-force, ADM-1 pref-suppression, ADM-5 liveness). | **Confirmed** as under-tested; the specific bugs are Phase 3 logic targets. |
| **Debt/SOLID (Phase 1 §5–6, "low")** | Build clean at **0 warnings** under warnings-as-error; complexity proxy shows no runaway hand-written file (largest non-generated delta files: `Household.razor` 538, `AuthService.cs` 491 — sizeable, not god objects). EF migration `*.Designer.cs` dominate the size list but are generated. | **Confirmed.** No new debt surfaced. |
| **Step-0 "gates green"** | Executed: the arch tests, RLS parity gate, stamping/quota/MFA/billing pins all pass in the 483 Api.Tests. | **Confirmed.** |

## 2. Contradicted / false positives / refined

| Phase 1 finding | Tool evidence | Disposition |
|---|---|---|
| **DEP-4 (Medium)** — SDK-pin drift, "the pinned build stage will NU1004" | **Directly reproduced the drift mechanism** (`tooling/web-locked-restore.txt`): the committed `src/Web/packages.lock.json` pins the WASM SDK assets (`App.Internal.Assets`, `ILLink.Tasks`, `WebAssembly.Pack`) at **10.0.10** (SDK 10.0.302), but the local/Docker SDK **10.0.301 ships 10.0.9**. **Locked-mode restore passes** (it pulls the pinned 10.0.10 — so **no NU1004 in CI**, which floats 10.0.x + locked mode), **but a *non-locked* restore under 10.0.301 silently rewrites the lockfile 10.0.10→10.0.9** — observed live: my `dotnet list --deprecated` scan mutated the lockfile in place (9/9 lines, all 10.0.10→10.0.9; reverted). | **Refined + sharpened, not removed.** The NU1004-in-CI reading does **not** reproduce (locked mode saves CI). The *real* hazard is confirmed and more precise: **lockfile churn / silent-downgrade on any dev or Docker box on 10.0.301** (the Dockerfile's SDK pin) — it survives only because the Dockerfile deliberately unlocks Web restore. Net: doc-accuracy + a live dirty-tree/downgrade trap. Rule **R55** unchanged (single SDK pin source is the fix); DEP-4 re-ranked Low→Medium hazard on the churn, not the CI-break. |

No Phase 1 finding was **disproven**. No rule removed.

## 3. Tool-only findings (missed by the static pass)

| ID | Severity | Location | Finding | Fix |
|----|----------|----------|---------|-----|
| **TOOL-1** | Medium | `.github/workflows/ci.yml:120-128` | **The license gate scans only `src/Api`** (`--input src/Api/Perezosoft.Api.csproj --include-transitive`). That covers the server graph (Api→Infrastructure→Core) but **not** `src/Web`, `src/Shared.Ui` (RCL — ships `System.IdentityModel.Tokens.Jwt`), or `src/Maui`'s client dependency graphs. A copyleft transitive dep entering via a Blazor/MAUI package would not trip the gate. Current graph is clean (all client deps MIT/Apache), so this is a **coverage gap, not a live violation** — but license findings carry weight given commercial intent, and every template clone inherits the blind spot. | Point the scan at a solution/traversal project that references Web + Shared.Ui + Maui, or run it per-project across all shipped assemblies. |
| **TOOL-2** | Medium | `src/Shared.Ui/**` (coverage) | **The RCL client layer has ~1% unit/integration coverage** (1.2% line), guarded only by the Playwright E2E suite. This is precisely where the UX-1…UX-5 / ADM-8 / ADM-9 logic lives (reconcile reload, two-way pref sync, theme switcher state, notification bell). The shared harness offers no fast way to test a `MainLayout`/`AuthService` branch, so client logic ships on E2E-only confidence. | Add a bUnit (or equivalent) component-test seam to the shared harness; Phase 3 specs the missing tests. Harness-readiness gap for any downstream slice with client logic. |
| **TOOL-3** | Low | whole suite (coverage) | **Overall branch coverage is 63.5%** against 93.9% line — a ~30-point line/branch gap concentrated in the API layer (75.6% line). Error paths, lockout branches, and guard clauses (the ADM/auth cluster) are the untested branches. Not a bug; a measured test-completeness signal that hands Phase 3 its priority order. | Phase 3 targets the uncovered branches, auth/admin first. |
| **TOOL-4** | Low | supply-chain (see §6) | **Two load-bearing dependencies are effectively single-maintainer**: `Otp.NET` 1.4.1 (the entire MFA TOTP/recovery-code path, ADR-012) and `DotNetEnv` 3.2.0 (dev secret loading). Neither is vulnerable or deprecated today; both are small, low-churn, single-maintainer packages on a critical path — the classic inherited supply-chain risk a template propagates to every clone. | Pin (already lockfiled) + document the dependency risk in TECH_STACK; consider vendoring/abstracting the TOTP primitive behind the existing `IMfa*` seam so it's swappable. |

## 4. Unresolvable disagreements (static vs tools)

**None.** Every static finding is either corroborated, refined with its substance intact, or orthogonal to what tools can measure (e.g. the security-logic findings ADM-1/2/4 are about *what the code does when it runs*, which the green suite neither proves nor disproves — the suite has no test for those adversarial paths, which is itself the point). No item requires Phase 5 to break a static-vs-tool tie.

One item carried forward for Phase 3 rather than resolved here: **TR-11** (static `[Test]` count 34 vs documented "suite 32"). The E2E (Playwright/NUnit) project was **not executed** in Phase 2 — it needs the full web stack (API + WASM + Chromium + Mailpit), out of scope for the tool-reconciliation run. The static discrepancy stands as Info; reconcile via `dotnet test tests/E2E.Tests --list-tests` when the stack is up.

## 5. Reconciled worklist (merged, de-duplicated, re-prioritized)

Confidence tags: **C-tools** (confirmed by tools) · **Static** (static-only, tools orthogonal) · **Tool** (tool-only).
Foundation-reusability impact in the last column (every clone inherits it = ★).

| Rank | ID | Sev | Location | One-line | Conf | Reuse |
|------|----|----|----------|----------|------|-------|
| 1 | RLS-1 | High | `IntegrationTestFactory.cs:66-67` | RLS parity gate is tautological; new tenant tables reach prod unprotected | C-tools | ★ |
| 2 | ADM-2 | Med | `AdminApiControllerBase.cs:33-43` | Staff gate accepts impersonation tokens → misattributed privileged writes | Static | ★ |
| 3 | ADM-3 | Med | `MfaLoginService.cs:40-56` | MFA step-up has no per-user brute-force cap | Static | ★ |
| 4 | RLS-2 | Med | `TenantDissolutionService.cs:30-36` | Dissolve/erasure silently no-ops under RLS without EnterTenant (GDPR) | Static | ★ |
| 5 | ADM-4 | Med | `TokenHasher.cs:11-16` | Recovery codes ~49.5 bits under unsalted SHA-256 | Static | ★ |
| 6 | DEP-2 | Med | `Program.cs:218-242` | No security headers/HSTS on the single-origin web host | Static | ★ |
| 7 | RLS-4 | Med | `EfRepository.cs:16-19` | `QueryAllTenants`+`ExecuteUpdate/Delete` silently loses tenant sanction | Static | ★ |
| 8 | ADM-1 | Med | `AdminController.cs:348-350` | Security MFA-reset notice suppressible by user prefs | Static | ★ |
| 9 | ADM-5 | Med | `AdminController.cs:133-135` | Comp/revert 409 keys on id-presence not liveness; churned tenants stuck | Static | ★ |
| 10 | UX-1 | Med | `MainLayout.razor:135-137` | Locale-reload nukes /join + /auth-callback deep-links | C-tools | ★ |
| 11 | TOOL-2 | Med | `src/Shared.Ui/**` | RCL client layer ~1% fast-test coverage (E2E-only) | Tool | ★ |
| 12 | TOOL-1 | Med | `ci.yml:120-128` | License gate scans src/Api only; Web/Shared.Ui/Maui graphs unscanned | Tool | ★ |
| 13 | RLS-3 | Med | `RlsSessionInterceptor.cs:146-156` | System bypass inferred from null tenant (fail-open) not explicit scope | Static | ★ |
| 14 | NAT-1 | Med | `feat/native-oauth-resilience` | NATIVE-12 fix exists only on an unpushed local branch | Static | – |
| 15 | NAT-2 | Med | `Maui.csproj` + workload restores | MAUI dep graph floats despite the lockfile-exclusion rationale | Static | ★ |
| 16 | NAT-3 | Med | `MauiProgram.cs:21-27` | Release builds ship dev localhost/cleartext wiring | Static | ★ |
| 17 | UX-2 | Med | `MainLayout.razor:119-137` | Possible infinite reload loop on write-blocked storage | Static | ★ |
| 18 | DEP-7 | Med | `ci.yml:787-806` | Un-smoked, un-gated-in-tree prod deploy job | Static | ★ |
| 19 | TR-1 | Med | CLAUDE.md doc map | Onboarding docs (NEW_APP_GUIDE/OVERVIEW/PRIMER) unlinked | Static | ★ |
| 20 | TR-4 | Med | `WAYS_OF_WORKING.md:70-78` | RLS slice recipe absent from the checklist/PR template (pairs w/ RLS-1) | Static | ★ |
| 21 | TR-6 | Med | `postman/README.md:3` | "Every HTTP surface" false — 6 auth endpoints absent, no exclusion note | Static | ★ |
| — | 35 Low + 2 Info | Low | (Phase 1 register) | RLS-5..8, DEP-1/3/5/6/8/9/10/11/12, NAT-4/6/8/9/10/11, ADM-6/7/10/11, UX-3/4/5, TR-2/3/5/7/8/9/10/11, TOOL-3/4 | mixed | mixed |

DEP-4 stays in the Low band as a hygiene/DX item (lockfile churn + doc-accuracy — not security/correctness), but its hazard is now **live and reproduced**, not latent (per §2). Its fix (R55, single SDK pin source) is worth doing early because the dirty-tree/silent-downgrade risk hits every contributor on 10.0.301.

## 6. Supply-chain depth

**Inventory** (`Directory.Packages.props`, all Central-Package-Managed + lockfiled): all versions are current stable, **no previews** (R28-family holds). Non-Microsoft load-bearing packages and their licenses:

| Package | Ver | Role | License | Maintainer risk |
|---|---|---|---|---|
| Stripe.net | 52.1.0 | billing | Apache-2.0 / MIT | Vendor-backed — low |
| Npgsql(.EFCore) | 10.0.2 | database | PostgreSQL (BSD-like) | Active org — low |
| MailKit | 4.17.0 | email | MIT | Active (jstedfast) — low |
| AWSSDK.S3 | 4.0.100 | file storage | Apache-2.0 | Vendor — low |
| OpenTelemetry.* | 1.16.0 | observability | Apache-2.0 | CNCF — low |
| Swashbuckle.AspNetCore | 7.2.0 | OpenAPI | MIT | Community — moderate |
| **Otp.NET** | 1.4.1 | **MFA TOTP + recovery codes** | MIT | **Single-maintainer, low-churn — TOOL-4** |
| **DotNetEnv** | 3.2.0 | dev secret loading | MIT | **Single-maintainer — TOOL-4 (dev-only)** |
| System.IdentityModel.Tokens.Jwt | 8.19.1 | JWT (client) | MIT | Microsoft — low |

- **Vulnerabilities:** none (`--vulnerable --include-transitive` clean across Core/Infrastructure/Api).
- **Deprecated:** none (5/5 scanned projects clean).
- **License compatibility (commercial SaaS):** every resolved license is permissive (MIT/Apache-2.0/BSD/PostgreSQL) — no copyleft. **But the CI gate that enforces this scans only `src/Api`** (TOOL-1): the Web/Shared.Ui/Maui client graphs are inventoried by no gate. Current client deps are all MIT, so no live exposure.
- **Lockfile integrity:** 8 committed `packages.lock.json`, locked-mode restore enforced in CI and **verified passing locally** (incl. `src/Web`, contra DEP-4's active-failure reading). The **MAUI project is deliberately excluded** (ADR-018) — NAT-2 shows that exclusion leaves the native graph floating on `$(MauiVersion)`; this is the one real lockfile-integrity gap.
- **Transitive bloat:** not material — the graph is dominated by the ASP.NET/EF meta-packages a clone needs anyway.

## 7. QA-artifact determinism (Phase 2 required check)

- **`QA_TEST_GUIDE.pdf` generation is already deterministic-in-CI.** `gen_qa_guide.py` renders the guide from `QA_TEST_PLAN.md`, and the `qa-artifacts` CI job runs `check_qa_artifacts.py` (pinned `reportlab==5.0.0`, `pypdf==6.14.2`) to regenerate and text-compare against the committed PDFs — a hand-edit or a stale PDF fails CI (v2 B11-8). **The phase's "move generation fully into the script in CI" proposal is already satisfied.** One nit: the pins are inline in the workflow, not in a requirements file — a version bump is a silent drift point (folds into R59's supply-chain-pin hygiene).
- **`gen_qa_runlog.py` produces a fresh blank run-log *form*** (with a "Build / commit SHA" column the tester fills in) — it doesn't rewrite any history because it emits a template, not a log. **The append-only property applies to the human-maintained run-log section of `QA_TEST_PLAN.md`, which is *not* machine-checked** — this is exactly Step-0 gap **S0-G5** / rule-gap R31. Recommendation stands: a git-diff CI check asserting the run-log block is additions-only (candidate already noted; folded below).

---

## Enforcement backlog (ordered; each mapped to its rule)

Ordered keystone-first / cheap-mechanical-last, so a later Phase-5 batch can lift it directly. **[machine]** unless noted.

1. **R36** — split `RlsTestSetup` so the integration factory applies role+grants only; meta-assert `IntegrationTestFactory` never calls `RlsDdl.StatementsFor`. *(fixes the RLS-1 keystone — do first; every other RLS rule assumes the gate works.)*
2. **R39 + R41 + R43** — one strengthened tenant-hatch fitness test: scope `Features/** + Endpoints/**`, patterns = `RlsTags` identifier + `rls:cross-tenant` literal + `QueryAllTenants`/`IgnoreQueryFilters` composed with `ExecuteUpdate/Delete`. *(merge per RULE_CONFLICTS C1.)*
3. **R45 + R50** — reject `impersonated_by` principals at `RequireStaffAsync` and on mutating account-preference endpoints; integration tests.
4. **R46** — per-user IP-independent MFA attempt cap; integration test (N wrong TOTPs across IPs ⇒ locked).
5. **R44** — `security.*` notifications bypass preferences; integration test.
6. **R37 + R40 + R42** — dissolve-enters-tenant RLS test; interceptor `TransactionFailed` invalidation test; tag-anchoring unit test.
7. **R47** — keyed/peppered hash (or ≥96-bit) for long-lived credential material; arch scan on `ITokenHasher` call sites.
8. **R52** — empty-config posture test for every `*Settings` (the never-shipped R21 gate).
9. **R53 + R54** — single-origin security-header + cache-policy integration tests.
10. **R55 + R62** — one-SDK-pin consistency grep (global.json/ci.yml/Dockerfile/TECH_STACK) + `--version` on every workload restore.
11. **R57 + R58 + R59** — deploy-fails-not-skips-on-missing-smoke-config; top-level least-privilege `permissions:`; no `:latest`/`releases/latest` + checksum raw downloads (incl. the QA-PDF pip pins).
12. **R61** — `native-paths` regex validated against the tree (adds `Directory.Build.props`).
13. **R56 + R63 + R64** — non-vacuous-CI-test assertion; Release-build dev-wiring guard; index.html/lib host-parity gate.
14. **R66 + R67 + R68 + R69 + R71 + R72** — Postman parity gate; doc-map/QA-count sync; config-catalog env-var/section coverage; DataProtection-string freeze; Features anonymous-error-object ban; resx satellite parity.
15. **R73** — reconcile-reload E2E (deep-link preserved; non-looping) — depends on the TOOL-2 client-test harness.
16. **R76** — R3 machine half: outbound-to-user-URL requests route through `IOutboundUrlGuard`.
17. **[new] R-runlog** — git-diff CI check that the `QA_TEST_PLAN.md` run-log block is additions-only *(S0-G5 / R31 machine half; §7)*.
18. **[review]** R38 (until `EnterSystem` seam), R48, R51, R65, R70, R74, R75, TOOL-4 doc.

---
*Phase 2 complete. Findings and rules added; none removed. Phase 3 (logic + test-completeness) reads this and the Phase 1 report next; Phase 5 remains the only reconciler and gate.*
