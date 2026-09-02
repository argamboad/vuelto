# v3 Delta Audit — Phase 3: Logic Bugs & Test-Completeness

> **Status: COMPLETE.** Diagnose-only — report + test *specs* only; **no test or production code was written.**
> Reads Phase 1 `AUDIT_REPORT.md`, Phase 2 `AUDIT_RECONCILIATION.md`, and `FOUNDATION_RULES.md`
> (R1–R81) first; defers to all prior rules (adds, never overturns; disagreements → `RULE_CONFLICTS.md`).

## SUMMARY

- **Commit SHA (pinned):** `5fc1762dc5487de26af0e515c34c264efaaa11a7` (branch `audit/v3-2026-07-phase3` at
  `4207295` = `5fc1762` + audit docs, **code identical**). Matches Phases 1–2.
- **Part A — logic bugs: 16 new wrong-RESULT findings** (2 Certain · 9 Likely · 2 Suspected · 3 latent),
  plus 8 executable confirmations of already-filed Phase-1 findings (UX-1…5, ADM-9). The green 532-test
  suite sees none of them: they are **concurrency/ordering, boundary/fault-path, and completeness** bugs,
  and there is **no concurrency test, no fault-injection test, and no `.razor` component test anywhere in
  the repo.**
- **Part B — test-completeness: ~79 missing test specs** (written test-first), spanning Unit/component,
  Integration, E2E. The dominant gap: **the client RCL (`src/Shared.Ui`, ~1.2% covered — Phase 2 TOOL-2)
  has no component-test host at all**, so the entire THEME/PREFS/NOTIFY client cluster ships on E2E-only
  confidence; and `RefreshTokenService` + `TokenHasher` (the security-critical rotation + hashing
  primitives) have **zero** tests.
- **Harness readiness:** the **server-side** harness (FakeBillingProvider, FakeTimeProvider, Testcontainers
  Postgres, the RLS runtime-role harness, `ServiceHarness`, `TenantPermissionGate`) is strong and a new
  slice reuses it out of the box. The **client-side** harness is effectively absent (no bUnit, no
  `IJSRuntime` fake, no persistence doubles, no JWT-claim builder, no client clock), and there are **4
  specific server-side seams missing** (concurrency runner, DB-fault injector, injectable clock on the
  webhook helper + MFA challenge service, impersonation-token client helper).
- **Rules:** candidate TDD-invariant rules **R82–R99** added; none removed. Overlaps with R37/R45/R46/R47/
  R73 logged in `RULE_CONFLICTS.md` for Phase 5 to merge.
- **The single most urgent Part-A item is LB-TEN-1** (Certain, verified): tenant deletion silently orphans
  hashed API-key credentials and encrypted webhook secrets, and GDPR export silently omits three
  `ITenantScoped` tables — a live GDPR + credential-hygiene defect with a clean structural fix.

---

## Part A — logic bugs

Confidence: **Certain** (traced end-to-end / verified) · **Likely** · **Suspected** · **latent** (no wrong
result under a current invariant, but one refactor from breaking).

### Completeness (the headline)

**LB-TEN-1** · **Certain (verified)** · High-live · `TenantDissolutionService.cs:33-36` +
`TenantRepository.cs:110-134` (`WipeDataAsync`) + `TenantExportService.cs:48-49`
Only three `ITenantDataContributor`s exist — Audit→`AuditEvent`, Notes→`Note`, Billing→`Subscription` — and
`WipeDataAsync` deletes only `TenantInvitations`/`TenantMemberships`/`Tenants`. Of the 7 `ITenantScoped`
entities, **`ApiKey`, `UsageCounter`, and `WebhookSubscription` are wired into neither dissolution nor
export** (grep-confirmed: they appear in no contributor/export/wipe/dissolve code). None has an FK to
`Tenants` (ADR-003 plain-Guid tenancy), so there is **no cascade**.
- **Dissolve** (sole-owner leave / solo-owner erasure) → a dissolved tenant's **hashed API-key
  credentials**, its **encrypted webhook secret + URL + delivery logs**, and its **usage counters** survive
  as orphaned rows. Silent GDPR-erasure failure + lingering-credential security issue.
- **Export** → the "all your tenant data" bundle silently omits API-key metadata, usage counters, and
  webhook subscriptions — no section, no exclusion note.
