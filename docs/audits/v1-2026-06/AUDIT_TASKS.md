# Audit Remediation Backlog

> Actionable plan for the findings in [`AUDIT_REPORT.md`](AUDIT_REPORT.md) (read it for full evidence
> per `CONF-n`/`MITI-n` ID). Work is grouped into **batches sized for one session each**; batches are
> mostly independent so they can be done in any order across different sessions — except **Batch 1
> first** (it's the keystone) and **Batch 9 last** (it locks in everything else).
>
> **How to use this in a fresh session:** pick a batch, read the linked report section for evidence,
> do it TDD per `WAYS_OF_WORKING.md` (failing test first), tick the boxes, commit per Conventional
> Commits, then check the batch's **Exit check** before moving on. Each task is self-contained:
> files, problem, fix, acceptance criteria, and a verify command.
>
> Legend — Sev: High/Med/Low · Effort: S (<1h) · M (a session) · L (multi-session).
> Status markers: `[ ]` todo · `[~]` in progress · `[x]` done (add the commit SHA).

---

## Progress tracker

| Batch | Theme | Tasks | Highest sev | Status |
|-------|-------|-------|-------------|--------|
| **B1** | Tenant write-safety (keystone) | B1-1…B1-4 | High | [x] |
| **B2** | Passwordless hardening | B2-1…B2-3 | High | [x] |
| **B3** | Auth token hygiene | B3-1…B3-4 | High | [x] |
| **B4** | Reference slice + feature scaffolding | B4-1…B4-3 | Low | [x] |
| **B5** | Persistence correctness | B5-1…B5-5 | Low | [x] |
| **B6** | Test foundation | B6-1…B6-4 | Med | [x] |
| **B7** | UI / UX | B7-1…B7-3 | Med | [x] |
| **B8** | Docs reconcile | B8-1…B8-4 | Low | [x] |
| **B9** | Enforcement & "Definition of Solid" | B9-1…B9-4 | — | [x] |

**Recommended order:** B1 → B3 → B2 → (B4–B8 in any order) → **B9 last**. Do B1 before B6 (the
isolation tests in B6 lean on the B1 invariant).

---

## Batch 1 — Tenant write-safety (the keystone) · do first
Makes tenant isolation structural in *both* directions. Report §3 (CONF-1), §4 (CONF-2, MITI-1).

- [x] **B1-1 · Write-side tenant stamping interceptor** — Sev: High · Effort: M · (CONF-1)
  - Files: new `src/Infrastructure/Persistence/TenantStampingInterceptor.cs`; wired in `src/Infrastructure/Persistence/AppDbContext.cs` (`OnConfiguring`).
  - Problem: the global query filter scopes reads only; nothing sets/validates `TenantId` on insert, so a feature can persist a row under the wrong tenant and the read filter then hides it from its true owner (invisible bug).
  - Fix: added a stateless `SaveChangesInterceptor` that, for every `Added` entry implementing `ITenantScoped` *while a tenant is current*: if `TenantId == default` → stamps `AppDbContext.CurrentTenantId`; if `TenantId != default && != CurrentTenantId` → **throws** (fail closed). A tenant-less (`CurrentTenantId == Guid.Empty`) system/seed context is not enforced. Registered in `OnConfiguring` (not DI) so direct-constructed test contexts enforce it too. Legitimate cross-tenant writes run on a system context or the named unscoped path (B1-3).
  - Done-when:
    - [x] Test: inserting an `ITenantScoped` entity with no `TenantId` set stamps the current tenant. (`TenantStampingInterceptorTests`)
    - [x] Test: inserting with a *foreign* `TenantId` throws (does not persist). (`TenantStampingInterceptorTests`)
    - [x] Existing Notes/tenant tests still green.
  - Verify: `dotnet test tests/Api.Tests`

- [x] **B1-2 · Make `WipeDataAsync` filter-independent** — Sev: Low · Effort: S · (CONF-2)
  - Files: `src/Infrastructure/Repositories/TenantRepository.cs`.
  - Problem: invitation delete is double-filtered by the global filter, membership delete is not; correct only because today's sole caller's `CurrentTenantId == tenantId` (and the `Tenant` FK cascade masks the orphan). Note found while fixing: the orphan is currently latent — the `OnDelete(Cascade)` FK from `TenantInvitation` to `Tenant` already removes invitations when the tenant row is deleted, so the bug is not live; the fix removes the hidden dependency on that cascade and on `current == target`.
  - Fix: all three sets now deleted via `IgnoreQueryFilters().Where(x => x.TenantId == tenantId).ExecuteDeleteAsync(...)`, so teardown targets exactly the argument tenant. `ExecuteDeleteAsync` enlists in the ambient `EfUnitOfWork` transaction the dissolve flow opens.
  - Done-when: [x] a test dissolving a tenant while a *different* tenant is "current" removes that tenant's rows (no orphans). (`WipeDataTests`) Verify: `dotnet test tests/Api.Tests`.

- [x] **B1-3 · Named unscoped read surface** — Sev: Med · Effort: M · (MITI-1)
  - Files: `src/Core/Repositories/IRepository.cs`, `src/Infrastructure/Repositories/EfRepository.cs`, `src/Api/Features/Notes/NotesDataContributor.cs`.
  - Problem: `Query()` returns raw `IQueryable`, so a feature can chain `IgnoreQueryFilters()` and read every tenant's rows — the "can't forget to scope" promise is bypassable.
  - Fix: kept `Query()` tenant-scoped; added explicit, rare, greppable `IQueryable<T> QueryAllTenants()` (`Set<T>().IgnoreQueryFilters()`) for cross-tenant needs and migrated `NotesDataContributor` to it. (The build-time ban on `IgnoreQueryFilters` in features lands in B9-1.)
  - Done-when: [x] contributors use `QueryAllTenants()`; [x] feature reads have no ad-hoc unscoping call in their normal path; [x] tests green (`RepositoryScopingTests`).

- [x] **B1-4 · Doc the write-safety model in ADR-003** — Sev: Low · Effort: S
  - Files: `docs/DECISIONS.md` (ADR-003).
  - Fix: added a dated amendment to ADR-003 stating scoping is now structural on **both** read (query filter) and write (stamping interceptor), and that `QueryAllTenants()`/`IgnoreQueryFilters` is the audited cross-tenant escape hatch.
  - Done-when: [x] ADR-003 reflects the implemented reality.

**Exit check (B1):** `dotnet test tests/Api.Tests` green; a foreign-tenant insert throws; dissolve leaves no orphans.

---

## Batch 2 — Passwordless hardening
Report §3 (CONF-5), §4 (CONF-6). Security-focused session.

- [x] **B2-1 · Rate-limit OTP/magic-link send + verify** — Sev: High · Effort: M · (CONF-5)
  - Files: new `src/Api/Configuration/RateLimiting.cs`, `src/Api/Program.cs`, `src/Api/Controllers/AuthController.cs` (`/otp/send`, `/magic-link/send`, `/otp/verify`).
  - Problem: no rate limiting anywhere; `/otp/send` and `/magic-link/send` are an unthrottled email-bomb / cost amplifier and enable unbounded OTP guessing rounds.
  - Fix: added a shared `AddPasswordlessRateLimiter()` extension defining a named `"passwordless"` fixed-window policy (5/min) partitioned by client IP; `app.UseRateLimiter()` wired after auth; the three endpoints carry `[EnableRateLimiting("passwordless")]`; trips return 429.
  - **Deviation (justified):** partitioned by **IP** (not "email AND IP"). Reading the email for partitioning requires buffering the request body before model binding (fragile); the per-email, IP-independent dimension is instead enforced by B2-2's cumulative DB lockout, which is strictly stronger against distributed guessing. Limit/window are constants in `RateLimiting.cs`.
  - Done-when: [x] integration test floods one client → 429 after the limit, exercising the REAL policy through rate-limiter middleware in a minimal `TestServer` (`RateLimitingTests`). The same policy is applied to all three endpoints (send + verify).
  - Verify: `dotnet test tests/Api.Tests`

- [x] **B2-2 · Stop the attempt-counter reset on resend** — Sev: High · Effort: M · (CONF-5)
  - Files: `src/Api/Services/PasswordlessService.cs` (`RedeemOtpAsync`), `src/Core/Repositories/ILoginTokenRepository.cs` + `src/Infrastructure/Repositories/LoginTokenRepository.cs` (`CountFailedAttemptsSinceAsync`), `IPasswordlessSettings`/`PasswordlessSettings` + `appsettings.json` (`OtpLockoutWindowMinutes`, default 15).
  - Problem: each resend invalidates the old code and mints a fresh `AttemptCount=0` code, so the per-code cap (5) is trivially reset; the latest code is always the one redeemed.
  - Fix: lockout is now CUMULATIVE per email over a sliding window — `RedeemOtpAsync` sums failed attempts across all codes issued to the email since `now - OtpLockoutWindowMinutes` and returns `TooManyAttempts` once the sum hits `OtpMaxAttempts`, regardless of code rotation. The historical codes keep their `AttemptCount` (only `ConsumedAt` is set on rotation), so the sum survives resends; failures age out of the window for an automatic cooldown.
  - Done-when: [x] test: 5 wrong guesses → resend → the fresh (even correct) code stays locked (`Otp_ResendAfterLockout_...`); [x] lockout clears after the window (`Otp_LockoutClearsAfterWindowElapses`).
  - Verify: `dotnet test tests/Api.Tests`.

- [x] **B2-3 · Collapse OTP verify enumeration signal** — Sev: Low · Effort: S · (CONF-6)
  - Files: `src/Api/Services/PasswordlessService.cs` (`OtpErrors.ClientCode`), `src/Api/Controllers/AuthController.cs` (`VerifyOtp`).
  - Problem: verify returned distinct `code_expired` vs `invalid_code`, leaking whether an address has an outstanding OTP.
  - Fix: `OtpErrors.ClientCode` maps both `Expired` (no active code) and `Invalid` (wrong code) to one `invalid_code`; the `OtpStatus` distinction stays server-side. (`too_many_attempts` kept — lockout is a deliberate, in-scope-excluded signal.)
  - Done-when: [x] test: wrong-code vs no-active-code yield the identical client error (`OtpErrorMappingTests`).

**Exit check (B2):** OTP cannot be guessed by resend-looping; send endpoints 429 under flood; verify is enumeration-neutral.

---

## Batch 3 — Auth token hygiene
Report §3 (MITI-3), §4 (CONF-4, CONF-7, CONF-8).

- [x] **B3-1 · `email_verified` fails closed** — Sev: High · Effort: S · (MITI-3)
  - Files: `src/Api/Services/ClaimsExtractor.cs`, `src/Api/Services/IClaimsExtractor.cs`.
  - Problem: absent `email_verified` claim ⇒ `true`, defeating the documented fail-closed takeover guard; Microsoft may omit the claim.
  - Fix: `IsEmailVerified` now returns `true` **only** when the claim is explicitly `"true"` (case-insensitive); absent/empty/anything-else ⇒ `false`. Reconciled the XML comments on both the impl and the interface (UserService's fail-closed default was already consistent).
  - Done-when: [x] unit test proving an absent (and non-"true") claim is NOT verified (`ClaimsExtractorTests`); [x] comments agree.

- [x] **B3-2 · Refresh-token reuse detection** — Sev: Med · Effort: M · (CONF-4)
  - Files: `src/Core/Repositories/IRefreshTokenRepository.cs`, `src/Infrastructure/Repositories/RefreshTokenRepository.cs`, `src/Api/Services/RefreshTokenService.cs`, `src/Api/Controllers/AuthController.cs`.
  - Problem: replaying an already-rotated token returned null (filtered by `!IsRevoked`) → silent 401, no theft response.
  - Fix: added `GetByHashAsync` (no revoked/expiry filter) + `InspectRefreshTokenAsync` returning `RefreshTokenStatus` {Valid, Expired, Unknown, Reuse}. On `/refresh`, a found-but-revoked hash ⇒ **Reuse** ⇒ `RevokeAllUserTokensAsync(userId)` + audit log; the client gets the *same* generic `invalid_refresh_token` as an unknown/expired token (no enumeration leak). Softened the AuthController "replayed once" comment.
  - **Deviation (justified):** chose per-user revoke over adding a `FamilyId`/lineage column. The task's own fix text endorses `RevokeAllForUserAsync`; it fully meets the done-when and the security goal while avoiding a schema migration (and the migration-drift concern is separately tracked in B5-1). True per-lineage `FamilyId` is a documented optional refinement if multi-device session granularity is ever needed.
  - Done-when: [x] test: replaying a rotated token is detected as reuse and revoking-all kills the live token (`RefreshTokenServiceTests`); [x] unknown token classified Unknown (→ 401), distinct from reuse server-side.

- [x] **B3-3 · Inject `TimeProvider` into `JwtTokenService`** — Sev: Med · Effort: S · (CONF-8)
  - Files: `src/Api/Services/JwtTokenService.cs` (+ ctor), `tests/Api.Tests/Infrastructure/ServiceHarness.cs`.
  - Fix: added `TimeProvider clock` to the ctor (DI-registered `TimeProvider.System`); `expires` now `clock.GetUtcNow().UtcDateTime.AddMinutes(settings.ExpiryMinutes)`.
  - Done-when: [x] no `DateTime.UtcNow` in the service; [x] test asserts token `exp` against a `FakeTimeProvider` (`JwtTokenServiceTests`).

- [x] **B3-4 · Single source for `TokenValidationParameters`** — Sev: Low · Effort: S · (CONF-7)
  - Files: new `src/Api/Configuration/JwtValidation.cs`, `src/Api/Program.cs`, `src/Api/Services/JwtTokenService.cs`.
  - Fix: extracted `IJwtSettings.CreateParameters()` consumed by both the bearer handler and `JwtTokenService.ValidateToken`; removed the duplicated inline params (and now-dead usings in `Program.cs`).
  - Done-when: [x] validation rules defined once; [x] tests green.

**Exit check (B3):** absent-claim takeover test passes; refresh reuse revokes the family; token lifetime is clock-injected; one validation-param source.

---

## Batch 4 — Reference slice + feature scaffolding
Report §4 (CONF-9, CONF-3, CONF-10). Makes the copy-me exemplar exemplary.

- [x] **B4-1 · Notes slice uses injected clock** — Sev: Low · Effort: S · (CONF-9)
  - Files: `src/Api/Features/Notes/NotesHandler.cs` (+ ctor); `tests/Api.Tests/NotesSliceTests.cs`.
  - Fix: added `TimeProvider clock` to the handler's primary ctor; `CreatedAt`/`UpdatedAt` now come from `clock.GetUtcNow()`.
  - Done-when: [x] `Create_StampsCreatedAt_FromInjectedClock` asserts `CreatedAt`/`UpdatedAt` via `FakeTimeProvider`. (Also cleaned up the CS9107 warning the B6-3 base-class introduced: `PostgresTestBase` now exposes `Fixture` and derived bodies use it.)

- [x] **B4-2 · Shared feature-endpoint scaffolding + single authz policy** — Sev: Low · Effort: M · (CONF-3)
  - Files: new `src/Api/Configuration/AuthPolicies.cs`, new `src/Api/Features/FeatureEndpointExtensions.cs`, `src/Api/Features/Notes/NotesEndpoints.cs`, `src/Api/Program.cs`, `src/Api/Controllers/TenantApiControllerBase.cs`.
  - Fix: one named policy `AuthPolicies.TenantApi` (`RequireAuthenticatedUser` + JWT-bearer scheme) registered via a shared `AddTenantApiAuthorization()` extension; `MapTenantFeatureGroup(prefix)` returns a group already guarded by it; both the controller base `[Authorize(AuthPolicies.TenantApi)]` and Notes use the single source.
  - **Deviation (justified):** deliberately did NOT add a global fallback authorization policy — this app has many intentionally-anonymous endpoints (login, OAuth callback, refresh, OTP send) that aren't `[AllowAnonymous]`, so a fallback would force auth on them and break sign-in. `MapTenantFeatureGroup` (always applies the policy) is the safer guard against a forgotten `.RequireAuthorization`.
  - Done-when: [x] Notes registers via the helper; [x] one policy is the single source; [x] a TestServer test hits a feature group unauthenticated → 401 (`FeatureAuthorizationTests`).

- [x] **B4-3 · Extract `SingleUseCacheToken<T>`** — Sev: Low · Effort: M · (CONF-10)
  - Files: new `src/Api/Services/SingleUseCacheToken.cs`, `src/Api/Services/LinkTokenService.cs`, `src/Api/Services/NativeAuthCodeService.cs`.
  - Fix: generic `SingleUseCacheToken<T>` (Issue / TryConsume-once over `IMemoryCache`); both services delegate to it, preserving their cache-key prefixes + 5-min lifetime (backward-compatible). `TryConsume(out T)` keeps the value-type payloads (`Guid`, `NativeAuthGrant`) clean.
  - Done-when: [x] both services delegate; [x] `SingleUseCacheTokenTests` covers consume-once on the shared primitive (existing service tests still green).

**Exit check (B4):** Notes demonstrates clock + scaffolding correctly; no duplicated token primitive.

---

## Batch 5 — Persistence correctness
Report §4 (CONF-11, CONF-12, CONF-13, CONF-14, MITI-2).

- [x] **B5-1 · Resolve migration/model drift** — Sev: Low · Effort: S · (CONF-11)
  - Files: `src/Infrastructure/Persistence/Migrations/20260617235537_InitialCreate.cs`.
  - Fix: `has-pending-model-changes` was already clean (model==snapshot after the earlier snapshot-sync); edited the two `RefreshToken` columns in the InitialCreate body from `DateTime` → `DateTimeOffset` so the throwaway migration is honest (Npgsql maps both to `timestamptz`, so runtime-identical).
  - Done-when: [x] `dotnet ef migrations has-pending-model-changes` reports clean.

- [x] **B5-2 · Set-based revoke/invalidate** — Sev: Low · Effort: S · (CONF-12)
  - Files: `src/Infrastructure/Repositories/RefreshTokenRepository.cs`, `src/Infrastructure/Repositories/LoginTokenRepository.cs`, `src/Infrastructure/ServiceCollectionExtensions.cs`, `tests/Api.Tests/Infrastructure/ServiceHarness.cs`.
  - Fix: `RevokeAllForUserAsync` and `InvalidateActiveAsync` now use `ExecuteUpdateAsync` (standalone commit / enlists in an ambient transaction). Also injected `TimeProvider` into both repos so their active-token filters use the same clock as the services (resolves an ambient-`UtcNow` inconsistency noticed during B2; `TryAddSingleton(TimeProvider.System)` keeps Infrastructure self-contained).
  - **Watch-out hit:** the single `RevokeAsync` was deliberately LEFT as load-then-flip — converting it to `ExecuteUpdateAsync` left the just-rotated, already-tracked token stale in memory, which `GetByHashAsync` (no `IsRevoked` filter) read back, breaking refresh reuse detection. Bulk methods have no such tracked read-back, so they stay set-based. Comment added at the call site.
  - Done-when: [x] behavior unchanged, full suite green (incl. reuse-detection).

- [x] **B5-3 · SMTP send resilience** — Sev: Low · Effort: S · (CONF-13)
  - Files: `src/Infrastructure/Email/SmtpEmailSender.cs`, `src/Infrastructure/Email/SmtpSettings.cs`, `src/Core/Abstractions/IEmailSender.cs`, callers (`AuthController`, `TenantInvitationService`), `tests/.../ServiceHarness.cs` (NoopEmailSender).
  - Fix: `SmtpClient.Timeout` from new `SmtpSettings.TimeoutSeconds` (default 30); `CancellationToken` added to `IEmailSender.SendAsync` and threaded through Connect/Authenticate/Send/Disconnect; try/catch logs via `ILogger` and throws a typed `EmailSendException` (re-throwing `OperationCanceledException` untouched). All callers pass their token.
  - Done-when: [x] a failed send logs + surfaces `EmailSendException` instead of a raw stall; [x] callers pass the token.

- [x] **B5-4 · Clarify the UnitOfWork boundary** — Sev: Low · Effort: M · (MITI-2)
  - Files: `src/Core/Repositories/IUnitOfWork.cs`, `src/Core/Repositories/IRepository.cs`.
  - Fix: chose the **document** option (behavior-preserving; re-architecting to a single commit authority would touch every repo/service and risk regressions). `IUnitOfWork` now spells out the shared-context flush model: any `SaveChangesAsync` flushes ALL tracked changes; atomicity across writes is `BeginTransactionAsync`→`CommitAsync`; single self-contained writes may save directly. Cross-referenced from `IRepository.SaveChangesAsync`.
  - Done-when: [x] the transactional boundary is unambiguous (documented).

- [x] **B5-5 · Unique token-hash indexes** — Sev: Low · Effort: S · (CONF-14)
  - Files: `src/Infrastructure/Persistence/AppDbContext.cs` (+ migration `20260622154447_AddUniqueTokenHashIndexes`).
  - Fix: `.IsUnique()` on `RefreshToken.TokenHash` and `TenantInvitation.TokenHash`; migration generated cleanly (only the two index swaps — confirms no other drift). Verified no test seeds reuse a hash (all random per-row).
  - Done-when: [x] unique indexes in place; [x] migration generated + `has-pending-model-changes` clean; [x] tests green.

**Exit check (B5):** `dotnet ef migrations has-pending-model-changes` clean; `dotnet test tests/Api.Tests` green.

---

## Batch 6 — Test foundation
Report §4 (CONF-16, CONF-18, CONF-19, CONF-17). Do after B1.

- [x] **B6-1 · Populate `Core.Tests`** — Sev: Med · Effort: M · (CONF-16)
  - Files: `tests/Core.Tests/LoginTokenTests.cs` + `TenantInvitationTests.cs`, `src/Core/Entities/LoginToken.cs`, `TenantInvitation.cs`.
  - Fix: added deterministic `IsExpiredAt(now)`/`IsValidAt(now)` cores on both entities; the parameterless `IsExpired`/`IsValid` now delegate at ambient time (non-breaking — the `Ignore(...)` mappings stay valid; no production code used the props). Boundary tests assert at/before/after, covering LoginToken's inclusive expiry vs TenantInvitation's exclusive expiry, and the consumed/pending interplay.
  - Done-when: [x] `dotnet test tests/Core.Tests` runs real assertions for every derived rule (8 tests).

- [x] **B6-2 · Exercise migrations in a test** — Sev: Med · Effort: S · (CONF-18)
  - Files: new `tests/Api.Tests/MigrationsTests.cs`.
  - Fix: a self-contained test (own throwaway Postgres container) runs `Database.MigrateAsync()` — catching a broken migration — then asserts `GetPendingMigrationsAsync()` empty AND `HasPendingModelChanges()` false (model==snapshot). The shared fixture still uses `EnsureCreated`; this test is the drift guard the rest of the suite lacked.
  - Done-when: [x] migration/snapshot drift fails the suite.

- [x] **B6-3 · Enforce per-test isolation** — Sev: Low · Effort: S · (CONF-19)
  - Files: new `tests/Api.Tests/Infrastructure/PostgresTestBase.cs`; all 11 relational test classes.
  - Fix: added `PostgresTestBase : IAsyncLifetime` that calls `fixture.ResetAsync()` in `InitializeAsync` (runs before every test); every relational class now inherits it (primary-ctor `fixture` is both passed to the base and still used for `CreateContext`), and all ~36 scattered `await fixture.ResetAsync()` calls were deleted. Isolation is now structural.
  - Done-when: [x] tests pass with no per-test reset call; counts safe by construction (85 green).

- [x] **B6-4 · Fix `PLAYWRIGHT_BASE_URL` override** — Sev: Med · Effort: S · (CONF-17)
  - Files: `tests/E2E.Tests/E2ETestBase.cs`, `tests/E2E.Tests/playwright.runsettings`.
  - Fix: `BaseUrl` now resolves `TestContext.Parameters.Get("PLAYWRIGHT_BASE_URL")` (the runsettings `TestRunParameter`) first, then the env var, then the default. Reconciled the stale `5001` in runsettings to `https://localhost:7008` (the Web app's https launch profile, confirmed in `src/Web/Properties/launchSettings.json`).
  - Done-when: [x] the runsettings value actually changes the base URL; [x] port matches the Web app.

**Exit check (B6):** all four test projects green; drift + isolation are enforced, not by-convention.

---

## Batch 7 — UI / UX
Report §4 (CONF-15, MITI-4, MITI-5).

- [x] **B7-1 · Destructive confirm fails closed** — Sev: Med · Effort: S · (CONF-15)
  - Files: new `src/Shared.Ui/JsConfirm.cs`, `src/Shared.Ui/Pages/Household.razor`, `src/Shared.Ui/Pages/Settings.razor`, `Resources/AppStrings*.resx`.
  - Fix: extracted a shared `IJSRuntime.ConfirmAsync` extension that **fails closed** (`catch { return false; }`). Household's remove/leave/dissolve now call it (the old fail-open private `Confirm` is deleted); Settings' Unlink — previously unconfirmed — now goes through it too, with a new `Settings_ConfirmUnlink` string (EN + ES).
  - Done-when: [x] an interop-throw path returns false ⇒ does NOT execute remove/leave/dissolve/unlink.

- [x] **B7-2 · Don't swallow cancellation in AuthController** — Sev: Low · Effort: S · (MITI-4)
  - Files: `src/Api/Controllers/AuthController.cs` (Refresh, Logout).
  - Fix: both catch blocks now use `when (ex is not OperationCanceledException)` so a client-disconnect cancellation propagates (request aborted) instead of being masked as a logged 500. Chose the targeted guard over global `UseExceptionHandler` to preserve the endpoints' specific error envelopes (`refresh_failed`/`logout_failed`).
  - Done-when: [x] cancellation propagates; unexpected errors still get the per-endpoint 500 envelope.

- [x] **B7-3 · Remove the double full-page reload on locale mismatch** — Sev: Low · Effort: M · (MITI-5)
  - Files: `src/Shared.Ui/Layout/MainLayout.razor`.
  - Fix: on first authenticated load with a non-device locale, apply the culture IN-PROCESS (`CultureInfo.CurrentCulture/UICulture` + persist to localStorage) before `_initialized` flips, so `@Body` renders in the new culture with no `forceLoad`. Unknown locales are caught and ignored. `Web/Program.cs` still reads `app_culture` at cold start (now complementary, not reload-driven).
  - Done-when: [x] first authenticated visit with a non-device locale does not hard-reload.

**Exit check (B7):** no fail-open destructive actions; no needless reload; cancellation respected.

---

## Batch 8 — Docs reconcile
Report §4 (CONF-20, CONF-21, CONF-22) + carry-over doc nits from the prior backlog.

- [x] **B8-1 · Remove the `SCHEMA.sql` reference** — Low · S · (CONF-20) — `CLAUDE.md` doc-map row now points at `src/Infrastructure/Persistence/Migrations/`.
- [x] **B8-2 · Fix `magic_link` → `magic-link` in docs** — Low · S · (CONF-21) — `docs/DATA_MODEL.md` + `docs/FEATURES.md` now say `magic-link`, matching `LoginToken.cs`. (No other `magic_link` underscore hits remain outside the audit docs.)
- [x] **B8-3 · Reconcile email `FromName`** — Low · S · (CONF-22) — `appsettings.json` default is now `"Perezosoft"` (consistent with `.env.example`/REBRANDING); `REBRANDING.md` now names `appsettings*.json` as the committed runtime default with `.env`/env vars as the override.
- [x] **B8-4 · Carry-over doc nits** — Low · S — README `saas-template/` → "this template tree"; ADR-C9 reworded (MAUI shells scaffolded with auth wired, feature parity deferred web-first); ADR-C13 "all ports" scoped to **Compose service** ports (app ports fixed in launch profiles); ADR-C11 doc list expanded; ADR-001 made the `Section__Sub` ≡ `Section:Sub` equivalence explicit. (ADR-C15 left as-is — REF-13 confirmed it's intentionally preserved historical, already carrying a dated supersede note.)
  - Done-when (B8): [x] no doc references a nonexistent artifact; [x] doc strings match code values.

**Exit check (B8):** grep for `SCHEMA.sql`, `magic_link`, `Perezosoft`, `saas-template` returns only intended hits.

---

## Batch 9 — Enforcement & "Definition of Solid" · do last
Turns the fixes into invariants CI defends, so audits stop being necessary. Report §6.

- [x] **B9-1 · Architecture tests** — Effort: M
  - Files: `tests/Api.Tests/ArchitectureTests.cs`.
  - Extended `Api.Tests` with source/model-scan tests: no `IgnoreQueryFilters` under `src/Api/Features/**`; every `ITenantScoped` entity has a global query filter (via `GetDeclaredQueryFilters()` on the built model); the web app carries no inline Blazor components (only a bootstrap allowlist). No extra dependency.
  - Done-when: [x] a violation fails `dotnet test` (3 tests, currently green).

- [x] **B9-2 · Analyzers at warning-as-error** — Effort: S
  - Files: new root `Directory.Build.props`.
  - Enabled `Nullable` + `TreatWarningsAsErrors` solution-wide. Full build (incl. MAUI) stays at 0 warnings, so it's clean today and fails the build on any new warning. (Skipped the optional Banned-API analyzer — the ambient-`DateTime.UtcNow` cases the audit cared about are already removed.)
  - Done-when: [x] `dotnet build` treats warnings as errors across projects.

- [x] **B9-3 · CI gate** — Effort: M
  - Files: new `.github/workflows/ci.yml`.
  - Workflow on push/PR to `main`/`develop`: build (warnings-as-errors) for the Linux-buildable graph (API, Web/WASM → covers Shared.Ui, E2E compile-check), `dotnet test` Core + Api (which run the arch tests B9-1 and migrate-drift guard B6-2 against Testcontainers Postgres), then `ef migrations has-pending-model-changes`. MAUI (`net10.0-windows`) is excluded — it can't build on Linux and is deferred.
  - Done-when: [x] the gate is defined; enable branch protection requiring the `build-test` check to block PRs.

- [x] **B9-4 · Freeze the "Definition of Solid" checklist** — Effort: S
  - Files: new `CONTRIBUTING.md`.
  - Copied Report §6 into a committed checklist (all 5 criteria checked) plus an "how the bar is enforced" table mapping each guard to its file. This is the frozen spec.
  - Done-when: [x] the 5 criteria are committed and all checked.

**Exit check (B9 = project exit):** All three Highs closed and **gated**; arch + drift + auth-fail-closed
covered by tests in CI. When this batch is green, the template is *solid by construction* — stop
running discovery audits.

---

## Cross-reference: finding ID → task
CONF-1 B1-1 · CONF-2 B1-2 · MITI-1 B1-3 · CONF-5 B2-1/B2-2 · CONF-6 B2-3 · MITI-3 B3-1 · CONF-4 B3-2 ·
CONF-8 B3-3 · CONF-7 B3-4 · CONF-9 B4-1 · CONF-3 B4-2 · CONF-10 B4-3 · CONF-11 B5-1 · CONF-12 B5-2 ·
CONF-13 B5-3 · MITI-2 B5-4 · CONF-14 B5-5 · CONF-16 B6-1 · CONF-18 B6-2 · CONF-19 B6-3 · CONF-17 B6-4 ·
CONF-15 B7-1 · MITI-4 B7-2 · MITI-5 B7-3 · CONF-20 B8-1 · CONF-21 B8-2 · CONF-22 B8-3.

Refuted/immaterial items are listed in Report §5 — do not re-file them.
