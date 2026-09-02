# RULE_CONFLICTS — v3 delta audit

> Where two phases disagree, the earlier rule **stands** until Phase 5 resolves it. Phases 1–4 only *log*
> here; they never overturn. Phase 1 has no cross-phase conflicts (it is first), so the entries below are
> **candidate-rule overlaps and dependencies** Phase 5 must merge/order when promoting the R36–R76 block —
> plus the deference relationships to v1.0 (R1–R35). No entry proposes revising a binding v1.0 rule.

## Phase 1 — candidate-rule overlaps to merge at Phase 5

| # | Overlapping candidates | Nature | Suggested resolution (Phase 5 decides) |
|---|------------------------|--------|----------------------------------------|
| C1 | **R43** (extend hatch bans to `Endpoints/`) vs **R39/R41** (set-based-write ban, tag-literal ban) | R43 widens the *scope* of the R5-family scans; R39/R41 widen their *pattern*. They touch the same arch-test surface (`ArchitectureTests.cs:22-53`). | Merge into one strengthened tenant-hatch fitness test covering both scope (`Features/**` + `Endpoints/**`) and patterns (identifier, literal, set-based composition). |
| C2 | **R61** (`native-paths` names every native-affecting file) — filed by both DEP-5 and NAT-5 | Duplicate finding, single rule. | One rule; drop the duplicate ID at promotion. |
| C3 | **R55** (one SDK pin source) vs **R62** (pin every workload restore) | Both address version-float; R55 is the .NET SDK, R62 is the MAUI workload set. Related, not identical. | Keep as two rules under one "pinned-toolchain" heading; the CLAUDE.md bump-together playbook (R55) should list the workload `--version` locations (R62). |
| C4 | **R36** (RLS gate tests migrations alone) + **R70** (RLS slice recipe documented) + **R37/R43/R39** | The RLS new-slice contract is one guarantee enforced by several candidates (gate correctness, documented recipe, scope of bans). | Phase 5 should treat "a new tenant table is RLS-protected by construction" as the parent invariant and confirm the set (R36 machine keystone + R70 review recipe + R37/R39/R43 scope) fully covers it without gaps or redundancy. |
| C5 | **R57** (deploy fails, not skips, on missing smoke config) — spans DEP-6 (staging) and DEP-7 (prod) | Same principle, two jobs; DEP-7 also asks for prod smoke + concurrency. | One rule for the fail-not-skip principle; DEP-7's prod-smoke/concurrency additions are separate remediation tasks, not extra rules. |
| C6 | **R45/R50** (reject `impersonated_by` on staff + mutating account endpoints) vs **R38** (no implicit RLS/system bypass) | Both are "a token's *derived* authority must be explicit," at different layers (HTTP authz vs DB scope). Thematically one doctrine. | Keep as separate enforceable rules (different mechanisms) but cross-reference under an "explicit-authority" note in v2.0. |
| C7 | **R44** (security notifications bypass prefs) vs existing NOTIFY preference honoring | R44 carves a fail-open-on-delivery exception into the deliberately-fail-quiet preference system. Not a conflict with a rule, but with a design intent. | Confirm the `security.*` kind namespace is well-defined and finite before making the bypass machine-enforced. |

## Deference to v1.0 (R1–R35) — no revisions proposed

- All v3 candidates are **additive**. R36–R43 strengthen the tenancy family (R2/R5/R32) *without* relaxing
  it — the RLS backstop is a second wall under the EF filter, and R36 fixes the *test* that guards it, not
  the invariant.
- **R76** finally lands the machine half of **R3** (flagged in v2 as "review→machine when the seam lands";
  the seam has landed). This is completion of R3, not a conflict.
- **R52** lands the standing **R21** gate that v2 specified but never shipped. Completion, not conflict.
- **R43/R66** extend **R35** (route uniqueness) and the Postman rule to the `Endpoints/` surface created by
  the B9-6 move. Scope extension, not revision.
- No v3 finding contradicts a v1.0 rule or re-files a v2 REF-*/adjudicated item. Items deliberately **not**
  re-filed are listed in each auditor's raw notes (e.g. the instance-local `IMemoryCache` MFA single-use
  under the single-instance topology; `TenantMembership`/`Notification`/`WebhookDelivery` carrying
  `TenantId` without `ITenantScoped` per the R2 allowlist; the OTP-lockout window semantics adjudicated
  Held at v2 CONF-5/6).

