# v3 Delta Audit — Remediation Plan (AUDIT_TASKS)

> **Status: PROPOSED — report-only. No production code is written until this plan is approved.**
> Consolidated from Phases 1–4 by the Phase 5 gate. Every task cites its finding + rule (final numbering
> from `FOUNDATION_RULES_v2.md`), states its **done-when** + **verify** command, marks whether it **touches
> core**, and names the **test(s) that must exist first** (TDD is mandatory — R7).
>
> **Ordering** (per the suite): v1/v2 regressions → Critical security/tenancy → correctness (logic bugs) →
> test gaps (test-first) → debt/SOLID → docs → enforcement last. **There were zero v2 regressions** (Step-0:
> 46/47 held), so the plan opens with the Critical-tenancy keystone. Each batch is one focused session.
>
> **Integrity:** all four reports audit `5fc1762`. Base new work on current `develop` and re-verify the
> findings still reproduce before fixing (some are near the drift line).

## Escalations — human decisions needed before the affected batch

1. **R40 / RLS-3 (system-scope seam).** The fix (`EnterSystem()`/`ISystemScope`) touches the outbox
   dispatcher, scheduler, sweeps, and pre-auth flows — a core-surface change. **Decide:** build the explicit
   seam now, or ship the interim "log-on-null-tenant-bypass" and defer the seam. Affects **B6**.
2. **NAT-1 (NATIVE-12).** Is the unpushed `feat/native-oauth-resilience` branch meant for `develop`?
   **Decide:** push+merge, or log as intentionally-parked in `docs/stories/native.md`. Affects **B9**.
3. **ADR-023.** Referenced by the brief but absent from the repo. **Decide:** author it (distribution
   downstream) or confirm the ADR-018 amendment is canonical. Affects **B12**.
4. **DEP-7 (prod deploy gating).** Prod activation is downstream (ADR-018 amendment). **Decide:** harden
   `deploy-prod` now (template hygiene) or defer with the rest of prod activation. Affects **B8**.
5. **ADM-4 severity.** Recovery-code offline-crack risk depends on the DB/backup threat model. **Decide** the
   priority (keep in B5 vs defer). Affects **B5**.

---

## B1 — Keystone: make the tenancy enforcement gates actually enforce

*The gates that guard tenancy must work before anything else is trusted. Each fix is test-first: the
corrected gate should FAIL on current code (proving the defect), then pass once the code is fixed.*

- **B1-1 — Fix the RLS parity gate (RLS-1 / R37).** Split `RlsTestSetup.ProvisionAsync` into
  role/grants vs policy-DDL; `IntegrationTestFactory` applies role/grants only (migrations create policies);
  keep the policy-DDL variant for the `EnsureCreated` `RlsBackstopTests`. Add a meta-assertion that
  `IntegrationTestFactory` never references `RlsDdl.StatementsFor`. **Touches core:** no (tests/harness).
  **Test first:** `RlsMigrationGate_FailsForPolicylessTenantTable` (TB-TEN-4) — introduce a policy-less
  mapped entity, assert the gate reports it. **Done-when:** the gate fails for a policy-less table and passes
  for the current 7. **Verify:** `dotnet test --filter RlsMigrationGate` (fails on the seeded defect, green
  after). *Keystone — the Phase-4 proof (ADV-P4-2) makes this the top must-fix.*
- **B1-2 — Fix the route-uniqueness gate (ADV-P4-1 / R36).** Rewrite `RouteGroupPrefixes_AreUnique` to match
  `MapTenantFeatureGroup("…")` (and raw `MapGroup`, `[Route]`) and to scan `Controllers/ + Features/ +
  Endpoints/`. **Touches core:** no (tests). **Test first:** a fixture with two groups sharing a prefix must
  fail. **Done-when:** a duplicate `/api/<x>` across any two surfaces fails CI. **Verify:**
  `dotnet test --filter RouteGroupPrefixes` (fails on a seeded collision).
- **B1-3 — Add the tenant-axis completeness canary (LB-TEN-1 / R43).** Add
  `EveryTenantScopedEntity_IsWiredIntoTenantDissolution` (registered contributor or teardown allowlist),
  mirroring the user-keyed canary; cover `TenantId`-carrying non-`ITenantScoped` entities. **Touches core:**
  no (tests). **Done-when:** the canary **fails today** on `ApiKey`/`UsageCounter`/`WebhookSubscription`
  (that failure is the proof; B2 makes it green). **Verify:** `dotnet test --filter IsWiredIntoTenantDissolution`.
