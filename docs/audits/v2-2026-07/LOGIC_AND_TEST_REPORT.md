# LOGIC_AND_TEST_REPORT.md — v2 Phase 3 (Logic Bugs & Test-Completeness)

## SUMMARY

- **Commit SHA:** `84c7ad838c8e7cdc8c9bfb0c4cb939646025040e` — matches Phases 1–2 (branch `audit/v2-phase3-logic-tests`; only `docs/audits/v2-2026-07/` accumulates, `src/` tree unchanged).
- **Part A — logic bugs:** 2 High, 2 Medium, 4 Low + 1 verified-consistent (not a bug) + 3 open questions. Headliners: **LOGIC-S1 (High) MFA step-up is replayable** (challenge + TOTP both un-consumed) and **LOGIC-B1 (High) billing webhook blind-overwrites stale/out-of-order events** (its own docstring claims otherwise).
- **Scope verdict:** an adversarial read of RBAC resolution, MFA "every path," ADMIN impersonation/`EnterTenant`, GDPR export/erasure, and the by-convention tenant filters found **no new wrong-*tenant*/wrong-*scope* bug** beyond the known GAP-1/GAP-2. The scoping machinery is clean and fail-closed.
- **Part B — test-completeness:** unit/integration coverage is strong (Core 42/42, Api 335/335, ~88%); the gaps are **write-side tenancy negatives per epic**, the four **MFA step-up integration paths** (only the service seam is proven), **migration rollback (`Down`)**, the **fail-open/SSRF/stale-webhook/clock boundary** specs, and the **entire E2E journey layer** (whole suite = 4 OTP tests, not in CI). Harness is load-bearing on the DELETE-ME Notes entity (TR-1).
- **Rules added:** R28–R31 (MFA anti-replay, event-recency, atomic quota, TDD invariants). **Conflicts logged:** 0.

> Diagnose-only. No tests or production code written — Part B emits **specs**, not code. Confidence tags: Certain / Likely / Suspected. Undeterminable intent → open question, not a guessed bug.

---

## Part A — Logic-bug hunt

Two independent hunters ran: one on billing/outbox/time arithmetic + idempotency, one on auth/RBAC/GDPR/admin scope. IDs `LOGIC-B*` (billing/outbox/time), `LOGIC-S*` (scope/security-logic).

### LOGIC-S1 · High · Certain — MFA step-up is replayable; neither the challenge nor the TOTP code is consumed on use
**Files:** `MfaService.cs:144-151` (`TryVerifyTotp`), `MfaChallengeService.cs:28-49` (`TryRead`), `UserMfa.cs` (no last-used-step field), `AuthController.cs:511-522` (`mfa/verify`). **Verified by parent.**
`TryVerifyTotp` calls `new Totp(...).VerifyTotp(code, out _, Window)` and **discards the matched timestep** (`out _`), storing nothing on `UserMfa`; the challenge is a stateless DataProtection blob that `TryRead` only unprotects, never marks spent. Contrast the recovery-code path two lines up (`MfaService.cs:138-140`), which *does* consume via `UsedAt`.
- **Trigger:** an MFA-enabled user (or anyone who captures the pair) re-posts the same `{challenge, code}` to `POST /api/auth/mfa/verify` within the TOTP validity window (~90s at ±1 step).
- **Wrong result:** each POST mints a fresh, independent full session (access + rotating refresh). One intercepted challenge+code → N sessions. The `PasswordlessPolicy` rate limit (5/min/IP) bounds but does not close it.
- **Fix:** consume the challenge on first success (single-use, mirroring `SingleUseCacheToken<T>`) **and** persist the last-accepted TOTP timestep on `UserMfa`, rejecting steps ≤ the last accepted (RFC-6238 anti-replay). Either closes the multi-session replay; both are warranted.
- **Why tests miss it:** `MfaChallengeServiceTests` asserts round-trip/tamper only; MFA service/login tests assert valid-succeeds / invalid-fails, never that a **second identical** verify is rejected.