- **Root cause:** an enforcement-gate asymmetry — the *user* axis has a machine canary
  (`EveryUserKeyedEntity_IsWiredIntoAccountErasure`, `ArchitectureTests.cs:74-103`); the *tenant* axis has
  none. Every downstream slice adding an `ITenantScoped` table inherits the same silent hole.
- **Fix:** wire each into a contributor (or an explicit teardown allowlist) **and** add the tenant-axis
  canary (R86) so omission fails CI. `WebhookDelivery` (TenantId, not `ITenantScoped`) is a fourth orphan.

### Concurrency / ordering (invisible to the sequential suite)

**LB-AUTH-3** · **Likely** · `PasswordlessService.cs:73-82` (magic link) + `:109-121` (OTP) — two concurrent
redemptions of one single-use credential (email-client prefetch, double-click) both pass the
`ConsumedAt == null` check before either writes → **two sessions (two refresh tokens) from one single-use
credential.** Fix: atomic `ExecuteUpdate(ConsumedAt=now) WHERE Id=id AND ConsumedAt IS NULL`, issue only
when affected==1.

**LB-AUTH-2** · **Likely** · `PasswordlessService.cs:104-133` (+ no RowVersion on `LoginToken`) — the OTP
brute-force counter is a non-atomic read-modify-write; concurrent wrong guesses each read the same
`failuresInWindow` and last-writer-wins the `AttemptCount++`, so **the IP-independent lockout cap (the
designated brute-force backstop) is exceeded by racing requests.** Fix: atomic server-side increment;
evaluate the cap against the persisted post-increment value.

**LB-AUTH-1** · **Likely** · `MfaLoginService.cs:45-48` (side effects `MfaService.cs:160-175`) — step-up
calls `mfa.VerifyAsync` (which burns the recovery code / advances `LastVerifiedTimeStep`) **before**
`challenges.Consume`; if Consume then fails, the second factor is irreversibly spent with no session issued
→ the user's recovery code is gone and login failed. Fix: consume the challenge before the stateful factor
check, or wrap both in one transaction.

### Billing / jobs boundary & fault paths

**LB-BILL-1** · **Likely** · `BillingWebhookHandler.cs:82` — the recency guard drops events with
`OccurredAt <= last`, but Stripe `Created` is **whole-second** granularity: two distinct events in the same
second (checkout `created`+`updated`, rapid plan change) → the genuinely-newer one is **dropped as "stale"**
and, if it's the flip to `active`, the tenant stays on Free despite payment. Exact redelivery is already
caught by the inbox (by EventId), so the guard should reject only *strictly* older (`<`). Recency tests
space events by minutes, so this second-boundary is unexercised.

**LB-BILL-2** · **Likely** · `OutboxProcessor.cs:69-100` — `HandleAsync` + `Status=Sent` are inside the
`try`, but `SaveChangesAsync`/`CommitAsync` are **outside** it. A commit-time failure (transient disconnect,
or a handler that stages a constraint-violating row) rolls back → `AttemptCount` never advances → the
message stays `Pending`, the **external side effect re-executes every pass, and it never dead-letters** (a
poison-at-commit message loops forever). Stronger than the at-least-once contract: attempt accounting makes
no progress. Fix: advance attempt/backoff/dead-letter bookkeeping on *any* completion failure.

**LB-BILL-3** · **Suspected** · `QuotaService.cs:71-75` — the first-consume `catch (DbUpdateException)`
assumes the only cause is a unique-key race and retries; a non-23505 failure (serialization/deadlock,
timeout, future check-constraint) is reinterpreted as a benign insert race, and the retried conditional
`ExecuteUpdate` can **spuriously deny a request that had headroom** or mask the real error. Fix: catch only
`SqlState == "23505"`; rethrow otherwise.

**LB-BILL-4** · **Suspected** · `BillingWebhookHandler.cs:85,117-135` — the **first-ever** webhook for a
tenant carrying `past_due`/`canceled` (failed first invoice, abandoned checkout later canceled) fires a
dunning/"canceled" notification to a tenant that **never had a live subscription** (`previousStatus` is null
so `Status != previousStatus`). Fix: only notify on a transition *out of* a granting status.

### Admin / audit integrity

