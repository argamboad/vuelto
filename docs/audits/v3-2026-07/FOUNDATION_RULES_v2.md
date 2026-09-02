# FOUNDATION_RULES — v2.0 (final, post-v3-consolidation)

> **Status: FINAL (Phase 5 consolidation).** This is the binding ruleset after the v3 delta audit.
> **R1–R35 are carried over unchanged from `docs/audits/v2-2026-07/FOUNDATION_RULES.md` (v1.0) and remain
> binding** — they are not restated here; consult that file for their text. This document adds and
> consolidates the v3 rules as **R36–R76 (final numbering)**, produced by de-duplicating the Phase 1–4
> candidate block (R36–R100) per the conflict resolutions in `RULE_CONFLICTS.md`.
>
> Integrity: all four v3 phase reports audit commit `5fc1762dc5487de26af0e515c34c264efaaa11a7` (verified).
> Nothing here overturns an R1–R35 invariant; every v3 rule is additive or completes a previously
> review-only R1–R35 rule.

Each rule: **[machine]** (arch test / analyzer / CI gate) or **[review]** · category · enforcement mechanism
· the candidate ID(s) and finding(s) it subsumes.

## Conflict resolutions applied (from RULE_CONFLICTS.md)

- **C1/C13** → route + hatch scans merged and scoped to `Features/ + Endpoints/` (R36, R41).
- **C2** → the `native-paths` regex rule is single (DEP-5≡NAT-5) (R60).
- **C4/C8** → the RLS new-slice contract is one family: gate correctness (R37) + tenant-axis canary (R43) +
  documented recipe (R66) + dissolve-enters-tenant (R44).
- **C5** → one deploy-fail-not-skip rule (R56); prod-smoke is a remediation task, not a separate rule.
- **C9** → the three atomicity rules are kept, grouped (R47–R49).
- **C10** → R45 (staff-gate rejects impersonation) and R50 (impersonated-write attribution) are both kept —
  complementary, neither subsumes the other.
- **C11/C12** → R98 folded into R73-equivalent (R70); the bUnit-chassis clause of R99 is the concrete form of
  R67; kept as the client-test-seam rule.
- **C13** → R100 (route regex matches the mandated helper) supersedes CAND-S0-1 and becomes the single
  route-uniqueness rule (R36).

---

## Tenancy / RLS (Critical — the adversarial pass ranks these above all)

- **R36 [machine]** — Route-prefix uniqueness across `src/Api/Controllers/`, `src/Api/Features/`, **and**
  `src/Api/Endpoints/`; the scan matches the **mandated** helper `MapTenantFeatureGroup("…")` as well as
  `[Route]` and raw `MapGroup`. *(fix `RouteGroupPrefixes_AreUnique`; subsumes R100, CAND-S0-1, S0-G1;
  ADV-P4-1.)*
- **R37 [machine] — RLS parity gate keystone.** The migration-gated integration database receives RLS
  policies **only from migrations**; the harness may provision roles/grants but never model-derived policy
  DDL. Split `RlsTestSetup`; add a meta-assertion that `IntegrationTestFactory` never references
  `RlsDdl.StatementsFor`. *(R36-cand; RLS-1, proven live ADV-P4-2.)*
- **R38 [machine]** — Tenant-hatch bans (`IgnoreQueryFilters`, `QueryAllTenants` outside `*DataContributor`,
  `RlsTags` **and** the literal `rls:cross-tenant`) cover both `Features/**` and `Endpoints/**`. *(merges
  R39/R41/R43/R6-cand; RLS-6, S0-G2.)*
- **R39 [machine]** — Never compose `QueryAllTenants()`/`IgnoreQueryFilters()` with
  `ExecuteUpdateAsync`/`ExecuteDeleteAsync`; set-based cross-tenant writes require `EnterTenant`/system scope
  first. Multiline source scan over `src/**`. *(R39-cand; RLS-4, RLS-8.)*
- **R40 [review→machine] — No implicit RLS bypass.** The bypass GUC is asserted only for an explicitly
  entered system scope (`EnterSystem()`/`ISystemScope`) or a sanctioned tagged command — never inferred from
  a null tenant on a request-scoped context. *(R38-cand; RLS-3. **Human-decision on seam scope — see the
  remediation plan.**)*
- **R41 [machine]** — `RlsSessionInterceptor` invalidates its per-connection GUC cache on **every** revert
  path, including `TransactionFailed`; tag recognition is anchored to the leading EF tag-comment block.
  *(R40/R42-cand; RLS-5, RLS-7.)*
- **R42 [machine]** — A new `ITenantScoped` entity ships its RLS policy in the same migration
  (`RlsDdl.StatementsFor`); enforced by the corrected R37 gate. *(TR-4 machine half.)*
