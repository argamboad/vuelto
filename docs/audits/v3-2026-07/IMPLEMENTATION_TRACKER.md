# v3 Remediation — Task-Granular Implementation Tracker (all severities)

> Every finding from Phases 1–5, decomposed into discrete tasks. **Workflow: discuss each task → implement
> it (test-first) → verify → next.** Nothing is implemented before its discussion.
>
> **Conventions** (per repo memory): work on a branch off `develop` (never `main`); one coherent
> group ≈ one branch/PR unless we decide otherwise; commit per task; **do not `git push` until confirmed**;
> TDD — the failing test lands before the production code; update `QA_TEST_PLAN.md` / Postman / docs in the
> same task when touched. Re-verify each finding still reproduces on current `develop` before fixing.
>
> Status legend: ⬜ not started · 🔵 discussing · 🟡 in progress · ✅ done · ⏸️ deferred/decision-pending.

Ordering is dependency-first: the broken enforcement gates (Group A) come first because they gate trust in
everything else; then critical tenancy → auth → correctness → deploy/native → client → seams → test backfill
→ supply-chain/config → docs → enforcement close-out. We can reorder any of it during discussion.

## Group A — Keystone: fix the broken enforcement gates (test-first; each fails on current code first)
| # | Finding(s) | Sev | Task | Rule | Core? | Status |
|---|-----------|-----|------|------|-------|--------|
| T1 | RLS-1 / ADV-P4-2 | High | Fix the tautological RLS parity gate: split `RlsTestSetup` so the integration factory applies role+grants only; meta-assert `IntegrationTestFactory` never references `RlsDdl.StatementsFor` | R37 | no | ✅ |
| T2 | ADV-P4-1 / S0-G1 | High | Fix `RouteGroupPrefixes_AreUnique`: match `MapTenantFeatureGroup` (+`MapGroup`,`[Route]`) and scan Controllers/Features/Endpoints | R36 | no | ✅ |
| T3 | LB-TEN-1 | High | Add the tenant-axis dissolution/export canary (mirrors the user-keyed erasure canary) — fails today on 3 entities | R43 | no | ✅ |
| T4 | S0-G2 / RLS-6 | Low | Extend hatch bans (IgnoreQueryFilters/QueryAllTenants/RlsTags + `rls:cross-tenant` literal) to `Endpoints/` | R38 | no | ✅ |

## Group B — Critical tenancy: data-orphaning + set-based writes
| # | Finding(s) | Sev | Task | Rule | Core? | Status |
|---|-----------|-----|------|------|-------|--------|
| T5 | LB-TEN-1 | High | Wire ApiKey/UsageCounter/WebhookSubscription (+ WebhookDelivery) into dissolve + secret-free export → makes T3 green | R43 | yes | ✅ |
| T6 | RLS-2 | Med | `DissolveAsync`/erasure/sole-owner-leave execute inside `EnterTenant(target)` + caller scan | R44 | yes | ✅ |
| T7 | RLS-4 / RLS-8 | Med | Ban `QueryAllTenants`/`IgnoreQueryFilters` composed with ExecuteUpdate/Delete; fix the 4 live sites | R39 | yes | ✅ |
| T8 | RLS-5 / RLS-7 | Low | Interceptor: `TransactionFailed` cache invalidation + anchor tag detection to the leading comment block | R41 | yes | ✅ |