**LB-ADM-1** · **Certain** · `AuditLog.cs:24-32` + `AuditEvent.cs:14-35` + every `audit.RecordAsync` on
tenant-write paths — writes performed **during** a legitimate impersonation session are audited as the
impersonated user with **no `impersonated_by` marker** (the `AuditEvent` entity has no such column; the
claim is read only client-side). Only the session *start* is recorded; every mutation inside the window is
indistinguishable from the user acting alone. This is the audit-integrity twin of Phase-1 ADM-2 (which
concerns the staff *gate*) — ADM-2's fix does not add per-write attribution. Fix: an `ImpersonatedBy`
attribution column threaded through the audit write.

**LB-ADM-2** · **Likely** · `AdminController.cs:236-238` — `POST …/announce` with `user_ids: []` (present
but empty) fails the `is { Count: > 0 }` test and falls through to the "all members" branch → an
explicitly-empty target set **broadcasts to the whole tenant** (max blast radius) with `targeted=false`.
Fix: empty list ⇒ zero recipients (or 400).

**LB-ADM-3** · **latent** · `PermissionService.cs:23-24` + `RequireTenantPermissionAttribute.cs:36-37` +
`TenantRepository.cs:36-37` — permission resolution is `FirstOrDefaultAsync(m => m.UserId == userId)` with
**no tenant filter and no ordering**, and the gate never compares the resolved membership's tenant to the
JWT `tenant_id`. Safe only under the single-tenant-per-user invariant; if a user ever holds two
memberships, authz resolves against an arbitrary membership (and the impersonation-token scope is minted the
same way). Fix: key resolution on the token's `tenant_id`; make membership lookups deterministic.

### Client (RCL) — new, plus executable confirmations of filed findings

**LB-UI-2** · **Likely (new)** · `AuthCallback.razor:17-37` + `MainLayout.razor:77-88,135-137` — on a web
sign-in with a locale mismatch and a pending `post_login_redirect` (invite acceptance), the SignedIn
reconcile's `forceLoad` to `/` (because `/auth-callback` is an anonymous path) **pre-empts the redirect
nav**, and the redirect key has already been consumed-and-removed → the invite target is **lost
permanently.** Compounds UX-1.

