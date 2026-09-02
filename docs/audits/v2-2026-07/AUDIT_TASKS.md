# AUDIT_TASKS.md — v2 Remediation Plan (Phase 5 consolidation gate)

> **Status: COMPLETE on `develop`.** Approved 2026-07-01; fully implemented 2026-07-02. **All of V2-B1…B11 are landed** — the Critical + every High, the full B9 SOLID/DEBT refactor set (on the HTTP integration harness), every B11 enforcement + CI-infra gate, and **B8-5 E2E-in-CI** (a bootable Postgres+Mailpit+API+WASM stack drives headless Chromium through the auth, MFA enroll→step-up, and i18n journeys). Test-first, merged to `develop`; build clean (warnings-as-error), Core 42/42, Api 407/407, plus **five green CI jobs** (build-test, secret-scan, license-scan, qa-artifacts, e2e). Residual E2E surfaces are intentionally not browser-automated (unchanged rationale, documented at B8-5): OAuth (external provider), desktop/Android (native runners), and heavy multi-user RBAC / admin-impersonation / GDPR-erasure / billing (integration-covered or manual QA). This plan consolidates Phases 1–4 (all pinned to `84c7ad8`). Order mirrors the archived `audits/v1-2026-06/AUDIT_TASKS.md`: **keystone first, enforcement last.** Each task lists severity, the rule/finding it satisfies, whether it **touches core**, the **tests that must exist first** (TDD), a **done-when**, and a **verify** command.
>
> **Batch commits (on `develop`):** B1 `c74b359` · B2 `10b1b9b` · B3 `28dcce5` · B4 `eec8fdb` · B5 `bbc1196` · B6 `483e516` · B7 `72cba09` · B8 `4464021` · B9(DEBT-2) `184e325` · B10 `41cf3eb` · B11 `fc2d839`. B8–B11 reached `develop` via the re-landing PR #63 (see the landing note under the tracker).

## Gate results (Phase 5)

- **Four reports share SHA `84c7ad8`** — gate OK.
- **`RULE_CONFLICTS.md`: 0 true conflicts.** Two flagged items resolved in `FOUNDATION_RULES.md` §"Phase-5 conflict resolutions" (R5⊕R33 merge; TR-8/DOC-17 doctrine wording). No two Critical invariants are irreconcilable → no human escalation required on rules.
- **Every proposed fix below was cross-checked against the full ruleset** (Phase 5 step 2): none re-introduces another phase's flagged problem. Notable checks — the GAP-1 fix (R1) must not break the "boots with zero setup" dev promise (kept: fake provider still allowed in Development); the SSRF fix (R3) routes through one seam so it can't fork; the AuthController split (SOLID-2) keeps routes stable so no client/contract regression.

## v1 regressions to restore — **NONE**