## Phase 3 — candidate-rule overlaps to merge at Phase 5

| # | Overlapping candidates | Nature | Suggested resolution |
|---|------------------------|--------|----------------------|
| C8 | **R82/R83** (tenant-axis dissolution+export canary; dissolve-inside-EnterTenant) vs **R37** (review-only "dissolve enters tenant") and **R12/R13** (user-axis erasure canary + unique export keys) | R82 is the tenant-axis mirror of R12; R83 is the machine half of R37. | Promote R82 as the tenant-axis completeness gate; fold R83 into R37 as its machine enforcement; keep R12/R13 as the user-axis siblings. One "every scoped entity is torn down + exported, under the right tenant" family. |
| C9 | **R85/R86/R87** (atomic single-use consume; atomic counter; 2nd-factor-after-confirm) vs **R30** (atomic quota) and **R46** (MFA cap exists) | R30/R46 concern *existence* of a cap/atomic-quota; R85–R87 concern *atomicity/ordering* of the consume — orthogonal but adjacent. | Keep all; group under an "atomicity of security-state mutations" heading. R86 explicitly generalizes R30's pattern to lockout counters. |
| C10 | **R92** (impersonated-write attribution) vs **R45** (reject `impersonated_by` at staff gate) | R45 blocks impersonation at the *staff* surface; R92 requires attribution for *ordinary tenant* writes made during impersonation. Complementary, not duplicate. | Keep both; note that R45 does not subsume R92 (a legitimate ADR-014 "sign in as" still performs tenant writes that must be attributed). |
| C11 | **R95** (RCL component-test host) + **R96/R97** (reconcile guards) vs **R73** (reconcile-reload E2E preserves deep-link) and **R98** (same, review) | R73/R98 are the E2E/behavior rule; R95 is the fast-test *seam* they depend on; R96/R97 are the specific client invariants. | R95 is prerequisite infrastructure (do first); R96/R97 are unit/component-testable once R95 lands; R73/R98 stay as the E2E backstop. Merge R98 into R73 (same rule, two phases). |
| C12 | **R99** (harness seams) vs Phase-2 **R77–R81** (coverage/tooling) and the harness notes throughout | R99 enumerates missing *test* seams (concurrency runner, fault injector, clocks, impersonation helper, bUnit chassis); R77–R81 are supply-chain/coverage tooling. Adjacent, not conflicting. | Keep R99 as the harness-readiness rule; its bUnit-chassis clause is the concrete form of R95. |

## Open cross-phase items

- Phase 2 (tools) — DONE. Confirmed all static findings; DEP-4 refined (reproduced live lockfile churn);
  4 tool-only findings (TOOL-1..4); rules R77–R81.
- Phase 3 (logic/tests) — DONE. 16 new logic bugs (headline LB-TEN-1, verified), ~79 test specs, harness
  gaps; rules R82–R99. Confirmed RLS-1/ADM-3/UX cluster as executable specs (TB-TEN-4/5, TB-AUTH-3,
  TB-UI-1/2/5).
- Phase 4 (adversarial slice) — DONE. Built the `Projects` throwaway slice; **proved RLS-1 live** (policy-less
  migration passed `RlsMigrationGateTests`), **proved TR-4** (EF scaffolds no RLS DDL), **proved LB-TEN-1/R82
  gap** (no tenant-axis canary), **proved TR-5** (exemplar copy reproduces the banned error shape), and found
  **ADV-P4-1** (route gate regex misses the mandated helper → rule R100). Forced 2 core edits (AppDbContext +
  Program.cs) → Assurance Claim 3 fails for entity-bearing slices. `EveryEntity_IsDocumentedInDataModel`
  correctly caught the undocumented slice (a gate that *works*, the model for the broken ones).

## Phase 4 — candidate-rule overlap

| # | Overlapping candidates | Nature | Suggested resolution |
|---|------------------------|--------|----------------------|
| C13 | **R100** (route gate matches `MapTenantFeatureGroup` + scans Controllers/Features/Endpoints) vs **CAND-S0-1** (extend the R35 route scan to `src/Api/Endpoints/`) and **R35** (original route uniqueness) | R100 is the corrected, superset form of CAND-S0-1 — it fixes both the *regex* (helper miss, ADV-P4-1) and the *scope* (Endpoints/, S0-G1). | Promote R100 as the single route-uniqueness rule; retire CAND-S0-1 into it; R100 replaces the broken `RouteGroupPrefixes_AreUnique` implementation. |