- **B1-4 — Extend the hatch/route bans to `Endpoints/` (S0-G1/G2 / R36, R38).** Widen the
  `IgnoreQueryFilters`/`QueryAllTenants`/`RlsTags`/`rls:cross-tenant`-literal scans to `src/Api/Endpoints/`.
  **Touches core:** no. **Verify:** `dotnet test --filter FeatureSlices_DoNotBypassTheTenantFilter`.

## B2 — Critical tenancy: close the data-orphaning + set-based-write holes

- **B2-1 — Wire the three orphaned entities into dissolve + export (LB-TEN-1 / R43).** Add
  `ITenantDataContributor`s (or teardown+export allowlist entries) for `ApiKey`, `UsageCounter`,
  `WebhookSubscription`; handle `WebhookDelivery`. Export must be secret-free (no key hash, no webhook
  secret). **Touches core:** yes (new contributors + Program.cs registration). **Test first:** TB-TEN-2
  (`Dissolve_WipesEveryTenantScopedTable`), TB-TEN-3 (`Export_IncludesEveryTenantScopedSection`). **Done-when:**
  B1-3's canary is green and dissolve/export cover all `ITenantScoped` tables. **Verify:** `dotnet test`.
- **B2-2 — Dissolve/erasure enters the target tenant (RLS-2 / R44).** Wrap `TenantDissolutionService
  .DissolveAsync` body in `EnterTenant(targetTenantId)`; add the caller scan. **Touches core:** yes.
  **Test first:** TB-TEN-5 (`Dissolve_UnderForeignEnteredTenant_DoesNotSilentlyOrphan`, RLS-role harness).
  **Verify:** `dotnet test --filter Dissolve`.
- **B2-3 — Ban `QueryAllTenants`/`IgnoreQueryFilters` composed with set-based writes (RLS-4/RLS-8 / R39).**
  Add the multiline source scan; fix the four live sites (incl. `BillingDataContributor.WipeAsync`,
  `ApiKeyService` stamp) to `EnterTenant`/system-scope first. **Touches core:** yes. **Test first:** the
  arch scan + TB-TEN-9. **Verify:** `dotnet test --filter ExecuteDelete`.
- **B2-4 — RLS interceptor hardening (RLS-5/RLS-7 / R41).** Implement `TransactionFailed` cache
  invalidation; anchor tag detection to the leading comment block. **Touches core:** yes (interceptor).
  **Test first:** commit-failure integration test + marker-in-literal unit test. **Verify:** `dotnet test --filter Rls`.

## B3 — Critical auth: impersonation, brute-force, atomicity, hashing

*(B3-2 needs the concurrency-runner harness seam — build it first here or pull B10-1 forward.)*

- **B3-1 — Reject impersonation tokens on privileged/mutating surfaces (ADM-2/ADM-8 / R45) + attribute
  impersonated writes (LB-ADM-1 / R52).** Reject `impersonated_by` in `RequireStaffAsync` and on account-
  preference endpoints; add an `AuditEvent.ImpersonatedBy` column threaded through the audit write.
  **Touches core:** yes (+ migration incl. RLS policy per R42). **Test first:** TB-ADM-1/2/3. **Verify:**
  `dotnet test --filter Impersonat`.
- **B3-2 — Per-user MFA brute-force cap + atomic security-state (ADM-3/R47, LB-AUTH-1/2/3 / R48/R49/R50).**
  Add a per-user IP-independent MFA lockout; make single-use consume atomic (conditional `ExecuteUpdate`);
  make the OTP lockout counter atomic; consume the MFA challenge before the stateful factor check.
  **Touches core:** yes. **Test first:** TB-AUTH-3/4/5/6 (concurrency runner required). **Verify:**
  `dotnet test --filter Mfa|Otp|MagicLink`.
- **B3-3 — Security notifications bypass prefs + recovery-code hashing (ADM-1/R46, ADM-4/R51).** `security.*`
  kinds force both channels; hash recovery codes under HMAC+pepper (or lengthen entropy). **Touches core:**
  yes. **Test first:** TB-ADM-5, TB-AUTH-8. **Verify:** `dotnet test --filter Notification|Recovery|TokenHasher`.
- **B3-4 — Config-gate posture gate (S0-G3 / R53).** Empty-config test for every `*Settings`. **Touches
  core:** no (tests). **Verify:** `dotnet test --filter Settings`.