STEP 0 verified all 31 v1 remediations Held at `84c7ad8` (0 regressed, 0 superseded). There is no regression batch. *(Per the suite's "standing gate for every regressed fix" rule: N/A — nothing regressed. The new enforcement batch V2-B10 nonetheless adds gates that would catch future regressions of the areas touched here.)*

## Decisions required before some tasks (human/ADR calls)

These are intent questions the audit cannot settle; each blocks only its own task, not the batch.

| # | Question | Blocks | Recommended default |
|---|---|---|---|
| D1 | GAP-1 fix shape: fail-fast at startup (throw if no Stripe key outside Development) **or** don't-map the webhook controller when fake? | V2-B1 | ✅ **DECIDED (2026-07-01): fail-fast at startup**; keep fake in Development. |
| D2 | Billing projection: apply only strictly-newer provider events, or accept last-writer-wins? | V2-B4 | **Strictly-newer** (recency guard) — the docstrings already claim it. |
| D3 | Quota guarantee strict (never exceed under concurrency)? | V2-B5 | **Yes** — the "Atomically" contract says so. |
| D4 | Invitation acceptance: email-bound or bearer-capability? | V2-B9 (LOGIC-S3) | Leave **bearer** (document it); add email-binding only if product wants it. |
| D5 | MFA challenge clock: accept the DataProtection ambient-clock limitation or re-implement with injected clock? | V2-B3 | **Accept + document** (no exploit); revisit if a TimeProvider overload appears. |
| D6 | UTC-monthly quota reset for non-UTC tenants — intended? | V2-B5 (LOGIC-B8) | **Document as intended**; tenant-tz reset is a future option. |
| D7 | PUBAPI/HOOKS = controllers (amend epics) or minimal-API platform features (amend ADR-004)? | V2-B8/B9 (DEBT-6/TR-7) | ✅ **DECIDED (2026-07-01): amend ADR-004** to sanction config-gated minimal-API platform surfaces; move files to `src/Api/Endpoints/`. |

---

## Progress tracker

| Batch | Theme | Tasks | Highest sev | Touches core | Status |
|---|---|---|---|---|---|
| **V2-B1** | Billing webhook auth (keystone) | B1-1…B1-3 | **Critical** | yes | ✅ Done (`c74b359`) |
| **V2-B2** | Write-side tenant guard | B2-1…B2-2 | High | yes | ✅ Done (`10b1b9b`) |
| **V2-B3** | MFA step-up anti-replay | B3-1…B3-2 | High | yes | ✅ Done (`28dcce5`) |
| **V2-B4** | Billing event ordering | B4-1…B4-2 | High | no (Api/Core) | ✅ Done (`eec8fdb`) |
| **V2-B5** | Correctness: SSRF, fail-open, quota, clocks | B5-1…B5-5 | High | mixed | ✅ Done (`bbc1196`) |
| **V2-B6** | GDPR per-user erasure seam | B6-1 | High | yes (Core seam) | ✅ Done (`483e516`) |
| **V2-B7** | Harness de-couple from Notes | B7-1…B7-2 | High | tests only | ✅ Done (`72cba09`) |
| **V2-B8** | Test-completeness (test-first) | B8-1…B8-6 | Critical (tests) | tests only | ✅ Done — cross-tenant negatives, HTTP harness, MFA step-up on all 4 paths, migration-Down, **+ E2E-in-CI (B8-5)**¹ |
| **V2-B9** | Debt & SOLID | B9-1…B9-7 | High | yes | ✅ Done — B9-1 split AuthController, B9-2/4 config+DI+model, B9-5 RBAC filter, B9-6 Endpoints move, B9-7 dissolve/email/DTOs (PRs #69/70/71/72 + this) |
| **V2-B10** | Docs reconcile | B10-1…B10-8 | High | no | ✅ Done (`41cf3eb`) |
| **V2-B11** | Enforcement & Definition of Solid | B11-1…B11-9 | — | tests/CI | ✅ Done — all arch gates (R2/4/5/6/7/8/9/12/13/15/24/34/35), correctness pins, Def-of-Solid, + CI-infra (gitleaks, CPM+lockfile, license-scan, QA-artifacts) |

> **Landing note:** B8–B11 were originally opened as stacked PRs #59–#62, which auto-merged into their intermediate base branches instead of `develop`. The four commits were re-landed onto `develop` via a single consolidation PR (this branch); the hashes above are the re-landed commits.

¹ **B8-5 — E2E-in-CI (DONE).** The `e2e` CI job boots a full stack (Postgres + Mailpit + API + Blazor WASM, on http) and drives headless Chromium through the auth smoke, **MFA enroll → login step-up** (live TOTP), and **i18n** journeys, reading OTP codes from Mailpit. The passwordless per-IP limit is config-tunable so the shared-IP browser suite doesn't self-throttle (prod default unchanged). Intentionally **not** browser-automated (documented, unchanged rationale): OAuth (external provider), desktop/Android (native runners), and heavy multi-user RBAC / admin-impersonation / GDPR-erasure / billing — these stay integration-covered (`IntegrationTestFactory` harness + `RbacForbiddenIntegrationTests` + service tests) or in the manual QA plan.
>
> **Every scoped follow-up from earlier drafts is now DONE** — the HTTP harness (B8-6), MFA step-up on every path (B8-2), E2E-in-CI (B8-5), the full B9 SOLID/DEBT set (B9-1/2/4/5/6/7), and every B11 gate incl. CI-infra (secret-scan, CPM+lockfile, license-scan, QA-artifacts, arch gates R6/R13/R24/R35, MailKit ban). **The v2 remediation is fully complete.**

**Recommended order:** V2-B1 → B2 → B3 → B4 → B5 → B6 → B7 → (B8 interleaved as each fix's tests are its own precondition) → B9 → B10 → **B11 last**. B7 before B8's tenancy tests (they need the test-only fixture entity). B11 locks in everything.

---

## V2-B1 — Billing webhook authenticity (KEYSTONE · do first)
The one Critical. Fixes the default-config unauthenticated cross-tenant write.

- [x] **B1-1 · Gate `FakeBillingProvider` to Development** — Critical · (GAP-1, R1) · **touches core**
  - Test-first: `BillingWebhook_FakeProvider_RegisteredOnlyInDevelopment` — build DI with no Stripe key under a non-Development `IHostEnvironment` ⇒ startup **throws** (per D1).
  - Fix: in `ServiceCollectionExtensions.cs:96-99`, register `FakeBillingProvider` only when `environment.IsDevelopment()`; otherwise, absent a Stripe key, throw at startup.
  - Done-when: non-Dev + no key ⇒ startup exception; Dev unchanged; existing billing tests green.
  - Verify: `dotnet test tests/Api.Tests --filter FullyQualifiedName~Billing`
- [x] **B1-2 · Log rejected/forged webhook signatures** — Low · (GAP-5, R19) · touches core
  - Test-first: `BillingWebhook_ForgedSignature_LogsWarning_WithSourceContext`.
  - Fix: warning log (+ optional audit/metric) in the `InvalidSignature` path of `BillingWebhookController`/`BillingWebhookHandler`, incl. source IP.
  - Verify: `dotnet test tests/Api.Tests --filter FullyQualifiedName~BillingWebhook`
- [x] **B1-3 · (D1 confirmed) document the non-Dev-without-Stripe posture** — Low · docs · no core
  - Done-when: `TECH_STACK.md`/ADR-006 note that production requires a real provider; a dated ADR-006 amendment records the environment gate.
  - **Exit check (V2-B1):** an unauthenticated `POST /api/billing/webhook` cannot mint a subscription in a non-Dev build; forged signatures are logged.

## V2-B2 — Write-side tenant guard (UPDATE/DELETE)
- [x] **B2-1 · Interceptor rejects foreign UPDATE/DELETE** — High · (ADV-1, R32) · **touches core**
  - Test-first: `Interceptor_ForeignTenant_Update_And_Delete_AreScoped` — a row loaded via the hatch then `Modified`/`Deleted` under a different current tenant throws.
  - Fix: extend `TenantStampingInterceptor` (`:53`) to inspect `Modified`/`Deleted` `ITenantScoped` entries and throw when `TenantId != currentTenantId` on a tenant context (system/no-tenant context exempt).
  - Verify: `dotnet test tests/Api.Tests --filter FullyQualifiedName~Interceptor`
- [x] **B2-2 · Extend Features arch-ban to `QueryAllTenants`** — Medium · (ADV-2, R5) · tests only
  - Fix: add `QueryAllTenants` to the banned-substring set for `src/Api/Features/**` in `ArchitectureTests.cs`, excluding `*DataContributor.cs`.
  - Done-when: a request-path slice calling `QueryAllTenants()` fails the build; contributors still pass.
  - Verify: `dotnet test tests/Api.Tests --filter FullyQualifiedName~Architecture`
  - **Exit check (V2-B2):** careless slice write-leaks are blocked at CI and at runtime in both directions.

## V2-B3 — MFA step-up anti-replay
- [x] **B3-1 · Consume the step-up challenge on first success** — High · (LOGIC-S1, R28) · **touches core**
  - Test-first: `MfaVerify_ReplayedChallengeAndCode_IsRejectedOnSecondUse`.
  - Fix: make the challenge single-use (cache a jti/hash on success, reject reuse — mirror `SingleUseCacheToken<T>`).
  - Verify: `dotnet test tests/Api.Tests --filter FullyQualifiedName~Mfa`
- [x] **B3-2 · Reject replayed TOTP timestep** — High · (LOGIC-S1/S2, R28) · touches core (Core entity + service)
  - Test-first: same code accepted twice within the window ⇒ second rejected.
  - Fix: persist last-accepted TOTP timestep on `UserMfa`; in `TryVerifyTotp` capture the used step (not `out _`) and reject steps ≤ last accepted; optionally tighten enrollment-confirm window (D5 note: challenge clock left as-is, documented).
  - Migration: adds a column to `UserMfa` (drift test will require it).
  - Verify: `dotnet test tests/Api.Tests --filter FullyQualifiedName~Mfa`
  - **Exit check (V2-B3):** one captured `{challenge, code}` yields at most one session.

## V2-B4 — Billing event ordering
- [x] **B4-1 · Recency guard on the subscription projection** — High · (LOGIC-B1, R29) · Api/Core, no interceptor change
  - Test-first: `BillingWebhook_StaleRedelivery_DoesNotClobberNewerStatus`.
  - Fix: persist a provider `updated`/sequence timestamp on `Subscription`; in `UpsertSubscriptionAsync` apply only strictly-newer events (per D2). Migration adds the column.
  - Verify: `dotnet test tests/Api.Tests --filter FullyQualifiedName~BillingWebhook`
- [x] **B4-2 · Dunning fires on true transitions only** — Medium · (LOGIC-B2/B6) · Api
  - Fix: once B4-1 lands, `MaybeNotifyDunningAsync` reads the recency-ordered status; add a stale→earlier-period guard so the lapse sweep (`SubscriptionLapseSweepJob`) can't be wedged.
  - Test-first: interleaved active/past_due redelivery does not double-notify; a real re-lapse after renewal still nudges.
  - Verify: `dotnet test tests/Api.Tests --filter FullyQualifiedName~Lapse`
  - **Exit check (V2-B4):** out-of-order/redelivered billing events never regress state or mis-fire dunning.

## V2-B5 — Correctness: SSRF, fail-open, quota, clocks
- [x] **B5-1 · SSRF validator for outbound webhook URLs** — High · (GAP-2, CON-1, GAP-3, R3) · Api/Infra
  - Test-first: `WebhookUrl_SsrfTargets_AreRejected` (loopback/link-local/RFC-1918/ULA/metadata + DNS-rebinding), `WebhookUrl_Http_IsRejectedOutsideDevelopment`.
  - Fix: one `SafeHttpClient`/validator seam used by both the sync test (`WebhookEndpoints.cs`) and async `WebhookSender`; https-only outside Dev; return/store a generic error, not `ex.Message`.
  - Verify: `dotnet test tests/Api.Tests --filter FullyQualifiedName~Webhook`
- [x] **B5-2 · Reject all-invalid scopes/event-types** — Medium · (SOLID-3, R17) · Api
  - Test-first: `ApiKeyScopes_AllInvalidInput_IsRejected_NotGrantedAll`, `WebhookEventTypes_AllInvalidInput_IsRejected_NotGrantedAll`.
  - Fix: distinguish `null` (default-all) from provided-but-all-invalid (400) in `ApiKeyService.NormalizeScopes` + `WebhookSubscriptionService.NormalizeEventTypes`.
  - Verify: `dotnet test tests/Api.Tests --filter "FullyQualifiedName~ApiKey|FullyQualifiedName~Webhook"`
- [x] **B5-3 · Atomic quota consumption** — High · (LOGIC-B7, R30) · Api/Infra
  - Test-first: `Quota_TryConsume_ConcurrentAtLimit_DoesNotOverConsume` (two concurrent consumers at limit-1 ⇒ exactly one succeeds; first-of-period race doesn't 500).
  - Fix: conditional `ExecuteUpdateAsync` guarded by `Count+amount<=limit` (check rows-affected) + upsert-on-conflict for the create path (per D3).
  - Verify: `dotnet test tests/Api.Tests --filter FullyQualifiedName~Quota`
- [x] **B5-4 · Inject `TimeProvider` into `CookieService` + `S3FileStorage`** — Medium · (LOGIC-B3, GAP-4, R15) · Api/Infra
  - Test-first: `S3Download_PresignedUrlExpiry_UsesInjectedClock`; cookie expiry matches the token's injected-clock expiry.
  - Fix: replace ambient `DateTimeOffset.UtcNow`/`DateTime.UtcNow` at `CookieService.cs:36` + `S3FileStorage.cs:81` with the injected clock.
  - Verify: `dotnet test tests/Api.Tests --filter "FullyQualifiedName~Cookie|FullyQualifiedName~S3"`
- [x] **B5-5 · (D6) document UTC-monthly quota reset** — Low · docs · no core
  - **Exit check (V2-B5):** no SSRF target reachable; no full-access grant from a typo; quota cap holds under concurrency; expiries are clock-injected.

## V2-B6 — GDPR per-user erasure seam
- [x] **B6-1 · Introduce `IUserDataContributor`** — High · (SOLID-1, R12) · **touches core (new Core seam)**
  - Test-first: `Erasure_NewUserKeyedEntity_IsAlsoWiped_ViaContributor` + an arch test (R12) asserting every `UserId`-bearing entity outside the identity-core allowlist has a registered contributor.
  - Fix: add `IUserDataContributor { WipeAsync(userId, ct) }` to `Core.Abstractions`; move MFA + NOTIFY per-user deletes into contributors; `AccountErasureService` keeps identity-core rows + the contributor loop.
  - Verify: `dotnet test tests/Api.Tests --filter FullyQualifiedName~Erasure`
  - **Exit check (V2-B6):** a new user-keyed entity cannot ship without a contributor (red build), so GDPR erasure stays complete.

## V2-B7 — Harness de-couple from the DELETE-ME slice
- [x] **B7-1 · Test-only `ITenantScoped` fixture entity** — High · (TR-1, R9) · tests only
  - Fix: add a harness-owned `TestWidget` (test project) as the tenant-scoped fixture; migrate `RepositoryScopingTests`, `TenantStampingInterceptorTests`, `EnterTenantScopingTests`, `OutboxProcessorTests`, `Gdpr/*` off `Note`/`NotesDataContributor`; keep one `NotesSliceTests` proving the sample.
  - Done-when: deleting `src/Api/Features/Notes/` + its entity leaves the tenancy/GDPR/outbox tests green.
  - Verify: `dotnet test tests/Api.Tests` (then, in a scratch branch, delete Notes and re-run to confirm)
- [x] **B7-2 · Derive fixture TRUNCATE from the model** — Medium · (TR-3, R11, R34) · tests only
  - Fix: `PostgresFixture.cs:62` builds its reset list from `AppDbContext.Model.GetEntityTypes()`.
  - Verify: `dotnet test tests/Api.Tests`
  - **Exit check (V2-B7):** the sample slice is deletable per the docs without breaking the platform's own guards.

## V2-B8 — Test-completeness (test-first specs → tests)
Each spec from `LOGIC_AND_TEST_REPORT.md` Part B not already created by B1–B7. Land as failing-then-green.
- [x] **B8-1 · Write-side tenancy negatives per epic** — Critical(test) — ✅ complete: `Revoke_CannotRevokeAnotherTenantsKey` (ApiKey), `Delete_CannotDeleteAnotherTenantsSubscription` + `Replay_UnknownOrOtherTenant_ReturnsFalse` (Webhook), `Subscription_TenantA_CannotRead/Update/DeleteTenantBSubscription` (Billing), `List_TenantAUser_CannotSeeTenantBUsersNotifications` (Notify, user-keyed boundary).
- [x] **B8-2 · MFA step-up on every path (integration)** — High — `MfaStepUpIntegrationTests` drives all four sign-in paths at the HTTP boundary via the harness: OTP + native-exchange (challenge JSON → `mfa/verify` with a real TOTP → session), magic-link + OAuth callback (302 → `/login?mfa=`), plus a wrong-code 401 and a no-MFA contrast. A test `External`-scheme handler drives the OAuth callback without a real provider.
- [x] **B8-3 · Migration `Down` rollback** — High — `Migrations_Down_RevertCleanly_ToEmptySchema` (`4464021`).
- [x] **B8-4 · Fail-open / SSRF / stale-webhook / clock** — covered by B5-1/2/4 + B4-1 tests; no duplication.
- [x] **B8-5 · E2E journeys** — the E2E suite now runs **in CI on a bootable stack** (`e2e` job: Postgres + Mailpit service containers + API + Blazor WASM on http + headless Chromium, reading OTP from Mailpit). Journeys: auth smoke (login/OTP sign-in/sign-out/validation), **MFA enroll → login step-up** (reads the enrollment secret from the UI, computes live TOTP; + a wrong-code negative), and **i18n** language switch. The IP-partitioned passwordless limit is now config-tunable (`Auth:RateLimit:PasswordlessPermitLimit`) so the shared-IP browser suite doesn't self-throttle (prod default 5 unchanged). Deliberately **not** automated (unchanged rationale): OAuth (needs an external provider), desktop/Android (native runners); tenant-isolation / RBAC three-tier / admin-impersonation / GDPR erasure remain **integration-covered** (harness + `RbacForbiddenIntegrationTests` + service tests) rather than driven through multi-user/staff browser orchestration; billing E2E still awaits a fake-provider checkout seam. Covers the reliable, high-value browser journeys; the rest stay in the QA plan / integration layer.
- [x] **B8-6 · Harness gaps** — delivered as `IntegrationTestFactory` (`WebApplicationFactory<Program>` + throwaway Postgres): boots the real app, seeds tenants/users, mints real tokens, and asserts the full auth→tenant-filter→controller→Postgres pipeline at the wire (`HarnessSmokeTests`). Supersedes the narrower "extend `ServiceHarness`" plan and unblocks B8-2/B9-1.
  - **Exit check (V2-B8):** every Critical/High spec in Part B exists and is green (or E2E explicitly tracked in CI).

## V2-B9 — Debt & SOLID
- [x] **B9-1 · Split `AuthController`** — High · (SOLID-2) · touches core — the 679-line god class became a shared `AuthControllerBase` + focused `AuthController` (login/callback/refresh/logout/passwordless, 341 lines), `MfaController`, `AccountController`, `NativeAuthController` — all `[Route("api/auth")]`, so the 22 routes are byte-identical; each ctor takes only its own deps. Route-stability + behavior asserted end-to-end by the B8 harness (integration + arch suites green).
- [x] **B9-2 · One config-binding pattern** — Medium · (DEBT-1, R22) — one `AddAppSettings` extension registers all five settings singletons (IConfiguration-ctor pattern kept — the scattered `Auth:*` keys don't map to a single section, and `.env` compat matters); validation stays at construction (startup). The duplicate `new JwtSettings` is gone — Program builds it once and reuses that instance for the JWT-bearer `TokenValidationParameters`.
- [x] **B9-3 · `ClaimsPrincipal.GetUserId()` helper** — Medium · (DEBT-2) — deleted the 6 copies; centralized in `ClaimsPrincipalExtensions.GetUserId()` (`184e325`).
- [x] **B9-4 · Per-epic `Add*()/Map*()` extensions + `IEntityTypeConfiguration<>`** — Medium · (DEBT-3/4, R34) — Program's flat registration block became per-concern extensions (`AddAuthServices`/`AddTenantServices`/`AddMfaServices`/`AddNotificationServices`/`AddPlatformAdminServices`/`AddRbacServices`/`AddBillingServices`), same lifetimes; Program 314→245 lines. `OnModelCreating`'s ~230 inline lines became 20 `IEntityTypeConfiguration<>` classes + `ApplyConfigurationsFromAssembly` (global tenant-filter loop kept); model byte-identical (migration-drift test green).
- [x] **B9-5 · Unify RBAC 403 (filter) + shared `ErrorResponse`** — Medium · (DEBT-5, SOLID-7, R18) — `[RequireTenantPermission(Permission, message)]` action filter replaces the inline `if (RequirePermission(...) is {} forbidden)` gate across Household/Invitations/Billing (mirrors the exact null-membership→401 / denied→403 envelope; behavior pinned by `RbacForbiddenIntegrationTests` at the wire). `IErrorResponseFactory`/`ErrorResponseFactory` dropped — the `ErrorResponse` record is used directly (60 `CreateError` call-sites → `new ErrorResponse(...)`).
- [x] **B9-6 · Move PUBAPI/HOOKS per D7; extract `TenantDissolutionService`; branded notification email** — Medium · (DEBT-6/7/8, TR-7) — ✅ **DEBT-6**: PUBAPI/HOOKS (`ApiKeyEndpoints`/`WebhookEndpoints`) + the shared endpoint-extension helpers moved `Features/` → `src/Api/Endpoints/` (`Perezosoft.Api.Endpoints`); `Features/` now holds only the Notes sample; ADR-004 amended (D7); the **R6 gate** landed → **B11-1 complete**. ✅ **DEBT-7** (`TenantDissolutionService`) + **DEBT-8** (branded notification email) delivered in B9-7 (PR9).
- [x] **B9-7 · Lower-value smells** — Low · (DEBT-9/10/11, SOLID-5/6/8, LOGIC-S3 per D4) — **DEBT-7** `TenantDissolutionService` extracted (the shared contributors→`WipeDataAsync` sequence; both leave + erasure call it, same order, in the caller's transaction); **DEBT-8** notification email branded (`BrandedEmail.Notification`, HTML-encoding preserved); **SOLID-5** auth DTOs moved out of `AuthController` → `Models/AuthModels.cs`; **D4** documented (invitation accept is intentional bearer-capability). `AuthSchemes` constant / result-types / locale-constant skipped — code already uses named constants + consistent patterns (no concrete win).
  - **Exit check (V2-B9):** the patterns every slice copies are single-sourced; no god controller.

## V2-B10 — Docs reconcile
- [ ] **B10-1 · `DATA_MODEL.md`: add the 4 live entities** — High · (DOC-10) — `ApiKey`, `WebhookSubscription`, `WebhookDelivery`, `UsageCounter`; retitle built-vs-future; fix the dissolve-hook guidance to `ITenantDataContributor` (DOC-11); add `LapseNotifiedAt` (DOC-12).
- [ ] **B10-2 · CLAUDE.md golden rule 1 + `DATA_MODEL.md:13`: `QueryAllTenants()`/`EnterTenant`, not `IgnoreQueryFilters()`** — Medium · (DOC-17, R23).
- [ ] **B10-3 · `WAYS_OF_WORKING.md` slice recipe** — Medium · (DOC-18) — `MapTenantFeatureGroup`, `ExportKey`+`ExportAsync`, the `.RequirePermission`/`.RequireEntitlement` filters, the full touchpoint checklist.
- [ ] **B10-4 · `docs/stories/ui.md` (retrospective) + ROADMAP note** — Medium · (DOC-22).
- [ ] **B10-5 · ROADMAP/BACKLOG/FEATURES done-markers** — Medium · (DOC-1/2/3/4/5/6/7/8/9).
- [ ] **B10-6 · ADR amendments** — Low · (DOC-13 ADR-014 EnterTenant + story fix; DOC-14 ADR-016 HOOKS-2; DOC-15 ADR-015 PUBAPI-2; DOC-20 ADR-008(b); DOC-21 ADR-006 stripe-mock; ADR-004 drift per D7/TR-2/7/8).
- [ ] **B10-7 · CLAUDE.md doc-map PUBAPI-2; `TECH_STACK.md` missing packages; QA_TEST_PLAN no-UI list** — Low · (DOC-16/23/19).
- [ ] **B10-8 · 403 message strings "owner or admin"** — Low · (DOC-24) — `HouseholdInvitationsController` + resx.
  - **Exit check (V2-B10):** the auto-loaded manuals compile-if-followed and match code; no "done" marker lies.

## V2-B11 — Enforcement & Definition of Solid (LAST · locks everything)
Add each machine rule as an arch test / analyzer / CI step (backlog `AUDIT_RECONCILIATION.md` §7 E1–E22 + R32/R34/R35/R28/R29/R30 tests).
- [x] **B11-1 · Arch tests:** ✅ shipped: R2 (`EveryEntityWithATenantId_IsScopedOrAllowlisted`), R4 (`EveryController_DerivesFromATenantOrAdminBase_OrIsAllowlisted`), R5 (Features ban both hatches — from B2), R7 (`FeatureFolders_DoNotReferenceEachOthersNamespaces`), R8 (`OnlyProgram_ReferencesFeatureNamespaces_FromOutsideFeatures`), R9 (`PlatformTests_DoNotDependOnTheDeleteMeNotesSample`), R15 (`ServerServices_UseInjectedClock_NotAmbientUtcNow`), R34 (fixture=model — from B7), R35 (`RouteGroupPrefixes_AreUnique` + `TenantScopedEntities_MapToDistinctTables`), R6 (`FeatureFiles_RegisterRoutesViaMapTenantFeatureGroup_NotRawMapGroup`, after the B9-6 PUBAPI/HOOKS move). ✅ **B11-1 complete.**
- [x] **B11-2 · Correctness pins:** R28 (MFA replay), R29 (stale webhook), R30 (atomic quota), R17 (fail-open), R32 (write UPDATE/DELETE) — shipped as standing tests alongside B2–B5.
- [x] **B11-3 · R12 user-data-contributor coverage test** — `EveryUserKeyedEntity_IsWiredIntoAccountErasure` (after B6).
- [x] **B11-4 · R13 ExportKey uniqueness test** — `TenantDataContributors_HaveUniqueExportKeys`.
- [x] **B11-5 · CI doc-sync (R23) + config-key⇄.env (R20) + secret scan (gitleaks) + MailKit-outside-Email ban** — `DocAndConfigSyncTests`: `MailKit_StaysBehindInfrastructureEmail` (R-ban), `EveryEntity_IsDocumentedInDataModel` (R23 doc-sync), `ConfigKeys_ReadInCode_AreDocumented` (R20 — caught 3 undocumented keys, now in `.env.example`). Plus a `secret-scan` CI job running the gitleaks OSS binary (no org-license) with `.gitleaks.toml` allowlisting example/test-only values. *(R23's story-✅⇄ROADMAP sync left to review — too intent-based for a reliable grep; the concrete entity/config drift is now gated.)*
- [x] **B11-6 · Supply chain:** R25 (CPM + lockfile + `--locked-mode`), R26 (license scan), R27 — `Directory.Packages.props` centralizes every version (Test.Sdk consolidated 17.14.0→.1; MAUI keeps `$(MauiVersion)`); `RestorePackagesWithLockFile=true` + 8 committed `packages.lock.json` (MAUI excluded — not CI-built); CI restores `--locked-mode` then builds/tests `--no-restore`. A `license-scan` job (`dotnet-project-licenses` + `.github/forbidden-licenses.json`) fails only on GPL/AGPL/LGPL/SSPL (prohibited-list → no MS/permissive false positives).
- [x] **B11-7 · MA0048 file-name analyzer (R24 naming half)** — `SourceFile_DeclaresATypeMatchingItsName` arch test (a file declares a type matching its name; `*Models` DTO aggregations + `SettingsProvider`/`WebhookService` exempt). Realized as a source-scan gate rather than the Meziantou MA0048 analyzer, which would enable ~150 unrelated rules under warnings-as-error (unbounded churn) — same guarantee, bounded.
- [x] **B11-8 · QA: move guide-PDF generation into CI (deterministic); assert run-log append-only; assert QA plan + PDFs change together** — `docs/check_qa_artifacts.py` + a `qa-artifacts` CI job regenerate both PDFs (guide + run-log) and compare **extracted text** (date/whitespace-normalized, since PDFs aren't byte-deterministic) against the committed artifacts, failing on drift — so editing `QA_TEST_PLAN.md` without re-running the generators is caught (subsumes plan⇄PDF co-change + keeps the run-log's case list in sync). reportlab/pypdf pinned. The gate found + fixed a real stale-guide drift on landing.
- [x] **B11-9 · Definition of Solid + Standing Instruction:** `CONTRIBUTING.md` updated with the new invariants; `FOUNDATION_RULES.md`-binding standing instruction added to `CLAUDE.md` (per the suite's STANDING INSTRUCTION block). E2E-in-CI decision (B8-5) deferred with the harness.
  - **Exit check (V2-B11):** every machine rule in `FOUNDATION_RULES.md` v1.0 is a green gate; a generated clone inherits them; the doc-only floor (TR-9) is now CI-enforced.

---

## Coverage map (every finding → a task)

- **Critical:** GAP-1 → B1-1/B1-2.
- **High:** SOLID-1→B6-1 · GAP-2→B5-1 · TR-1→B7-1 · DOC-10→B10-1 · SOLID-2→B9-1 · LOGIC-S1→B3 · LOGIC-B1→B4-1 · ADV-1→B2-1.
- **Medium:** LOGIC-B7→B5-3 · LOGIC-B3/GAP-4→B5-4 · LOGIC-B2→B4-2 · SOLID-3→B5-2 · CON-1/GAP-3→B5-1 · ADV-2→B2-2 · DEBT-1..7→B9 · DOC-13/16/17/18/22 + stale markers→B10 · T1(supply)→B11-6 · CON-2→B11-5 · CON-3/DEBT-5→B9-5.
- **Low:** GAP-5→B1-2 · LOGIC-B5/B6/B8, S2/S3→B3-2/B4-2/B9-7 (+D-decisions) · ARCH-1..3, TR-2..9, SOLID-4..8, DEBT-8..12, DOC (low)→B9/B10 · T2/T3/T4→B11-6/B8.
- **Enforcement (R1–R35 machine):** B11.

**Plan approved 2026-07-01 and implemented.** The Critical + all High batches (B1–B7) and the core of B8/B10/B11 + DEBT-2 landed on `develop`, test-first, each verified against `FOUNDATION_RULES.md` v1.0. **Before generating the first real app**, clear the scoped follow-ups (HTTP/E2E harness → the B9 refactors it gates → the B11 CI-infra gates) and re-run the Phase-4 adversarial slice pass (B2/B6/B7 were horizontal-concern changes).