### LOGIC-B1 · High · Certain (behavior); Likely (unintended) — Billing webhook blindly overwrites with stale/out-of-order events; no recency guard
**Files:** `BillingWebhookHandler.cs:52-98` (`UpsertSubscriptionAsync`), `Subscription.cs` (no provider-sequence/updated field). **Mechanism cross-confirmed by the Part-B agent.**
The class docstring (`:12-15`) and inline comment (`:43`) claim "out-of-order delivery" is handled, but the only ordering defense is inbox dedup on the **unique** `evt.EventId` (`:44`) — which suppresses redelivery of the *same* event, nothing for two *different* events arriving out of order. `UpsertSubscriptionAsync` then unconditionally overwrites `Status`/`PlanKey`/`CurrentPeriodEnd`/… with the current event.
- **Trigger:** Stripe emits `updated→active (periodEnd T2)` then a redelivered/older `canceled` (distinct event id) that arrives *after*. Both pass the inbox.
- **Wrong result:** last-*delivered* wins regardless of true provider recency — a stale `canceled` clobbers a live `active` (tenant loses paid entitlements) or a stale `active` re-grants a canceled plan (unpaid access).
- **Fix:** persist a provider `updated`/sequence timestamp on the projection and apply only strictly-newer events; skip otherwise. Fixing this also protects LOGIC-B2 and LOGIC-B6.

### LOGIC-B7 · Medium-High · Likely — `QuotaService.TryConsumeAsync` violates its "Atomically consumes" contract under concurrency
**Files:** `QuotaService.cs:38-64`, contract `IQuotaService.cs:18-19`, unique index confirmed at `AppDbContext.cs:234` (`(TenantId, Key, Period)`). **Index existence verified by parent.**
Read (`FirstOrDefaultAsync`) → check (`current + amount > limit`) → increment → `SaveChanges` is not atomic; `UsageCounter` has no concurrency token.
- **Wrong result (update path):** two concurrent consumers both read `current`, both pass the check, both write — a lost update that **exceeds the cap**. **(create path):** two first-of-period racers both `AddAsync`; the unique index makes the loser throw `DbUpdateException` → the request 500s instead of consuming.
- **Fix:** conditional `ExecuteUpdateAsync` guarded by `Count + amount <= limit` (check rows-affected) or `SELECT … FOR UPDATE`; upsert-on-conflict for the create path. The word "Atomically" makes this a spec violation, not just a theoretical race. *(Seat path `CanAddSeatsAsync` shares the check-then-act shape but is lower-severity; the seat boundary math itself is correct.)*

### LOGIC-B3 · Medium · Certain — Refresh-token cookie `Expires` uses ambient `DateTimeOffset.UtcNow`, diverging from the injected-clock token expiry
**File:** `CookieService.cs:36`. A live sibling of GAP-4, and worse: the cookie lifetime and the server-side DB token lifetime are *meant to coincide* (`RefreshTokenService.cs:69,77` uses the injected `TimeProvider`) but are now driven by two clocks.
- **Wrong result:** under a shifted/virtual clock the cookie can drop before the token expires (wedged sign-in) or persist after (token the server treats as expired); expiry-boundary tests that freeze the clock silently get a real-wall-clock cookie.
- **Fix:** inject `TimeProvider` into `CookieService`, compute `Expires` from `clock.GetUtcNow()`. *(Feeds the R15 arch test — other ambient-clock sites: `S3FileStorage.cs:81` (GAP-4), plus lower-impact WASM/entity-default sites `NotificationBell.razor`, `AuthService.cs:386`, entity `CreatedAt` defaults.)*

### LOGIC-B2 · Medium · Likely — Dunning re-notification is driven by delivery order, not transition truth (amplified by B1)
**File:** `BillingWebhookHandler.cs:100-120` (`MaybeNotifyDunningAsync`). "Previous status" is whatever the projection holds, which per B1 is the last-*delivered* event. Interleavings like `active→past_due→(stale)active→past_due` re-fire "payment failed" for the same episode (the `:55-57` "no spam" promise only holds for identical consecutive statuses); a stale event between two real ones can also mask a genuine transition. **Fix:** gate on a recency-ordered state machine (fixing B1 largely fixes this) or dedup dunning per (tenant, episode).