## B4 — Correctness: billing / jobs logic bugs

*(Needs the DB-fault-injection + injectable-clock harness seams — build them first here.)*

- **B4-1 — Webhook recency strictly-older (LB-BILL-1 / R56).** Compare `<`, not `<=`; two same-second
  distinct events both apply. **Test first:** TB-BILL-1. **Touches core:** yes. **Verify:** `dotnet test --filter Webhook`.
- **B4-2 — Outbox dead-letters on commit-fault (LB-BILL-2 / R57).** Advance attempt/backoff on any completion
  failure. **Test first:** TB-BILL-6. **Touches core:** yes. **Verify:** `dotnet test --filter Outbox`.
- **B4-3 — Quota catches only 23505 (LB-BILL-3 / R58).** Narrow the catch. **Test first:** TB-BILL-3.
  **Touches core:** yes. **Verify:** `dotnet test --filter Quota`.
- **B4-4 — Dunning-on-transition + comp/revert liveness (LB-BILL-4/R59, ADM-5).** Suppress cold-start
  dunning; key the 409 on status liveness. **Test first:** TB-BILL-2, TB-ADM-6/7. **Touches core:** yes.
  **Verify:** `dotnet test --filter Dunning|Comp`.

## B5 — Deploy / web-host hardening

- **B5-1 — Security + cache headers on the single-origin host (DEP-2/DEP-3 / R62).** HSTS/nosniff/frame-
  ancestors + SPA `no-cache` / assets `immutable` when serving the web client outside Development. **Test
  first:** integration test over the host. **Touches core:** yes (Program.cs). **Verify:** `dotnet test --filter SingleOrigin`.
- **B5-2 — CI hardening (DEP-6/7/8/9 / R63).** Fail-not-skip on missing smoke config; top-level
  `permissions:`; pin mailpit + checksum downloads; move QA pip pins to a manifest. **Touches core:** no
  (CI). **Verify:** workflow lint + a dry-run.
- **B5-3 — Stripe key mode guard + SDK pin (DEP-10/R64, DEP-4/DEP-11/R61).** `ExpectLiveKey` fail-closed
  guard; single SDK pin source + `global.json` + bump-together playbook; `--version` on workload restores.
  **Test first:** `StripeKeyMode` startup test. **Touches core:** yes (startup) + CI. **Verify:**
  `dotnet test --filter BillingProviderRegistration` + `dotnet restore --locked-mode`.

## B6 — Native

- **B6-1 — Release dev-wiring guard (NAT-3 / R67).** Fail Release builds with a localhost API base; scope
  cleartext config + env override to Debug. **Touches core:** yes (MAUI build). **Verify:** a Release build fails without a real base URL.
- **B6-2 — Native CI correctness (NAT-4/DEP-5≡NAT-5/NAT-6 / R60, R68).** `native-paths` regex adds
  `Directory.Build.props`; non-vacuous smoke assertion; fix the `companyname` logcat grep; workload `--version`
  pins (R61). **Touches core:** no (CI). **Verify:** CI dry-run.