- **R43 [machine] — Tenant-axis completeness canary.** Every `ITenantScoped` entity is wired into tenant
  dissolution **and** export (a registered `ITenantDataContributor` or an explicit teardown/export
  allowlist), and every `TenantId`-carrying non-`ITenantScoped` entity has a dedicated cross-tenant
  isolation test. Model-scan mirroring `EveryUserKeyedEntity_IsWiredIntoAccountErasure`. *(R82/R84-cand;
  LB-TEN-1 keystone, LB-TEN-2, extends R12/R13 to the tenant axis.)*
- **R44 [machine] — Dissolve/erasure enters its target tenant.** `DissolveAsync`/erasure/sole-owner-leave
  execute inside `EnterTenant(target)` so RLS'd set-based deletes cannot silently no-op; scan every
  `DissolveAsync(` caller. *(R83-cand, machine half of the review-only intent; RLS-2.)*

## Auth / admin surface (Critical/High)

- **R45 [machine]** — Every endpoint gated by `RequireStaffAsync`, and every mutating account-preference
  endpoint, rejects principals carrying `impersonated_by`. *(R45/R50-cand; ADM-2, ADM-8.)*
- **R46 [machine]** — Security-class notifications (`security.*`) are delivered on both channels regardless
  of `NotificationPreferences`. *(R44-cand; ADM-1.)*
- **R47 [machine]** — Every second-factor verify path enforces a per-user, IP-independent attempt cap
  (mirrors the OTP lockout). *(R46-cand; ADM-3.)*
- **R48 [machine]** — Single-use credential consumption (magic-link, OTP, MFA challenge) is atomic
  (conditional `ExecuteUpdate … WHERE ConsumedAt IS NULL`, affected==1; session only on the winning update).
  *(R85-cand; LB-AUTH-3.)*
- **R49 [machine]** — Brute-force/attempt counters increment atomically (server-side increment or
  RowVersion+retry; cap evaluated against the persisted post-increment value). *(R86-cand; LB-AUTH-2;
  generalizes R30.)*
- **R50 [review→machine]** — Second-factor state is not mutated before the login challenge is confirmed
  single-use. *(R87-cand; LB-AUTH-1.)*
- **R51 [machine]** — Long-lived credential material (recovery codes, API keys, refresh tokens) is stored
  under a keyed/slow hash (HMAC + server pepper) or carries ≥96 bits of entropy. *(R47-cand; ADM-4.)*
- **R52 [machine]** — No audit write on a tenant-scoped mutation attributes an action to a principal without
  recording `impersonated_by` when the principal carries it (requires an `AuditEvent` attribution column).
  *(R92-cand; LB-ADM-1.)*
- **R53 [machine]** — Config-gated features are closed under empty configuration: bind every `*Settings`
  from an empty `IConfiguration` and assert disabled/closed. *(R52-cand; the never-shipped R21 gate, S0-G3.)*
- **R54 [review]** — Tenant-permission resolution keys on the request's `tenant_id`; membership lookups for
  authz are deterministic, never an unfiltered `FirstOrDefault`. *(R93-cand; LB-ADM-3.)*
- **R55 [review]** — Bulk/destructive endpoints define empty-selector semantics explicitly (empty ⇒ none)
  and never default to the widest blast radius. *(R94-cand; LB-ADM-2, LB-UI-10.)*

## Billing / jobs correctness

- **R56 [machine]** — The webhook recency guard rejects only *strictly older* events; two distinct events
  sharing a timestamp both take effect in arrival order. *(R89-cand; LB-BILL-1.)*
- **R57 [machine]** — Outbox attempt/dead-letter bookkeeping advances on **any** completion failure incl.
  post-handler commit/flush faults; a poison-at-commit message dead-letters, never loops. *(R88-cand;
  LB-BILL-2.)*
- **R58 [machine]** — Quota insert-conflict recovery catches only the unique violation (`23505`); any other
  `DbUpdateException` propagates. *(R90-cand; LB-BILL-3.)*
- **R59 [review]** — Dunning/lapse notifications fire only on a transition *out of* a granting status; a
  provider-managed ⇒ 409 guard tests subscription liveness (status), not id-presence. *(R91/R48-cand;
  LB-BILL-4, ADM-5.)*

## Deploy / CI / supply-chain

- **R60 [machine]** — The `native-paths` filter regex names every file class that can affect a native binary
  (`Directory.Build.props`, `Directory.Packages.props`, `global.json` iff present, `src/`, the smoke
  harnesses, the workflow), validated against the tree. *(R61-cand; DEP-5≡NAT-5.)*
- **R61 [machine]** — One SDK pin source (`global.json` / `ci.yml` / Dockerfile / TECH_STACK agree, plus a
  bump-together playbook), and every `dotnet workload restore` carries `--version`. *(R55/R62-cand; DEP-4,
  DEP-11, NAT-2.)*
- **R62 [machine]** — When `Hosting:ServeWebClient` is on outside Development, `/` and `/_framework/*` carry
  HSTS + `nosniff` + a frame-ancestors policy; the SPA shell is `no-cache`, fingerprinted assets `immutable`.
  *(R53/R54-cand; DEP-2, DEP-3.)*