## Group C — Critical auth / admin
| # | Finding(s) | Sev | Task | Rule | Core? | Status |
|---|-----------|-----|------|------|-------|--------|
| T9 | ADM-2 / ADM-8 | Med | Reject `impersonated_by` principals at `RequireStaffAsync` + mutating account-pref endpoints | R45 | yes | ✅ |
| T10 | LB-ADM-1 | Certain | `AuditEvent.ImpersonatedBy` attribution column threaded through tenant-write audit records (+ migration) | R52 | yes | ✅ |
| T11 | ADM-3 | Med | Per-user, IP-independent MFA step-up brute-force cap | R47 | yes | ✅ |
| T12 | LB-AUTH-3 | Likely | Atomic single-use credential consume (magic-link/OTP/MFA challenge) — conditional ExecuteUpdate | R48 | yes | ✅ |
| T13 | LB-AUTH-2 | Likely | Atomic OTP lockout counter (server-side increment / RowVersion) | R49 | yes | ✅ |
| T14 | LB-AUTH-1 | Likely | Consume the MFA challenge before the stateful factor check (don't burn 2nd factor on a failed challenge) | R50 | yes | ✅ |
| T15 | ADM-1 | Med | `security.*` notifications bypass NotificationPreferences (force both channels) | R46 | yes | ✅ |
| T16 | ADM-4 | Med | Recovery codes: HMAC+server pepper (or ≥96-bit entropy) | R51 | yes | ✅ |
| T17 | ADM-5 | Med | Comp/revert 409 keys on subscription *liveness* (status), not id-presence | R59 | yes | ✅ |
| T18 | ADM-7 | Low | Admin MFA reset revokes the target's refresh tokens/sessions (or document as non-recovery) | R51-adj | yes | ✅ |
| T19 | ADM-6 / ADM-11 | Low/Info | Durable actor attribution on announce-all + tenant-less-user staff actions | R52 | yes | ✅ |
| T20 | ADM-10 / DEP-1 | Low | Document/config the `Proxy:Enabled` sole-ingress assumption; optional KnownNetworks/ForwardLimit | R63-adj | yes | ✅ |
| T21 | LB-ADM-2 | Likely | Announce with empty `user_ids: []` ⇒ notify none (not all) | R55 | yes | ✅ |
| T22 | LB-ADM-3 | latent | Deterministic membership lookup keyed on JWT `tenant_id` (no unfiltered FirstOrDefault) | R54 | yes | ✅ |

## Group D — Correctness: billing / jobs
| # | Finding(s) | Sev | Task | Rule | Core? | Status |
|---|-----------|-----|------|------|-------|--------|
| T23 | LB-BILL-1 | Likely | Webhook recency guard rejects only *strictly older* events (same-second distinct events both apply) | R56 | yes | ✅ |
| T24 | LB-BILL-2 | Likely | Outbox attempt/dead-letter bookkeeping advances on commit/flush faults (no infinite Pending loop) | R57 | yes | ✅ |
| T25 | LB-BILL-3 | Suspected | Quota insert-conflict recovery catches only `23505`; other DbUpdateException propagates | R58 | yes | ✅ |
| T26 | LB-BILL-4 | Suspected | Dunning fires only on transition *out of* a granting status (no cold-start dunning) | R59 | yes | ✅ |
| T27 | S0-G3 | Low | Empty-config posture gate for every `*Settings` (config-gated features closed by default) | R53 | no | ✅ |

## Group E — Deploy / CI
| # | Finding(s) | Sev | Task | Rule | Core? | Status |
|---|-----------|-----|------|------|-------|--------|
| T28 | DEP-2 / DEP-3 | Med/Low | Security headers (HSTS/nosniff/frame-ancestors) + SPA cache policy on the single-origin host | R62 | yes | ✅ |
| T29 | DEP-4 / DEP-11 | Med/Low | Single SDK pin source (`global.json`) + bump-together playbook + runtime image pin | R61 | no | ✅ |
| T30 | DEP-6/7/8/9 | Med/Low | CI hardening: fail-not-skip on missing smoke config, top-level `permissions:`, pin mailpit + checksum downloads | R63 | no | ✅ |
| T31 | DEP-10 | Low | Stripe `sk_live_`/`sk_test_` mode fail-closed startup guard | R64 | yes | ✅ |
| T32 | DEP-12 / TR-7 | Low | Rebrand touchpoints: render.yaml, CI package ids, Catalyst keychain, `ApplicationTitle` → REBRANDING §5 | R- doc | no | ✅ |

## Group F — Native
| # | Finding(s) | Sev | Task | Rule | Core? | Status |
|---|-----------|-----|------|------|-------|--------|
| T33 | NAT-3 | Med | Fail Release builds on localhost API base; scope cleartext config + env override to Debug | R67 | yes | ✅ |
| T34 | NAT-2 | Med | Pin MAUI workload set (`--version`) on all native legs; fix "CPM pins it" wording | R61 | no | ✅ |
| T35 | NAT-4 / NAT-5≡DEP-5 / NAT-6 | Low | native-paths regex adds `Directory.Build.props`; non-vacuous smoke assertion; fix `companyname` logcat grep | R60/R68 | no | ✅ |
| T36 | NAT-8/9/10/11 | Low | Loopback OAuth `state`; fix plist comment; 0600 file creation; `allowBackup=false`; date-stamp NATIVE_PARITY | R- | yes | ✅ |
| T37 | NAT-1 | Med | NATIVE-12: pushed + merged (decision: merge) | R69 | — | ✅ PR #172 |

## Group G — Client RCL + component-test chassis
| # | Finding(s) | Sev | Task | Rule | Core? | Status |
|---|-----------|-----|------|------|-------|--------|
| T38 | TOOL-2 | Med | Build the client component-test chassis (bUnit + IJSRuntime fake + persistence doubles + JWT builder + client clock) — **prereq for T39–T42** | R70 | no | ✅ |
| T39 | UX-1/UX-2 / LB-UI-1/2/3 | Med | Reconcile reload preserves `/join` + `/auth-callback` deep-links; non-looping on write-blocked storage | R72 | yes | ✅ |
| T40 | ADM-8/ADM-9 / LB-UI-4/5 | Med/Low | Reconcile no-ops while impersonating; device pref scoped to writing principal (no cross-user poison) | R71 | yes | ✅ |
| T41 | UX-3/UX-4 / LB-UI-6/7 | Low | Switcher surfaces PUT failure / pending marker; re-reads on reconcile (no stale value) | R72/R73 | yes | ✅ |
| T42 | UX-5/LB-UI-8, LB-UI-9/10 | Low | Billing plan/status localized; bell double-decrement fix; DELETE notifications non-default-all | R73/R55 | yes | ✅ |

## Group H — RLS-3 system-scope seam (decision-gated)
| # | Finding(s) | Sev | Task | Rule | Core? | Status |
|---|-----------|-----|------|------|-------|--------|
| T43 | RLS-3 | Med | `EnterSystem()`/`ISystemScope` seam — **decided 2026-07-27: DEFERRED** (concrete instances fixed individually; harness runs RLS-enforced; retrofit risk on sign-in paths inverts benefit — design + rationale in `PLATFORM_BACKLOG.md` §12) | R40 | yes | ✅ decided |

## Group I — Test-completeness backfill (the ~79 specs not covered by a fix above)
| # | Finding(s) | Sev | Task | Rule | Core? | Status |
|---|-----------|-----|------|------|-------|--------|
| T44 | TB-AUTH-1/2/8 | High | `RefreshTokenService` + `TokenHasher` test suites (zero coverage today) | R70/R7 | no | ✅ |
| T45 | remaining TB-* | Med | Backfill the remaining Phase-3 specs (tenancy negatives, billing boundaries, admin matrix) not landed with their fixes | R7 | no | ✅ |

## Group J — Supply-chain / coverage / config gates
| # | Finding(s) | Sev | Task | Rule | Core? | Status |
|---|-----------|-----|------|------|-------|--------|
| T46 | TOOL-1 | Med | License gate covers Web/Shared.Ui/Maui graphs, not just src/Api | R65 | no | ✅ |
| T47 | TR-9 | Low | Config-catalog gate covers `GetEnvironmentVariable` + `GetSection` reads | R75 | no | ✅ |
| T48 | S0-G4 | Low | R3 machine half: outbound-to-user-URL requests routed through `IOutboundUrlGuard` (arch scan) | R76 | no | ✅ |
| T49 | S0-G5 | Low | CI git-diff check: QA run-log block is append-only | R75 | no | ✅ |
| T50 | TOOL-4 | Low | Document `Otp.NET` single-maintainer risk + confirm it sits behind a swappable seam | R66 | no | ✅ |
| T51 | R80-nit | Low | Move CI pip/tool pins to a committed manifest | R63 | no | ✅ |

## Group K — Docs / exemplar / template-readiness
| # | Finding(s) | Sev | Task | Rule | Core? | Status |
|---|-----------|-----|------|------|-------|--------|
| T52 | TR-4 | Med | Document the `RlsDdl.StatementsFor` migration step in WAYS_OF_WORKING + PR template | R42-doc | no | ✅ |
| T53 | TR-5 | Low | Fix the Notes exemplar to use `ErrorResponse`; ban `new { error` in `Features/**` | R76 | yes | ✅ |
| T54 | TR-1/TR-2/TR-3 | Med/Low | CLAUDE.md doc map (NEW_APP_GUIDE/OVERVIEW/PRIMER); QA count 123→125; drop stale "framework deferred" bullet | R75 | no | ✅ |
| T55 | TR-6 | Med | Postman parity gate + add/annotate the 6 missing auth endpoints | R74 | no | ✅ |
| T56 | TR-8 | Low | Freeze the 5 DataProtection identity strings by test | R75 | no | ✅ |
| T57 | TR-10 | Low | Postman-governance ADR **authored as ADR-023** (filling the phantom number); ADR-020 header fixed + v3 addendum | R- | no | ✅ PR #190 |
| T58 | Phase-4 obs | Low | Correct ADR-004 "zero core edits" wording; add per-slice i18n key-namespacing guidance | R- | no | ✅ |
| T59 | B10-4 / TR-11 | Low/Info | Reconcile the never-created `ui.md` retrospective; reconcile E2E suite count (34 vs 32) | — | no | ✅ |

## Group L — Enforcement close-out (last)
| # | Finding(s) | Sev | Task | Rule | Core? | Status |
|---|-----------|-----|------|------|-------|--------|
| T60 | all `[machine]` | — | Promote every remaining machine rule (R36–R76) not already gated to a standing arch/CI check | all | no | ✅ |
| T61 | R7 mandate | — | Update CONTRIBUTING Definition of Solid → FOUNDATION_RULES_v2; standing instruction in CLAUDE.md/AGENTS.md | R7 | no | ✅ |
| T62 | re-audit | — | Phase-4 adversarial re-drill EXECUTED 2026-07-27: a throwaway `ITenantScoped` entity + policy-less migration + `MapTenantFeatureGroup("/api/notes")` collision → **all three corrected gates fired by name** (RLS parity: RLS not enabled/FORCEd/policy missing; route collision: /api/notes; contributor canary: AdversarialProbe) — the exact failures Phase 4 proved impossible pre-fix. Probe torn down; suite green | — | no | ✅ drill green |

---

**62 tasks across all severities.** 4 decision-gated (⏸️ T37, T43, T57, and the escalations inside T16/T20)
— **all four decided by 2026-07-27**: T37 merged (#172), T43 deferred-by-decision (`PLATFORM_BACKLOG.md`
§12), T57 authored ADR-023, and the T16/T20 escalations resolved inside their PRs.
Every Phase 1–5 finding is represented; test specs fold into their fix task (Group I holds only the specs
with no corresponding fix). We proceeded one task at a time: discuss, implement test-first, verify, mark
status, move on — and the tracker above records the outcome.
