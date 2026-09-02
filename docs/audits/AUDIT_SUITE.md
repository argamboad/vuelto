# The Hardening Suite — a repeatable super-audit for this template and its clones

> **What this is.** The single, self-contained process that produced `audits/v1-2026-06/`,
> `audits/v2-2026-07/`, and `audits/v3-2026-07/`. It hardens a template (or a downstream clone) to
> "solid by construction": a diagnostic → gate → remediation pipeline whose output is a set of **CI gates**
> that then keep the property true without anyone re-auditing. Run it when a trigger fires (Part 0); follow
> it top to bottom; each phase's output is a file under `audits/v<N>-<YYYY-MM>/`.
>
> **What this is not.** A cadence. `CONTRIBUTING.md` is deliberate: *do not run a routine discovery audit —
> keep the gates green instead.* The gates this suite emits **are** the ongoing audit. You run the suite
> again only when a trigger says the gates no longer cover reality.

---

## Part 0 — Philosophy, triggers, and scoping

### Why it exists
A reviewer asserting "this is solid" doesn't survive the next fifty commits. A **fitness test** does. Every
phase here ends by converting findings into machine-enforced rules (`FOUNDATION_RULES.md` + arch tests +
`ci.yml`), so the finish line is *provable by the suite + CI*, not by memory. The suite is the one-time cost
that buys a standing guarantee.

### When to run it (triggers)
Run a pass when — and only when — one of these fires:
- **A structural change to the core architecture** (the tenancy model, the auth model, the slice contract,
  the persistence/RLS layer). This warrants a **full** pass.
- **A new wave of platform epics** landed since the last settled run (v2 → the platform epics; v3 → DEPLOY/
  NATIVE/RLS-backstop/THEME/PREFS). A **delta** pass.
- **A major dependency or runtime bump** (a .NET major, EF major, a swap of a load-bearing package).
- **Before generating the first production app** from the template, and before any subsequent clone ships to
  prod. (At minimum re-run **Phase 4**, the adversarial slice pass — the cheapest way to re-prove the
  isolation guarantees after any horizontal concern changed.)

Between triggers: **keep the gates green, don't re-audit.**

### Full vs. delta
- **Full** (v1): audit everything at a pinned commit; the first pass on a codebase.
- **Delta** (v3 is the reference): audit only what changed since the last **settled** run. Method:
  1. Find the prior run's "done" commit — the commit that closed its remediation (`git log` for the batch
     that marks the prior suite complete). Call it `BASE`.
  2. `git log --oneline --first-parent <BASE>..HEAD` — that PR list **is** the audit surface.
  3. Read the prior run's `AUDIT_REPORT.md` §"settled"/refuted items and treat them as adjudicated — **do
     not re-file** them unless the code changed under them.
  4. Scope every phase below to the delta. Prior `FOUNDATION_RULES` numbers **carry forward unchanged**; new
     rules append.

### Integrity spine (every phase obeys these)
1. **Pin one commit for the whole run.** Every phase report states the SHA at the top and **stops** if it
   differs from a prior phase's SHA. (Audit docs committed on top don't change the *code* SHA being
   analyzed — note that explicitly.)
2. **Each report opens with a `## SUMMARY`**: commit SHA · findings by severity · (Phase 1 only) prior-run
   regressions found · rules added (by number) · conflicts logged. Phase 5 reconciles from summaries.
3. **Phases 1–4 diagnose only. No phase writes or fixes production code before Phase 5.** A contradiction
   between two reports is harmless until someone acts on it — and nobody acts until Phase 5.
4. **Later phases defer to earlier ones.** A phase may ADD a rule or FLAG one for revision, never silently
   overturn it. Disagreements go to `RULE_CONFLICTS.md`; the original stands until Phase 5.
5. **Phase 5 is the only reconciler and the only gate to implementation.**

---

## Phase 1 — Comprehensive static audit → `AUDIT_REPORT.md`

Deep static read of the (delta) code. Breadth-first catalog, then deep-dive the top-severity areas.