**LB-UI-5** · **Likely (new)** · `MainLayout.razor:103-111,125` — the **server-wins** legs of
`ReconcilePreferencesAsync` have **no `!Auth.IsImpersonating` guard** (only the device→server adopt legs
do). Latent today (BeginImpersonation doesn't re-fire `SignedIn`), but the guard asymmetry is one refactor
from writing the impersonated user's prefs into the admin's device store — a breach of the ADR-022 "never
rewrite the impersonated user's prefs" invariant.

**LB-UI-9** · **Likely (new)** · `NotificationBell.razor:155-168` — rapid double-click of one unread item
before the first `POST /read` returns passes the `ReadAt is null` guard twice → **`_count` double-decrements
for one item** (badge under-true until the 60 s poll). Fix: set `ReadAt` optimistically before awaiting.

**LB-UI-10** · **mechanism (robustness)** · `NotificationsController.cs:79-85` — `DELETE /api/notifications`
without `?read=` binds `read=false` → `DeleteAllAsync(onlyRead:false)` wipes **all** rows including unread.
The current client is correct, so not a live bug — but the safe-sounding "clear read" call nukes everything
if the param is ever dropped. Fix: make the destructive branch opt-in.

**LB-TEN-2** · **latent** · `WebhookService.cs:84-108` — `WebhookDelivery` carries `TenantId` but is neither
`ITenantScoped` (no RLS policy) nor EF-filtered; its isolation rests **entirely** on hand-written
`.Where(TenantId==)` filters. Correct today, but the one tenant-relevant table with **zero structural
backstop** — a dropped filter leaks/replays another tenant's webhook payloads with nothing to catch it.

**Executable confirmations of Phase-1 findings** (mechanisms traced, specs written — not re-filed as new):
LB-UI-1 (UX-1, `MainLayout.razor:125-138`), LB-UI-3 (UX-2 reload loop), LB-UI-4 (ADM-9 cross-user pref
poisoning, **Certain** — theme *and* locale), LB-UI-6 (UX-3 lost-pick revert), LB-UI-7 (UX-4 stale
switcher), LB-UI-8 (UX-5 raw billing tokens).

---

## Part B — test-completeness (~79 specs, written test-first; not implemented)

Grouped by level; **Critical** (security/tenancy/auth/money-correctness) before **High** (core logic).
Full arrange/act/assert intent for every spec is in the per-domain working notes; the Critical set is
itemized here, the High/Medium set is indexed by ID.

### Critical — tenancy & isolation negatives (the class the suite most lacks)

- **TB-TEN-1** (arch gate) `EveryTenantScopedEntity_IsWiredIntoTenantDissolution` — the tenant-axis mirror
  of the user-keyed canary; **fails today** on ApiKey/UsageCounter/WebhookSubscription (closes LB-TEN-1).
- **TB-TEN-2** (integration) `Dissolve_WipesEveryTenantScopedTable` — seed a row in *every* tenant table for
  two tenants; dissolve one; assert 0 rows for all of its tables, second tenant intact.
- **TB-TEN-3** (integration) `Export_IncludesEveryTenantScopedSection` — secret-free section per table; pin
  the exclusion list so a silent omission fails.
- **TB-TEN-4** (integration gate) `RlsMigrationGate_FailsForPolicylessTenantTable` — the corrected RLS-1
  gate: real-migrations-only DB (no `ProvisionAsync` back-fill), introduce a policy-less table, assert the
  gate reports it; + meta-assert `IntegrationTestFactory` never references `RlsDdl.StatementsFor`.
- **TB-TEN-5** (integration, RLS role) `Dissolve_UnderForeignEnteredTenant_DoesNotSilentlyOrphan` — proves
  RLS-2: under enforced RLS with ambient tenant ≠ target, today's dissolve deletes 0 RLS'd rows while core
  teardown succeeds (split-brain); post-fix all-or-nothing.
- **TB-TEN-6** (integration) `Erasure_OfTenantAOwner_LeavesTenantBUntouched` — cross-tenant erasure negative.
- **TB-TEN-7** (integration+RLS) `WebhookDelivery_CrossTenantRead_IsBlocked` — documents LB-TEN-2 (no RLS
  policy; forces a conscious add-policy-or-accept decision).
- **TB-TEN-8** (migration) `RlsTenancyBackstop_Down_DropsPoliciesAndDisablesRls` — the RLS migration has no
  rollback test.

### Critical — auth

- **TB-AUTH-1** (integration) `Refresh_RotatedTokenReplay_RevokesAllSessions` — the security-critical
  rotation/theft-revoke path has **zero** coverage.
- **TB-AUTH-2** (unit) `RefreshTokenService_Inspect_ClassifiesValidExpiredUnknownReuse`.
- **TB-AUTH-3** (integration) `MfaStepUp_NWrongCodesAcrossFreshChallenges_LocksUser` — the ADM-3 cap;
  fails today.
- **TB-AUTH-4** (integration) `MagicLink_ConcurrentRedemption_IssuesExactlyOneSession` (LB-AUTH-3).
- **TB-AUTH-5** (integration) `OtpVerify_ConcurrentWrongCodes_CountEveryAttempt` (LB-AUTH-2).

### Critical — admin / impersonation

- **TB-ADM-1** `Impersonation_TokenRejectedAtStaffGate` (ADM-2) · **TB-ADM-2** `ImpersonatedWrite_RecordsActingStaff`
  (LB-ADM-1, fails today) · **TB-ADM-3** `Impersonating_CannotExceedTargetRole` · **TB-ADM-4**
  `StaffMfaReset_RevokesTargetSessions` (ADM-7).

### Critical — billing / quota

- **TB-BILL-1** `Webhook_TwoDistinctEvents_SameSecond_AppliesTheLater` (LB-BILL-1) · **TB-BILL-2**
  `Webhook_FirstEventPastDue_NoPriorSubscription_DoesNotNotify` (LB-BILL-4) · **TB-BILL-3**
  `TryConsume_FirstConsume_NonUniqueDbError_Propagates` (LB-BILL-3) · **TB-BILL-4/5** quota boundary
  (`amount == limit`, `> limit`, `0`, negative) · **TB-BILL-17** `Webhook_UpsertThrows_RollsBackInboxClaim`
  · **TB-BILL-18** `Webhook_ConcurrentRedeliveryOfSameEvent_AppliesExactlyOnce` · **TB-BILL-19**
  `TryConsume_TwoConcurrentFirstConsumes`.

### Critical — client (RCL, component-level — currently impossible to write, see harness)

- **TB-UI-1** `Reconcile_ServerNullAndDevicePrefFromPriorUser_DoesNotPoisonAccount` (ADM-9/LB-UI-4).
- **TB-UI-2** `Reconcile_WhileImpersonating_NeverWritesDeviceOrServer` (ADM-8/LB-UI-5).

### High — indexed by ID (full specs in working notes)

- **Auth:** TB-AUTH-6..12 (recovery-code-not-burned LB-AUTH-1, recovery-code step-up path, `TokenHasher`
  constant-time, OTP short-circuit ordering, magic-link wrong-token + case-insensitive email, MFA-reset
  session-survival characterization, OTP-lockout E2E journey).
- **Tenancy:** TB-TEN-9..11 (all-RLS-table dissolve deletes, admin-comp tenant-scoping negative, DI-registered
  contributor coverage gate).
- **Billing/jobs:** TB-BILL-6..16, 20..27 (outbox commit-fault dead-letters LB-BILL-2, exponential backoff,
  unknown-handler dead-letter, scheduler-rerun needs FakeTimeProvider, lapse re-lapse/past-due/ownerless,
  broadcast fan-out idempotent, status-map exhaustive, seat boundary, concurrent last-seat accept
  BILLING-9, month/year-rollover UTC, concurrent pollers no-double-claim, migration up/down, RLS parity —
  **TB-BILL-27 blocked by RLS-1**).
- **Admin:** TB-ADM-5..13 (security-notification-bypasses-prefs ADM-1, comp→webhook→revert interleaving,
  cross-tenant announce id, empty-user_ids LB-ADM-2, announce-all attribution ADM-6, per-role×action 403
  matrix, ManageApiKeys/Webhooks matrix rows, transfer-ownership concurrency 409).
- **Client:** TB-UI-3..16 (locale-mismatch reloads-into-join LB-UI-1, persist-fails no-loop LB-UI-3, the
  **reconcile state-machine `[Theory]` matrix TB-UI-5 — the single highest-value spec**, auth-callback +
  pending-redirect LB-UI-2, switcher change-then-reconcile UX-4, switcher-PUT-fails UX-3, NotificationBell
  mutation set incl. double-decrement LB-UI-9 and ClearRead/ClearAll request-shape LB-UI-10, `Ago`
  boundaries, AuthService SignedIn-fires-once / claim-parse / impersonation transitions / staff-cache /
  refresh-coalesce, and **E2E TB-UI-16 `Reconcile_DoesNotBreakInviteAcceptance_UnderLocaleMismatch`** — the
  one place UX-1/LB-UI-2 is caught end-to-end).

### E2E — cross-referenced against existing debt (not double-counted)

Existing journeys already cover: theme happy-path (`ThemeJourneyTests`), locale-follows-user
(`I18nTests`), notifications (`NotificationJourneyTests`), membership lifecycle, magic-link single-use, MFA
enroll+step-up, billing upgrade-loop (`BillingJourneyTests`), seat-quota create-side 402
(`SeatQuotaJourneyTests`). **Net-new E2E debt this phase adds:** OTP-lockout journey (TB-AUTH-12),
accept-side 402 `/join` full-state (TB-BILL-24), dunning owner-notification end-to-end (TB-BILL-25), and the
locale-mismatch × invite-acceptance leg (TB-UI-16). These align with the roadmap's known E2E debt for the
platform features built unit/integration-first.

---

## Harness readiness

**Server-side: strong and reusable by a new slice out of the box.** `FakeBillingProvider` (offline
webhook/checkout/portal/cancel with a `ValidSignature` constant + recording queues), `FakeTimeProvider`
(every server service takes an injected `TimeProvider`), Testcontainers Postgres
(`PostgresFixture`/`Base`/`Collection` — real `FOR UPDATE SKIP LOCKED`/`ON CONFLICT`/conditional
`ExecuteUpdate`), the **RLS runtime-role harness** (`RlsTestSetup` provisions a non-superuser role;
`RlsBackstopTests` drives raw-SQL + EF as that role — a slice copies this to prove its own isolation),
`ServiceHarness`, and `TenantPermissionGate.RunAsync` (drives `[RequireTenantPermission]` exactly as the
pipeline). A new server slice inherits all of this.

**Four server-side seams are missing** and block whole columns of the specs above:
1. **No concurrency-test helper** (`RunConcurrentlyAsync(n, factory)`) — every fixture is sequential, so the
   atomicity specs (LB-AUTH-2/3, LB-BILL-18/19, outbox double-claim) cannot be written.
2. **No DB-fault-injection seam** — nothing can make `SaveChanges`/`Commit` fail on demand, so the
   error-path specs (LB-BILL-2 commit-fault, TB-BILL-17 rollback-claim, LB-BILL-3) can't be written.
3. **The webhook test helper pins `TimeProvider.System`** (and `MfaChallengeService` expiry rides the real
   clock via `TimeLimitedDataProtector` + `IMemoryCache`) — so the recency second-boundary (LB-BILL-1),
   dunning/lapse date math, and MFA-challenge expiry are untestable with the Test Clock.
4. **No impersonation-token client helper** — `IntegrationTestFactory.CreateClientFor` mints only normal
   tokens; without a `CreateImpersonatingClientFor(staff, target)` the ADM-2 / LB-ADM-1 gate tests can't be
   written against the real pipeline (`JwtTokenService.IssueImpersonationToken` is public, so the helper is
   a small addition).

**Client-side (RCL): effectively absent — the biggest chassis gap for downstream apps.** The pure
`AuthService` logic seam *is* testable today (`Core.Tests` already references `Shared.Ui` and stubs
`HttpMessageHandler` + `ISessionStore` in `NativeOtpLockoutTests`), so TB-UI-11..15 can be written now with
no new packages. But **everything that lives in `.razor @code`** — the `MainLayout` reconcile state machine,
`ThemeSwitcher`/`LanguageSwitcher`, `NotificationBell` — is untestable because there is **no bUnit (or any
component-test host), no `IJSRuntime` fake for the app's `appTheme`/`localStorage` contract, no
`IThemePersistence`/`ICulturePersistence` doubles, no JWT-claim builder to arrange
theme/locale/impersonation/expiry states, and no injected client clock.** This is precisely why the UX-1…5 /
ADM-8 / ADM-9 cluster ships on E2E-only confidence, and **a new slice adding any client screen must build
the entire component-test chassis from scratch** — the Notes exemplar (a server slice) offers no pattern to
copy.

---

## Candidate rules (TDD invariants) — R82–R99

Machine unless marked review. Overlaps with earlier rules are noted and logged in `RULE_CONFLICTS.md`.

**Completeness / tenancy**
- **R82 [machine]** — Every `ITenantScoped` entity is wired into tenant dissolution **and** export
  (a registered `ITenantDataContributor` or an explicit teardown/export allowlist entry); a model-scan arch
  test mirrors the user-keyed erasure canary and covers `TenantId`-carrying non-`ITenantScoped` entities
  too. *(LB-TEN-1 keystone; extends R12/R13 to the tenant axis.)*
- **R83 [review] — machine half of R37** — every `DissolveAsync`/erasure/sole-owner-leave executes inside
  `EnterTenant(target)`; scan that each `DissolveAsync(` caller is lexically within an `EnterTenant(` scope.
  *(LB-TEN-1 partial, RLS-2.)*
- **R84 [review]** — an entity carrying `TenantId` but not `ITenantScoped` must ship a dedicated
  cross-tenant isolation test (it has neither the query filter nor the RLS wall). *(LB-TEN-2.)*

**Concurrency / atomicity**
- **R85 [machine]** — single-use credential consumption (magic-link, OTP, MFA challenge) is atomic
  (conditional `ExecuteUpdate … WHERE ConsumedAt IS NULL`, affected==1; session only on the winning update),
  proven by a concurrent-redemption test. *(LB-AUTH-3.)*
- **R86 [machine]** — brute-force/attempt counters increment atomically (server-side increment or
  RowVersion+retry; cap evaluated against the persisted post-increment value). *(LB-AUTH-2; pairs with the
  atomic-quota rule.)*
- **R87 [review→machine]** — second-factor state is not mutated before the login challenge is confirmed
  single-use. *(LB-AUTH-1.)*

**Billing / jobs**
- **R88 [machine]** — outbox attempt/dead-letter bookkeeping advances on **any** completion failure,
  including post-handler commit/flush faults; a poison-at-commit message dead-letters, never loops.
  *(LB-BILL-2.)*
- **R89 [machine]** — the webhook recency guard rejects only *strictly older* events; two distinct events
  sharing a timestamp both take effect in arrival order. *(LB-BILL-1.)*
- **R90 [machine]** — quota insert-conflict recovery catches only the unique-violation (`23505`); any other
  `DbUpdateException` propagates. *(LB-BILL-3.)*
- **R91 [review]** — dunning/lapse notifications fire only on a transition *out of* a granting status,
  never on a cold-start into a bad state. *(LB-BILL-4.)*

**Admin / audit**
- **R92 [machine]** — no audit write on a tenant-scoped mutation attributes an action to a principal without
  also recording `impersonated_by` when the principal carries it (requires an `AuditEvent` attribution
  column). *(LB-ADM-1.)*
- **R93 [review]** — tenant-permission resolution keys on the request's `tenant_id`; membership lookups for
  authz are deterministic (tenant-scoped or ordered), never an unfiltered `FirstOrDefault`. *(LB-ADM-3.)*
- **R94 [review]** — bulk/destructive endpoints define empty-selector semantics explicitly (empty ⇒ none)
  and never default to the widest blast radius. *(LB-ADM-2, LB-UI-10.)*

**Client (RCL)**
- **R95 [machine]** — a test project exercises `src/Shared.Ui` `.razor` components with a component-test
  host (bUnit or equivalent); enforced by a Shared.Ui coverage floor or the existence of a razor-rendering
  test assembly. *(TOOL-2 keystone; the fast-test seam R73's E2E rule depends on.)*
- **R96 [machine]** — `ReconcilePreferencesAsync` no-ops entirely while `IsImpersonating` (both server-wins
  and adopt branches gated). *(LB-UI-5, ADM-8/9.)*
- **R97 [machine]** — device preference stores are scoped to the writing principal (or cleared on sign-out)
  before a null-server value may be adopted into an account. *(LB-UI-4/ADM-9.)*
- **R98 [review]** — reconcile/state reloads preserve the current deep link for non-terminal anonymous paths
  (`/join`, `/auth-callback`); only `/login`/`/auth-error` redirect to `/`. *(LB-UI-1/2; overlaps R73.)*

**Harness / process**
- **R99 [review]** — the shared harness must provide, and a new slice must reuse (never rebuild): a
  concurrency runner, a DB-fault-injection seam, an injectable clock on every time-sensitive test helper
  (webhook handler, MFA challenge), an impersonation-token client helper, and a client component-test
  chassis (bUnit + `IJSRuntime`/persistence doubles + JWT-claim builder). TDD invariants restated: no
  production code without a failing test first, at the right level; every slice ships happy-path +
  permission-denied + cross-tenant-isolation tests before "done"; every new public method tested per branch
  and error path; `QA_TEST_PLAN.md` updated in the same PR; the run log stays append-only (R81).

---

## Notes / open questions

- **No overturns.** Every finding adds to R1–R81; disagreements (R83↔R37, R85–R87↔R46/R47, R92↔R45,
  R95/R98↔R73) are logged in `RULE_CONFLICTS.md` for Phase 5 to merge, not resolved here.
- **Not re-filed** (adjudicated Phase 1 / v2): ADM-1..11, RLS-1..8, UX-1..5, and the v2 CONF-5/6 OTP-window
  semantics. Where a Phase-1 finding needed an executable proof, it is given as a *spec* (e.g. TB-TEN-4/5 for
  RLS-1/2; TB-AUTH-3 for ADM-3; TB-UI-1/2 for ADM-8/9), not a new finding.
- **Open questions for Phase 5** (undeterminable intent, not guessed bugs): (1) should scheduled-job `Name`
  carry a uniqueness gate (`_lastRun` keys on it)? (2) is `EntitlementService`'s exact-`CurrentPeriodEnd`
  boundary (fails closed at the instant) the intended semantics? (3) `TB-BILL-27` (billing RLS parity) is
  **blocked by RLS-1** and must be re-validated only after R36's fix lands. (4) the
  `HouseholdController.Rename`/`RemoveMember` forbidden-message text says "owner" while the matrix grants
  admin — result correct, message stale (low).
- **No money/rounding bugs exist** — all currency/proration is delegated to Stripe (checkout uses a
  configured price with `Quantity=1`; no in-code money arithmetic). That part of the brief is legitimately
  empty.

---
*Phase 3 complete. Phase 4 (adversarial build-a-slice) reads this next and must re-test the RLS new-slice
contract and the tenant-axis dissolution/export canary; Phase 5 is the only reconciler and the only gate to
implementation.*