- **R63 [machine]** — A deploy job that fires a hook fails (not skips) when its post-deploy smoke config is
  absent; every workflow declares a least-privilege top-level `permissions:`; no `releases/latest` URLs or
  `:latest` image tags, and raw binary downloads are checksum-verified; pinned CI tool/pip versions live in
  a committed manifest. *(R57/R58/R59/R80-cand; DEP-6, DEP-7, DEP-8, DEP-9, §7-nit.)*
- **R64 [machine]** — Outside Development, the Stripe key's `sk_live_`/`sk_test_` mode matches an explicit
  config expectation (fail-closed startup guard). *(R60-cand; DEP-10.)*
- **R65 [machine]** — The license gate inventories the transitive licenses of the server **and** client
  projects (Api, Web, Shared.Ui, Maui), not `src/Api` alone. *(R77-cand; TOOL-1.)*
- **R66 [review]** — Load-bearing single-maintainer dependencies (today: `Otp.NET` behind MFA) are
  documented in TECH_STACK and sit behind a swappable seam. *(R79-cand; TOOL-4.)*

## Native

- **R67 [machine]** — Native Release builds fail when dev wiring survives (localhost API base in Release;
  cleartext network config + API-base env override Debug-only). *(R63-cand; NAT-3.)*
- **R68 [machine]** — Host parity: both `index.html` files reference the identical RCL `js/*.js` set with
  `theme.js` before the first stylesheet, and the two vendored `wwwroot/lib` trees are byte-identical; every
  CI test-with-filter step proves non-vacuous execution. *(R64/R56-cand; NAT-4, host-parity.)*
- **R69 [review]** — Built-but-unmerged slice work reaches `origin` in the same session or is logged as
  in-flight in its epic story file. *(R65-cand; NAT-1.)*

## Client (RCL) / test-completeness

- **R70 [machine] — Client component-test chassis.** A test project exercises `src/Shared.Ui` `.razor`
  components with a component-test host (bUnit or equivalent) — the shared harness provides an `IJSRuntime`
  fake, `IThemePersistence`/`ICulturePersistence` doubles, a JWT-claim builder, an injectable client clock,
  a concurrency runner, a DB-fault-injection seam, and an impersonation-token client helper; a new slice
  reuses them, never rebuilds. Enforced by a Shared.Ui coverage floor or a razor-rendering test assembly's
  existence. *(R95/R99-cand; TOOL-2, harness gaps.)*
- **R71 [machine]** — `ReconcilePreferencesAsync` no-ops entirely while `IsImpersonating` (server-wins AND
  adopt branches gated); device preference stores are scoped to the writing principal (or cleared on
  sign-out) before a null-server value is adopted. *(R96/R97-cand; LB-UI-4/5, ADM-8/9.)*
- **R72 [review]** — Reconcile/state reloads preserve the current deep link for non-terminal anonymous paths
  (`/join`, `/auth-callback`); only `/login`/`/auth-error` redirect to `/`. Any accepted client-side race is
  an ADR amendment, not a commit-message aside. *(R98/R73/R75-cand; UX-1/2/3, LB-UI-1/2.)*
- **R73 [review]** — Server-supplied enum-ish values (plan key, status, roles) rendered as text pass through
  a localized mapping with raw fallback; resx keys are EN↔ES parity-complete and per-slice namespaced.
  *(R74/R72-cand; UX-5.)*

## Docs / template-readiness / process

- **R74 [machine]** — Postman parity gate: every controller/feature/endpoint `VERB path` appears in the
  collection (commented allowlist for browser-redirect flows). *(R66-cand; TR-6.)*
- **R75 [machine]** — Doc-map + QA-count sync (every `docs/*.md` in the CLAUDE.md map; the "N cases" figure
  matches the `^### QA-` count); config-catalog gate covers `GetEnvironmentVariable` + `GetSection` reads;
  DataProtection identity strings are frozen by test; the QA run-log block is append-only. *(R67/R68/R69/
  R81-cand; TR-1/2/8/9, S0-G5.)*
- **R76 [machine]** — The `Notes` exemplar models the shared `ErrorResponse` shape; `new { error … }`
  anonymous error objects are banned in `Features/**`; the R3 machine half (outbound-to-user-URL requests
  route through `IOutboundUrlGuard`) lands. *(R71/R76-cand; TR-5, S0-G4.)*

## Standing TDD mandate (review, carried from R7/CONTRIBUTING)

No production code without a failing test first at the right level; every slice ships happy-path +
permission-denied + cross-tenant-isolation tests before "done"; every new public method tested per branch +
error path; the shared harness is reused, never rebuilt; `QA_TEST_PLAN.md` is updated in the same PR; the
run log stays append-only. *(R99-cand mandate; R7.)*

---

*R1–R35 (v1.0) + R36–R76 (v3 consolidated) = FOUNDATION_RULES v2.0. Binding once the remediation plan in
`AUDIT_TASKS.md` is approved and its enforcement batch is green. `CONTRIBUTING.md`'s Definition of Solid is
updated in that batch to reference this file.*