- **STEP 0 first — verify prior remediations held.** For a delta pass, load every resolved finding from the
  prior run's `AUDIT_TASKS.md` and classify each: **Held** (fix present *and* effective — for the ones
  backed by a standing CI gate, confirm the gate exists and passes, that's the verification), **Regressed**
  (reverted/weakened by later work — flag Critical/High and *loudly*; a re-broken guarantee is worse than
  one never fixed), or **Superseded** (later work legitimately changed the approach — confirm intent
  preserved). The high-value targets are the fixes **without** a standing gate. Output a "prior-remediation
  status" table before fresh discovery.
- **Template-readiness is first-class.** Can a developer add a `Features/<X>/` slice touching only slice
  code? Does a new slice inherit tenancy, authz, i18n, logging/audit, outbox — or re-implement any
  (re-implementation burden = foundation defect)? Does core ever depend on a slice? Which good practices are
  enforced by inherited CI gates vs. only documented (the doc-only ones are what clones lose)?
- **Then fresh discovery**: architecture-vs-docs divergence; doc↔code sync; cross-module contradictions;
  gaps (error handling, validation, authz/tenancy holes, injection, secrets, unsafe defaults); debt & SOLID.
- **Finding IDs**: stable, area-prefixed, continuing the scheme (v3 used `RLS-n`, `DEP-n`, `ADM-n`, …).
- **Rules output**: for every recurring/systemic finding, write a concrete enforceable rule into
  `FOUNDATION_RULES.md` (candidate block). Number them continuing from the prior run's last rule. Mark each
  machine-enforceable (prefer extending `tests/Api.Tests/ArchitectureTests.cs` + `ci.yml`) vs review.
- End with a prioritized top-10, a foundation-readiness verdict, and unknowns needing a human decision.

## Phase 2 — Tool reconciliation → `AUDIT_RECONCILIATION.md`

Run the tools; reconcile them against Phase 1. Read Phase 1 + `FOUNDATION_RULES.md` first.