- **B6-3 — Loopback OAuth state + local-dev nits (NAT-8/9/10/11).** Add `state` to the loopback flow; fix
  the plist comment, 0600 file creation, `allowBackup`; date-stamp NATIVE_PARITY. **Touches core:** yes
  (MAUI). **Decision:** resolve NAT-1 (escalation #2). **Verify:** native smoke.
- **B6-4 — RLS-3 system-scope seam (R40).** *Gated on escalation #1.* If approved: `EnterSystem()` seam;
  outbox/scheduler/sweeps/pre-auth enter it; interceptor asserts bypass only for entered-system/tagged.
  **Touches core:** yes. **Test first:** system-scope integration tests.

## B7 — Test-completeness (test-first specs from Phase 3, ~79) + the client chassis

- **B7-1 — Build the client component-test chassis (TOOL-2 / R70).** Add bUnit + `IJSRuntime` fake +
  `IThemePersistence`/`ICulturePersistence` doubles + JWT-claim builder + client clock; a Shared.Ui coverage
  floor. **Touches core:** no (test infra). *Prereq for B7-2.* **Verify:** a first `.razor` component test runs.
- **B7-2 — Client logic tests + fixes (UX-1..5, ADM-8/9, LB-UI-* / R71/R72/R73).** Reconcile state-machine
  matrix (TB-UI-5), impersonation guard, cross-user-poison guard, deep-link-preserving reload, switcher
  resync, bell mutations, billing i18n. **Touches core:** yes (Shared.Ui). **Verify:** `dotnet test` + E2E TB-UI-16.
- **B7-3 — Server test-completeness backfill.** `RefreshTokenService` + `TokenHasher` tests (TB-AUTH-1/2/8);
  remaining TB-TEN/TB-BILL/TB-ADM specs not covered by B1–B4. **Touches core:** no. **Verify:** coverage
  delta (Api branch coverage up from 63.5%).

## B8 — Docs, exemplar, and template-readiness

- **B8-1 — RLS slice recipe (TR-4 / R42-doc).** Add the `RlsDdl.StatementsFor` migration step to
  `WAYS_OF_WORKING.md` + a PR-template checkbox. **Verify:** review.
- **B8-2 — Fix the exemplar (TR-5 / R76).** `Notes` (and any slice) use `ErrorResponse`; ban `new { error`
  in `Features/**`. **Touches core:** yes (exemplar). **Verify:** `dotnet test --filter Features`.
- **B8-3 — Postman + config + doc-map sync (TR-1/2/3/6/9 / R74/R75).** Postman parity gate; doc-map + QA-
  count gate; config-catalog env-var coverage; DataProtection freeze (TR-8); fix the stale CLAUDE.md bullets;
  ADR-020 header. **Decision:** ADR-023 (escalation #3). **Verify:** `dotnet test --filter DocAndConfigSync`.
- **B8-4 — Rebrand + supply-chain docs (DEP-12/TR-7, TOOL-1/4 / R65/R66).** REBRANDING §5 additions; license
  gate covers all projects; single-maintainer-dep note; correct the ADR-004 "zero core edits" wording.
  **Verify:** review + license scan.

## B9 — Enforcement batch (last): promote every machine rule to a standing gate

- **B9-1** — Land every remaining `[machine]` rule from `FOUNDATION_RULES_v2.md` (R36–R76) as an
  `ArchitectureTests`/`DocAndConfigSyncTests`/`ci.yml` check not already added by B1–B8: R39, R49, R53, R55,
  R57, R59, R60, R63, R65, R68, R74, R75, R76. **Verify:** the full arch/doc-sync suite green; each new gate
  demonstrably fails on a seeded violation.
- **B9-2** — Update `CONTRIBUTING.md`'s Definition of Solid to reference `FOUNDATION_RULES_v2.md`; add the
  standing instruction to `CLAUDE.md`/`AGENTS.md` (read the rules before writing code; TDD; slice invariants).
  **Verify:** review.
- **B9-3** — Re-run the Phase-4 adversarial slice check (per the standing re-audit trigger) and confirm the
  corrected gates now catch every breach this audit found: policy-less migration (B1-1), route collision
  (B1-2), missing contributor (B1-3). **Verify:** the throwaway-slice attacks now fail CI.

---

## Traceability — every finding maps to a batch

| Finding(s) | Batch | Finding(s) | Batch |
|---|---|---|---|
| RLS-1 / ADV-P4-2 | B1-1 | LB-BILL-1/2/3/4, ADM-5 | B4 |
| ADV-P4-1, S0-G1 | B1-2 | DEP-2/3/4/6/7/8/9/10/11/12 | B5 |
| LB-TEN-1, S0-G2 | B1-3, B2-1 | NAT-1..11 | B6 |
| RLS-2 | B2-2 | RLS-3 | B6-4 (escalation) |
| RLS-4/5/7/8 | B2-3, B2-4 | TOOL-2, UX-1..5, ADM-8/9, LB-UI-* | B7 |
| ADM-2/8, LB-ADM-1 | B3-1 | TB-* (~79 specs) | B1–B4, B7-3 |
| ADM-3, LB-AUTH-1/2/3 | B3-2 | TR-1..10, TOOL-1/4, LB-ADM-2/3 | B8 |
| ADM-1/4, S0-G3 | B3-3, B3-4 | all `[machine]` rules | B9 |

**Definition of done for the whole plan:** every batch's tests green, the Phase-4 adversarial attacks now
fail CI (B9-3), `FOUNDATION_RULES_v2.md` becomes binding, and `CONTRIBUTING.md`'s Definition of Solid
references it. At that point the template is "solid by construction" across the post-2026-06-22 platform
epics — the inheritance-hardening pass (doc-only → inherited CI gates) is the separate follow-on.

*Report-only. Await approval before implementing B1.*
