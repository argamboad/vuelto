# v3 Delta Audit — Phase 5 Consolidation Gate (record)

> The single reconciler and the only gate to implementation. Report-only.

## Integrity check

All four Phase 1–4 reports cite commit `5fc1762dc5487de26af0e515c34c264efaaa11a7` (verified — 4/4). No SHA
divergence; the gate proceeds.

## Step 1 — conflict resolution

All 13 `RULE_CONFLICTS.md` entries (C1–C13) resolved; resolutions applied in `FOUNDATION_RULES_v2.md` (see
its "Conflict resolutions applied" block). Precedence used, in order: (a) security/tenancy/auth invariants
from the adversarial pass outrank all; (b) a fix that would violate a higher rule is rejected; (c) machine
beats review when equivalent; (d) foundation-genericity beats slice convenience. Net effect: the ~65
candidate rules (R36–R100) consolidate to **R36–R76** final, with no contradictions and every machine rule
carrying an enforcement mechanism.

Two conflicts needed a real call rather than a mechanical merge:
- **C10** (R45 staff-gate vs R52 impersonated-write attribution): kept **both** — R45 blocks impersonation
  at the staff surface, R52 attributes ordinary tenant writes made during a *legitimate* impersonation.
  Neither subsumes the other; a "sign in as" session still performs attributable tenant writes.
- **C13** (R100 vs CAND-S0-1): R100 is the strict superset (fixes the regex miss *and* the Endpoints/ scope),
  so CAND-S0-1 retires into R36. The old `RouteGroupPrefixes_AreUnique` implementation is replaced, not
  extended — it never worked for feature slices.

## Step 2 — cross-check every proposed fix against the full surviving ruleset

This is the step that breaks the cycle: each remediation task was checked so it cannot re-introduce another
phase's flagged problem. No offender found; specifically confirmed:

- **B2-1** (wire orphaned entities into dissolve/export) uses `ITenantDataContributor` + `QueryAllTenants`
  inside `*DataContributor.cs` — the sanctioned hatch — so it does **not** violate R38's ban, and its
  set-based deletes go through the `EnterTenant`/contributor path required by R39/R44. ✔
- **B3-1** (AuditEvent.ImpersonatedBy column + migration) must ship its migration under the *append-only*
  audit constraint and, being on an `ITenantScoped`-adjacent table, must respect R42 (RLS policy in the same
  migration if the table is tenant-scoped — `AuditEvent` already is, so the column addition rides its
  existing policy). Checked: no new tenant table, so no new policy needed; the append-only interceptor still
  applies. ✔
- **B4-2** (outbox dead-letter on commit-fault) must not weaken the at-least-once + inbox-dedup contract
  (ADR-007) — the fix advances attempt bookkeeping, it does not drop messages, so idempotent redelivery
  (R57) is preserved. ✔
- **B5-1** (security headers) is gated on `Hosting:ServeWebClient` + non-Development, so it cannot break the
  API-only deploy topology or the E2E harness (which runs Development). ✔
- **B7-2** (client reconcile fixes) must keep R71's "no-op while impersonating" — the deep-link-preserving
  reload (R72) only changes the *target* of a reload that already respects the impersonation guard. ✔
- **B1-1** (RLS gate fix) does not relax any tenancy invariant — it makes the *test* faithful to production;
  the 7 existing tables already ship policies, so the corrected gate stays green for current code and only
  gains the ability to fail for a future policy-less table. ✔

No proposed fix violates a surviving R1–R76 rule.

## Step 3 — final ruleset

`FOUNDATION_RULES_v2.md` (R1–R35 carried from v1.0 + R36–R76 consolidated). Becomes binding when the
`AUDIT_TASKS.md` enforcement batch (B9) is green and `CONTRIBUTING.md`'s Definition of Solid references it.

## Step 4 — remediation plan

`AUDIT_TASKS.md` — 9 batches (B1 keystone gates → B9 enforcement), 5 human-decision escalations, full
finding→batch traceability. v2-regression-first ordering is moot (Step-0 found zero regressions), so the
plan opens with the Critical-tenancy keystone (the RLS parity gate, proven broken in Phase 4).

## Outcome

**Gate passes.** No two irreconcilable Critical invariants — the escalations (RLS-3 seam scope, NATIVE-12,
ADR-023, prod-deploy gating, ADM-4 priority) are scoping/priority decisions, not contradictions, and each is
attached to its batch. Implementation may begin **after approval**, test-first, one change at a time, B1
first, each verified against `FOUNDATION_RULES_v2.md`.