- **Run and capture** (raw output under `audits/v<N>-<YYYY-MM>/tooling/`): `dotnet build` (warnings-as-error
  is on), `dotnet test` + coverage, `dotnet list package --vulnerable --include-transitive` + `--deprecated`,
  a license scan, and a complexity/duplication proxy. Docker up ⇒ the Testcontainers integration + RLS gates
  actually execute (do that — don't assume).
- **Reconcile**: Confirmed (tools corroborate — promote severity if worse) · Contradicted/false-positive
  (tools disprove — the only ground to *remove* a Phase-1 rule; log it) · Tool-only (missed by static —
  complexity hotspots, real CVEs, nullable holes, coverage gaps) · Unresolvable (static vs. tools — mark for
  Phase 5). Then a merged, de-duplicated, re-prioritized worklist with a confidence tag each.
- **Supply-chain depth** (every clone inherits these): unmaintained/single-maintainer load-bearing deps;
  license compatibility with commercial use (High); lockfile integrity; transitive bloat.
- **Deference rule**: add findings/rules freely; remove a Phase-1 rule *only* if a tool proves its finding
  false (log in `RULE_CONFLICTS.md`). Mere disagreement → log and leave standing.
- Produce the **enforcement backlog**: ordered checks to add, each mapped to its rule, each specified as an
  `ArchitectureTests` test / analyzer / `Directory.Build.props` rule / `ci.yml` step.

## Phase 3 — Logic bugs & test-completeness → `LOGIC_AND_TEST_REPORT.md`

Two jobs the structure can't reveal. Read Phases 1–2 + `FOUNDATION_RULES.md` first.

- **Part A — wrong-RESULT bug hunt.** Per module/hotspot (prioritize the delta epics): infer intended
  behavior, then find where the implementation yields a wrong result — boundary/off-by-one, inverted
  conditionals, null/empty/zero/negative, ordering/uniqueness/idempotency (outbox at-least-once + inbox
  dedup), time-zone/date math, currency/rounding, retry/partial-failure, and tenancy that is *structurally*
  present but computes the *wrong scope* (`EnterTenant` targets, impersonation, cross-tenant teardown). Give
  the exact triggering input → wrong output → correct behavior → confidence. **The green suite can't see
  concurrency/fault/ordering bugs if there is no concurrency/fault test** — hunt there.
- **Part B — test-completeness, written test-first.** Enumerate what *should* be tested (unit per
  branch/error path; integration per cross-module contract + each migration & rollback; E2E per critical
  journey) and list every gap as a **spec** (name, arrange/act/assert intent) — NOT implemented code.
  **Tenancy-isolation negatives are the Critical class**: explicit "tenant A cannot read/write/export/erase
  tenant B" at every layer. Assess whether the shared harness lets a new slice test out of the box or forces
  rebuilding (concurrency runner? DB-fault seam? injected clock on time-sensitive helpers? client
  component-test chassis?).
- **Rules output**: TDD invariants (no production code without a failing test first; every slice ships
  happy-path + permission-denied + cross-tenant-isolation before "done"; harness reused not rebuilt; QA plan
  updated in the same PR; run log append-only).

## Phase 4 — Adversarial build-a-slice → `ADVERSARIAL_REPORT.md`

On a **throwaway branch** (disposable code, never merged), prove the foundation can still host a slice by
building one and attacking it. Read all prior reports first. **Build test-first, copying the `Notes`
exemplar.**

1. **Scaffold one realistic slice** (e.g. `Projects`: a `Core` entity implementing `ITenantScoped`, a
   `Features/<X>/` slice with endpoints/handler/models/`ITenantDataContributor`, its config, a migration via
   `dotnet ef`) **by the intended mechanism only**. **Log every forced core edit** — each is a foundation
   defect. This is the headline.
2. **Attack the inherited boundaries**: tenancy (read/write another tenant; forge a tenant id; bind
   `TenantId` from input; chain `IgnoreQueryFilters`; raw context — does the write-stamp + global filter +
   RLS hold, or did isolation depend on the slice remembering?); auth/authz (unauth, wrong-role,
   wrong-tenant — inherited without re-implementation?); a **second stub slice** that reuses a route
   prefix / adds a migration + i18n bundle in the same namespace space (do routes collide, migrations
   misorder, i18n keys clash, and — critically — do the *gates catch it*?).
3. **Run the full suite** (incl. the slice's tests). A gate that correctly reacts to the new slice is
   working; one that doesn't is a must-fix.
4. **Assurance-claims table** — for each guarantee every clone must hold, state Supported/Contradicted/
   Insufficient + verdict (Holds / Holds conditionally / Fails):
   1. No slice can access another tenant's data, even written carelessly.
   2. Every slice route inherits auth/authz without re-implementing it.
   3. A slice can be added touching only slice code — zero core edits.
   4. A slice's migrations/config/i18n cannot collide with core or another slice.
   5. Cross-tenant paths (`EnterTenant`, impersonation, export/erasure) preserve isolation.
- **Rules output**: every breach or forced core edit → a hard invariant, machine-enforceable, outranking
  softer style rules. The slice stays on the throwaway branch; only the report + rules graduate.

> *v3's Phase 4 is the model: it built `Projects`, **proved a gate tautological live** (a policy-less
> migration passed the RLS parity gate), found a **new** gate bug (a route-uniqueness regex that never
> matched the mandated helper), and showed "zero core edits" fails for an entity-bearing slice — none of
> which the four diagnostic phases had proven end-to-end.*

## Phase 5 — Consolidation gate → `FOUNDATION_RULES_v<N>.md` + `AUDIT_TASKS.md`

The only phase that resolves contradictions and the only gate to implementation. Read every report's
`## SUMMARY`, then `FOUNDATION_RULES.md` + `RULE_CONFLICTS.md` in full. Verify all reports share the SHA.

1. **Resolve every `RULE_CONFLICTS.md` entry.** Precedence: (a) security/tenancy/auth invariants from the
   adversarial pass outrank all; (b) a fix that would violate a higher-precedence rule is rejected/replaced;
   (c) machine-enforceable beats review-enforced when equivalent; (d) foundation-genericity beats slice
   convenience. Record each resolution.
2. **Cross-check every proposed fix against the full ruleset** — confirm no fix violates a surviving rule or
   re-introduces another phase's flagged problem. This step breaks the cycle.
3. **Emit `FOUNDATION_RULES` final** (de-duplicated, non-contradictory, numbered, each tagged machine/review
   with its enforcement mechanism). Prior rules carry their numbers; the run's rules consolidate on top.
4. **Emit `AUDIT_TASKS.md`** — the remediation plan in **one-session batches**, stable finding IDs,
   **done-when + verify command per task**, **keystone first / enforcement last**. Order: prior-run
   regressions → Critical security/tenancy → correctness (logic bugs) → test gaps (test-first) → debt/SOLID
   → docs → the enforcement batch (the new arch tests / CI gates). **For every regressed prior fix, the
   remediation must add a standing gate** so it can't regress a third time. Each item notes its rule/finding,
   the tests that must exist first, and whether it touches core.
- Two irreconcilable Critical invariants → **escalate as a human decision**, don't pick arbitrarily.
- Report-only until the plan is approved. Then implement test-first, one change at a time, each verified
  against the final rules. Update `CONTRIBUTING.md`'s Definition of Solid if new invariants were added.

## Phase 6 — QA paranoia → updates `QA_TEST_PLAN.md` (+ regen PDFs)

A functional QA plan is not a paranoid one. Make the *manual* plan exercise the adversarial classes the audit
found — because the automated net has holes exactly where the manual plan defers to it.

- Read the plan; catalog how many cases are true isolation/security vs happy-path.
- For each adversarial class in the reports (cross-tenant read/export/erase; impersonation abuse +
  attribution; brute-force; GDPR completeness across *all* tenant-scoped tables; webhook replay/ordering;
  auth single-use/rotation races; locale×invite deep-links; security headers; native release safety) —
  is there a manual case? If not, add one.
- Add a dedicated **`QA-ADV-*` "Adversarial & tenant-isolation"** section. **EXPECT-FAIL discipline**: cases
  that assert behavior the audit says is *currently broken* are flagged **⚠️ PENDING remediation** and
  recorded **Blocked**, never Pass — each becomes the acceptance check a remediation task later flips green.
- **Append-only**: never renumber existing case IDs; append traceability + sign-off rows; bump the count in
  the plan and the CLAUDE.md doc-map; regenerate the PDFs (`gen_qa_guide.py`/`gen_qa_runlog.py`) and confirm
  `check_qa_artifacts.py` is green.

## Phase 7 — Docs & course currency → updates `docs/tutorial/**` + doc map

Keep the teaching materials and the doc map honest with the code the suite just examined.

- **Coverage gate**: `python docs/tutorial/gen_coverage.py` must report **0 unmapped** — every tracked file
  maps to the lesson that builds it. Map any new files.
- **Content drift**: for each feature that landed since the last reconcile, confirm the owning lesson
  actually *teaches* the current behavior; reconcile stale prose and add ADR references. A feature bucketed
  into a lesson's file list but absent from its prose is an orphan — fix it.
- **The maintenance rule**: reconcile lessons + coverage + PDF **on the branch that changes the code**, not
  in a later sweep. Quote from the branch under audit, never a stale worktree.
- Same-PR obligations that keep the docs from drifting: endpoint changes update the Postman collection;
  entity/config changes update `DATA_MODEL.md`/`.env.example`; new docs update the CLAUDE.md map. These have
  standing gates — Phase 7 just confirms they held and reconciles what slipped.

---

## Outputs & conventions

Everything for one run lives in **`docs/audits/v<N>-<YYYY-MM>/`**:

| File | Phase | Contents |
|------|-------|----------|
| `AUDIT_REPORT.md` | 1 | static findings + Step-0 prior-remediation status + top-10 + verdict |
| `AUDIT_RECONCILIATION.md` | 2 | tool reconciliation + supply-chain depth + enforcement backlog |
| `tooling/` | 2 | raw tool output (build/test/coverage/vuln/deprecated/license), distilled |
| `LOGIC_AND_TEST_REPORT.md` | 3 | Part-A logic bugs + Part-B test specs + harness readiness |
| `ADVERSARIAL_REPORT.md` | 4 | forced-core-edit log + attacks + assurance-claims table |
| `FOUNDATION_RULES.md` | 1–4 | candidate rule block (grows per phase) |
| `RULE_CONFLICTS.md` | 1–5 | logged disagreements + Phase-5 resolutions |
| `PHASE5_GATE.md` | 5 | integrity check + conflict resolutions + cross-check record |
| `FOUNDATION_RULES_v<N>.md` | 5 | the **final** consolidated ruleset (binding once the enforcement batch is green) |
| `AUDIT_TASKS.md` | 5 | the batched remediation plan (done-when + verify per task) |
| `IMPLEMENTATION_TRACKER.md` | post-5 | task-granular status board for the discuss-then-implement loop |

- **`FOUNDATION_RULES` versioning**: R-numbers are permanent and carry across runs (R1–R35 from v2 are still
  R1–R35 in v3's v2.0). Each run appends a numbered block, then Phase 5 consolidates. Downstream: the *final*
  `FOUNDATION_RULES_v<N>.md` of the latest run is the binding file; `CONTRIBUTING.md` points at it.
- **The throwaway slice** (Phase 4) stays on its own `…-phase4-throwaway` branch, never merged.

## How to run it — orchestration recipe (what actually worked)

- **One branch per phase**, stacked, each committing that phase's docs (`audit/v<N>-<YYYY-MM>-phase<k>`).
  The code SHA under audit is constant; the audit docs accrete.
- **Fan out one diagnostic agent per area** in Phases 1 and 3 (tenancy/RLS, auth/admin, billing/jobs,
  deploy/CI, native, client/UI, docs/template-readiness). Give each the pinned SHA, the prior reports to
  defer to, and its area. Save each raw report to scratch, then synthesize into the phase file. This is how
  a large delta gets audited breadth-first without one context holding everything.
- **Verify the load-bearing findings yourself** (don't relay an agent's Critical unchecked) — e.g. v3's RLS-1
  was re-confirmed by reading the harness before it headlined.
- **Git discipline** (repo rules): never touch `main`; work on `develop`-based branches; **confirm before
  `git push`**; for remediation, one branch/PR per slice (or per keystone group), merged sequentially.
- **Remediation is keystone-first**: fix the broken *enforcement gates* before the findings they should
  catch, each test-first so the corrected gate fails on current code, then passes.

## Reference instances & cross-links

- **Worked examples**: `audits/v1-2026-06/` (first full pass), `audits/v2-2026-07/` (platform-epics
  extension), `audits/v3-2026-07/` (the delta pass — the closest model for a re-run).
- **The binding bar**: `CONTRIBUTING.md` (Definition of Solid) → the latest run's final `FOUNDATION_RULES`.
- **The slice contract** the adversarial pass exercises: `docs/WAYS_OF_WORKING.md` + ADR-004 + the `Notes`
  exemplar.
- **Standing instruction** (also in `CLAUDE.md`): foundation rules are binding; TDD is mandatory; a slice
  ships happy-path + permission-denied + cross-tenant-isolation tests before "done"; re-run **Phase 4** on
  any horizontal-concern change and before generating a production app; a **full** re-run only after a
  structural core change.

---
*Order matters: this suite hardens correctness. Converting the remaining doc-only good practices into
inherited CI gates (so the solidity survives a no-upstream clone) is the natural follow-on — and Phases 6–7
plus the enforcement batch of Phase 5 are where that conversion happens each run.*