### LOGIC-B6 · Low · Likely — Lapse-sweep re-nudge guard breaks only if a webhook pushes `CurrentPeriodEnd` backward (downstream of B1)
**File:** `SubscriptionLapseSweepJob.cs:34-48`. The `LapseNotifiedAt < CurrentPeriodEnd` guard is correct **as long as each renewal strictly advances `CurrentPeriodEnd`**; if LOGIC-B1's blind overwrite ever moves `CurrentPeriodEnd` earlier, the guard stays false forever and the tenant is **never** re-nudged on the next genuine lapse. Amplification of B1, not independent — recorded so the B1 fix is understood to protect it.

### LOGIC-B5 · Low · Suspected — MFA challenge (DataProtection `ITimeLimitedDataProtector`) expiry bypasses the injected `TimeProvider`
**File:** `MfaChallengeService.cs:22-26`. The 5-min challenge lifetime is evaluated against the framework's ambient clock; the service takes no `TimeProvider`. Not exploitable (real-time window still enforced) but untestable against a virtual clock and inconsistent with the auth timing model. No TimeProvider-aware overload exists today → accept+document or hand-roll a signed expiry. Open question.

### LOGIC-B8 · Low · Suspected (by-design) — `UsageCounter` period key is UTC-monthly with no tenant timezone
**Files:** `QuotaService.cs:45`, `UsageCounter.cs:17`. `clock.GetUtcNow().ToString("yyyy-MM")` resets at UTC month boundary, so a UTC-8 tenant's "monthly" quota rolls over at 4pm local on the last day — usage near the boundary counts to the wrong month. Deliberate simplification; flagged as a product-behavior open question, not a coding error.

### LOGIC-S2 · Low · Likely — TOTP verification window ±1 step (~90s) is applied on enrollment-confirm too, amplifying S1
**File:** `MfaService.cs:50,87,144-151`. `ConfirmEnrollmentAsync` and `VerifyAsync` share `Window = (1,1)`, so any accepted code is valid for up to 3 timesteps. Compounds S1's replay window. **Fix:** tighten confirm to window 0, or keep ±1 only once S1's timestep-consumption lands.

### LOGIC-S3 · Open question — Invitation acceptance is a pure bearer-token capability; the accepting user's email is never checked against `InvitedEmail`
**File:** `TenantInvitationService.cs:161-224` (`AcceptAsync`). Looks the invite up by token hash only and moves whoever presents it into the tenant; never compares caller email to `invitation.InvitedEmail`. This is a common, legitimate design (invite link = bearer capability; single-use, hashed, time-limited, atomically consumed). Whether email-binding is intended is undeterminable from code/docs. If intended → add `user.Email == invitation.InvitedEmail` (wrong-decision gap); if bearer is intended → correct as-is. Needs a human/ADR call.

### Verified correct (recorded so later phases don't re-litigate)
Outbox claim/commit atomicity (`FOR UPDATE SKIP LOCKED` + one transaction; crash-before-commit re-eligible; backoff `Base*2^(n-1)`; `>= MaxAttempts` dead-letter exactly right); inbox `INSERT … ON CONFLICT DO NOTHING` race-free; scheduler `_lastRun` advanced in `finally` (no hot-loop, no overlap, backward-skew delays not double-runs); seat boundary `Used + count <= limit`; OTP cumulative lockout allows exactly `OtpMaxAttempts`; entitlement fail-closed `CurrentPeriodEnd > now`; notification + delivery-log keyset paging ordered; **expiry-tick consistency** — every token lifetime resolves "exactly-at-expiry ⇒ invalid" uniformly (LOGIC-B4, a positive note, not a bug). RBAC matrix + owner-only permissions + single-owner/self-escalation invariants; MFA "every path" claim holds (`/refresh` correctly excluded); ADMIN `EnterTenant` scoped-and-restored, impersonation token carries target identity, 15-min, `ClockSkew=Zero`, non-refreshable, staff gate exact `OrdinalIgnoreCase` fail-closed; GDPR export/erasure drive off explicit ids, contributors scope by `QueryAllTenants().Where(TenantId==arg)`, ExportKeys distinct; `UsageCounter` is `ITenantScoped` (no cross-tenant counter sharing).

### Part-A open questions (for Phase 5 / human)
1. **B1/B2:** is the billing projection meant to apply only strictly-newer events (docstring read), or is last-writer-wins accepted? If the latter, the "out-of-order"/"no spam" comments are false and must be corrected regardless.
2. **B7:** is the quota guarantee strict (must never exceed under concurrency)? The "Atomically" contract says yes.
3. **B5:** accept the DataProtection challenge clock limitation, or re-implement with the injected clock?
4. **B8:** is UTC-monthly reset intended for non-UTC tenants?
5. **S3:** email-bound vs bearer-capability invitation acceptance?
6. **S2:** intended TOTP skew for login vs enrollment-confirm?

---

## Part B — Test-completeness (specs, test-first)

Existing coverage is genuinely strong across every epic (see the survey in the reconciliation's coverage note). Specs below are **gaps only**, grouped Critical (security/tenancy/auth) → High (core business logic) → E2E. Each: name · level · module · arrange/act/assert intent. Specs marked **[catches Part-A]** would turn red on a real logic bug above.

### Critical — Unit: fail-open normalization (SOLID-3 / R17)
- **`ApiKeyScopes_AllInvalidInput_IsRejected_NotGrantedAll`** · unit · PUBAPI/`ApiKeyService` — request `scopes:["raed","wrte"]` (all typos) ⇒ **reject (400/null)**, not a full-access key. `ApiKeyService.cs:102` returns `ApiScopes.All` when the filtered list is empty; only *partial*-invalid is tested. **[catches Part-A]**
- **`WebhookEventTypes_AllInvalidInput_IsRejected_NotGrantedAll`** · unit · HOOKS/`WebhookSubscriptionService` — `eventTypes:["bad"]` ⇒ reject, not subscribe-to-all. `WebhookService.cs:118` identical fail-open; distinguish `null` (default-all) from provided-but-all-invalid. **[catches Part-A]**

### Critical — Billing webhook authenticity & ordering
- **`BillingWebhook_StaleRedelivery_DoesNotClobberNewerStatus`** · integration · BILLING — apply `active(e1)`, `canceled(e2, later period)`, then a stale `active(e3, older period)` ⇒ status stays `canceled`. **[catches LOGIC-B1]**
- **`BillingWebhook_FakeProvider_RegisteredOnlyInDevelopment`** · integration · DI (R1, GAP-1) — build the container with no Stripe key under non-Development ⇒ startup **throws** (or the controller isn't mapped). No test exists; this is the must-fix Critical.
- **`BillingWebhook_ForgedSignature_LogsWarning_WithSourceContext`** · integration · BILLING (R19, GAP-5) — invalid signature ⇒ `InvalidSignature` **and** a warning logged with source IP.

### Critical — Webhook SSRF & scheme (GAP-2, CON-1)
- **`WebhookUrl_SsrfTargets_AreRejected`** · unit · HOOKS — `169.254.169.254`, `localhost`, `127.0.0.1`, `10/8`, `192.168/16`, `[::1]` all rejected (register + send-test + async sender); include a DNS-rebinding variant (resolves to private ⇒ reject after resolution). `IsValidUrl` (`WebhookService.cs:107`) checks only scheme+absolute.
- **`WebhookUrl_Http_IsRejectedOutsideDevelopment`** · unit · HOOKS — `http://…` rejected outside Development, matching the "https required" message.

### Critical — Tenancy-isolation NEGATIVES (write side) per epic
Read-isolation negatives exist; the **write/mutate** negatives are the hole. Each: tenant B owns a row; tenant A attempts to mutate B's row by id ⇒ not-found/forbidden, B's row unchanged.
- **`ApiKey_TenantA_CannotRevokeTenantBKey`** · integration · PUBAPI
- **`Webhook_TenantA_CannotDeleteOrReplayTenantBSubscription`** · integration · HOOKS (replay partly covered; delete not)
- **`Subscription_TenantA_CannotReadOrWriteTenantBSubscription`** · integration · BILLING (direct-service, beyond the webhook-apply test)
- **`Notification_TenantAUser_CannotListTenantBNotifications`** · integration · NOTIFY (rides on hand-filtering — the ARCH-2 risk that needs an explicit guard)
- **`Interceptor_ForeignTenant_Update_And_Delete_AreScoped`** · integration · tenancy core (the stamping test covers INSERT only; UPDATE/DELETE under a foreign current-tenant untested)

### Critical — Arch tests enforcing the flagged invariants
- **`Arch_EntityWithTenantId_IsScopedOrAllowlisted`** (R2/ARCH-2) · **`Arch_NoAmbientUtcNow_InSrc`** (R15/GAP-4 — fails today on `S3FileStorage.cs:81`, `CookieService.cs:36`) · **`Arch_EveryController_DerivesFromTenantOrAdminBase_OrAllowlisted`** (R4) · **`Arch_NoRawMapGroupRequireAuthorization_InFeatures`** (R6).

### High — MFA step-up on EVERY sign-in path (controller integration)
Only the `MfaLoginService` seam is proven; the four call sites are not exercised end-to-end. **[catches a dropped step-up call site]**
- **`MfaStepUp_OtpSignIn_WithMfaEnabled_ReturnsChallengeNotSession`** (`AuthController.cs:496`)
- **`MfaStepUp_MagicLink_WithMfaEnabled_ReturnsChallengeNotSession`** (`:449`)
- **`MfaStepUp_OAuthCallback_WithMfaEnabled_ReturnsChallengeNotSession`** (`:121`)
- **`MfaStepUp_NativeExchange_WithMfaEnabled_ReturnsChallenge_PreservesNativeFlag`** (`:620`)
Each: MFA-enabled user reaches the path ⇒ a challenge (no session/cookie/body token) is returned; the verify then issues the session.
- **`MfaVerify_ReplayedChallengeAndCode_IsRejectedOnSecondUse`** · integration · AUTH (R28) — **[catches LOGIC-S1]** second identical `{challenge, code}` ⇒ 401, no second session.

### High — other core-logic specs
- **`S3Download_PresignedUrlExpiry_UsesInjectedClock`** · unit · FILES (GAP-4) — inject `FakeTimeProvider`; presigned `Expires == clock.now + lifetime`; advancing past it ⇒ expired. **[catches GAP-4/LOGIC-B3 pattern]**
- **`Erasure_NewUserKeyedEntity_IsAlsoWiped_ViaContributor`** · unit/arch · GDPR (SOLID-1/R12) — after `IUserDataContributor` exists, every `UserId`-bearing entity outside identity-core has a registered contributor and is wiped. Turns the erasure gap into a red build.
- **`Migrations_Down_RevertCleanly_ToInitial`** · integration · MigrationsTests — migrate up to head, then `Down` to `InitialCreate`; every `Down` runs, schema reverts. Today only up+drift is tested; a broken `Down` is invisible until a production rollback.
- **`Quota_TryConsume_ConcurrentAtLimit_DoesNotOverConsume`** · integration · BILLING (R30) — counter one below limit; two concurrent `TryConsumeAsync` ⇒ exactly one succeeds. **[catches LOGIC-B7]**

### E2E — missing critical journeys (whole suite today = 4 OTP tests, not in CI)
Cross-referenced to the tracked "wire E2E into CI" debt (reconciliation E22) — these specify the *journeys*; the CI/harness wiring is that separate item, not double-counted.
`E2E_OAuth_SignIn_LandsInApp` · `E2E_MagicLink_SignIn_LandsInApp` · `E2E_Tenant_Create_And_Isolation` · `E2E_ReferenceSlice_FullCrud_UnderTenantContext` (needs the sample's UI half — TR-6) · `E2E_Rbac_PermissionDenied_ThreeTier` · `E2E_Mfa_Enroll_Then_StepUp_OnNextLogin` · `E2E_Gdpr_Export_Then_Erasure` · `E2E_Billing_Checkout_To_Webhook_To_Entitlement` (blocked on an absent stripe-mock/fake-provider E2E seam — DOC-21) · `E2E_Admin_Impersonation` · `E2E_I18n_LocaleSwitch`.

---

## Harness-readiness assessment

**Reusable out of the box (strong):** `PostgresFixture` + `PostgresTestBase` (structural per-test TRUNCATE isolation), `ServiceHarness` (real repos/services around one transactional `AppDbContext`, `FakeTimeProvider`, settable `TestCurrentTenant`, stub email/export), `MinioFixture` (real S3). A new **backend** slice gets happy-path + read-isolation testing immediately.

**A new slice author must still rebuild / cannot inherit:**
1. **TR-1 (High) — the harness is load-bearing on the DELETE-ME `Note` entity.** `PostgresFixture.cs:62` hardcodes `"Notes"`; `RepositoryScopingTests`, `TenantStampingInterceptorTests`, `EnterTenantScopingTests`, `OutboxProcessorTests`, `Gdpr/*` all use `Note`/`NotesDataContributor` as the tenant-scoped fixture. Deleting the sample per WAYS_OF_WORKING breaks the tenancy/GDPR/outbox guards and the TRUNCATE. **Top harness blocker** — needs a test-only `TestWidget` fixture entity.
2. **TR-3 — hand-maintained TRUNCATE registry** (`PostgresFixture.cs:62`); a new entity forgotten ⇒ silent cross-test leakage.
3. **No stripe-mock / fake-provider E2E seam** — blocks the billing E2E journey (DOC-21).
4. **E2E base is thin and un-CI'd** — only the OTP login journey has page objects; no CI job boots the app (`ci.yml` builds E2E, never runs it).
5. **`ServiceHarness` doesn't cover the newer epics** (MFA, notifications, files, webhooks, api-keys, billing-webhook handler) — those tests hand-assemble dependencies; a slice touching them builds its own wiring.

**Highest-leverage fixes:** the four Critical fail-open/GAP-1/stale-webhook/SSRF specs (they catch the security-relevant Part-A bugs), the write-side tenancy negatives, the TR-1 test-only fixture entity, and a bootable E2E CI job with the missing journeys.

---

## Rules delta (see `FOUNDATION_RULES.md`)
- **R28 [machine]** — Second-factor verification is single-use: the step-up challenge is consumed on first success, and a TOTP timestep already accepted is rejected (persist last-used step). *(LOGIC-S1, S2)*
- **R29 [machine]** — A projection writer applying external provider events applies only strictly-newer events (recency guard); never blind last-writer-wins. *(LOGIC-B1, B2, B6)*
- **R30 [machine]** — Quota/counter consumption is atomic (conditional UPDATE / row lock / upsert-on-conflict), honoring the "atomically consumes" contract under concurrency. *(LOGIC-B7)*
- **R31 [review→partly machine]** — TDD invariants (consolidates R14): a failing test precedes production code; every slice ships happy-path + permission-denied + cross-tenant-isolation **read and write** negatives + every new public method per branch/error path before "done"; the shared harness is reused, never re-implemented; `QA_TEST_PLAN.md` is updated in the same PR as the code it covers; the QA run log is append-only. Machine-checkable parts: cross-tenant write-negative presence per epic (review today; mechanizable once a slice-test manifest exists), run-log append-only (`git diff` additions-only check).
