# Audit Report — Template (deep adversarial sweep)

> **Date:** 2026-06-21 · **Branch:** `develop` · **Scope:** whole repo (code + docs).
> This report is the *why* behind [`AUDIT_TASKS.md`](AUDIT_TASKS.md) (the *what to do*). Findings
> carry stable IDs (`CONF-n`, `MITI-n`) referenced by the task backlog. Each finding records the
> evidence so a future session can act on it without re-deriving anything.
>
> **Status when written:** nothing here is fixed yet. Read [`AUDIT_TASKS.md`](AUDIT_TASKS.md) for the
> remediation plan, sequenced into one-session batches.
>
> **✅ RESOLVED (2026-06-22).** All 9 batches in [`AUDIT_TASKS.md`](AUDIT_TASKS.md) are complete on
> `develop` (commits `b01a422`, `d440ba5`, `dda7e2b`, `6b61bbc`, `9c60998`, `a335d50`, `d0cbaf0`,
> `ca3e103`, + the B9 enforcement commit). All three Highs are closed and **gated in CI**; the
> "Definition of Solid" (§6) is frozen in `CONTRIBUTING.md`. Per §6: **do not run a 4th discovery
> audit — keep the arch/drift/auth tests green instead.**

---

## 1. How this audit was produced

This is the third audit pass. The first two were single-reviewer sweeps (one found Critical→Low and
was remediated; the second found mostly Medium/Low). This third pass was a **multi-agent
adversarial sweep** run to settle the question "how many more audits until this is solid?":

1. **Survey** — 7 independent finder agents, one per dimension (architecture, auth-security,
   api-quality, core-infra-ef, ui-blazor, tests, docs), each reading the actual files and reporting
   evidence-backed findings. → 40 raw findings.
2. **Verify (adversarial)** — every finding got **two** independent agents:
   - a **refuter** instructed to disprove it against the real code (default stance: skeptical), and
   - a **materiality judge** deciding whether it's worth fixing in a *template* or is reviewer noise.
3. **Bucket** — a finding survives as **confirmed** only if both lenses agree it's real and material.

**Result:** 22 confirmed · 5 real-but-intentional/mitigated · 0 contested · 13 refuted.
Zero contested (the two lenses never disagreed on a survivor) ⇒ a high-confidence set.

### Why this is the *final* audit worth running
The adversarial pass is what a 4th, 5th… single-reviewer pass cannot be: it **filters its own
noise** and **corrects prior findings**. It overturned a nit from audit #1 (see §5, REF-6) and
killed several confidently-wrong finder claims. What remains is a deduped, severity-ranked,
evidence-checked set. A further pass would only surface Low-grade opinion. The finish line is not
"an empty report" (unreachable on any codebase) — it is **§6: the 3 Highs fixed + enforcement in
CI**. After that, "solid" is provable, not asserted.

---

## 2. Severity summary (post-adjustment)

Severities below are the **materiality judge's adjusted** values, which sometimes differ from the
finder's claim (noted per finding).

| Severity | Count | IDs |
|----------|-------|-----|
| **High** | 3 | CONF-1, CONF-5, MITI-3 |
| **Medium** | 7 | CONF-4, MITI-1, CONF-8, CONF-15, CONF-16, CONF-17, CONF-18 |
| **Low** | 17 | CONF-2, CONF-3, CONF-6, CONF-7, CONF-9, CONF-10, CONF-11, CONF-12, CONF-13, CONF-14, CONF-19, CONF-20, CONF-21, CONF-22, MITI-2, MITI-4, MITI-5 |

The three Highs are the keystone; everything else is polish that does not, on its own, make the
template "unsound."

---

## 3. The three High findings (read these in full)

### CONF-1 — Tenant isolation is read-only; nothing stamps/validates `TenantId` on writes  · **High**
**Files:** `src/Infrastructure/Persistence/AppDbContext.cs:146`, `src/Infrastructure/Repositories/EfRepository.cs:22`, `src/Api/Features/Notes/NotesHandler.cs:30`, `src/Core/Entities/Note.cs:16`, `src/Core/Entities/ITenantScoped.cs:17`

**Evidence:**
- The global filter is read-only: `ApplyTenantFilter<TEntity>(...) => builder.Entity<TEntity>().HasQueryFilter(e => e.TenantId == CurrentTenantId)` (`AppDbContext.cs:146-147`). `HasQueryFilter` affects **reads only**.
- No write-side stamping anywhere: a repo-wide search for `SaveChangesInterceptor|ISaveChangesInterceptor|override SaveChanges|SavingChanges|AddInterceptors` returns nothing. `EfRepository.SaveChangesAsync` just delegates to `db.SaveChangesAsync`.
- `TenantId` is freely settable: `Note.cs:16` declares `public Guid TenantId { get; set; }` (the interface only exposes a getter, but the concrete class adds a setter).
- The only thing producing a correct `TenantId` on insert is hand-written convention in the reference handler: `NotesHandler.CreateAsync` (`:30` guard, `:37` assignment).

**Why it matters:** ADR-003 markets tenant scoping as a "structural guarantee, so feature slices
can't forget to scope." That guarantee holds for reads but **not writes**. A feature author who
omits the stamp — or binds `TenantId` from request input — persists a row under the wrong tenant,
and because the read filter then hides that row from its true owner, the bug is **invisible in
ordinary testing**. A mis-stamped insert is worse than a leaked read: it's a latent, silent
data-integrity hazard every copied feature inherits.

**Fix:** add an `ISaveChangesInterceptor` (or override `SaveChangesAsync` in `AppDbContext`) that,
for every `Added` entry implementing `ITenantScoped`, sets `TenantId = CurrentTenantId` when default
and **throws if a non-default `TenantId != CurrentTenantId`** (fail closed). Keep explicit setting in
the contributor/dissolve paths that legitimately operate cross-tenant. → **Task B1-1**.

---

### CONF-5 — OTP is brute-forceable: no rate limiting + attempt counter resets on every resend · **High**
**Files:** `src/Api/Services/PasswordlessService.cs:69` (`IssueOtpAsync`), `:100` (attempt cap), `src/Api/Controllers/AuthController.cs:353` (`/otp/send`), `src/Api/Configuration/SettingsProvider.cs:63`

**Evidence:**
- OTP is 6 numeric digits (`OtpLength` default 6) with `OtpMaxAttempts` default 5 per code.
- `IssueOtpAsync` calls `InvalidateActiveAsync` (consuming the prior code) then issues a brand-new code with `AttemptCount = 0`. `RedeemOtpAsync` always reads the **latest** active code, so resending resets the guess budget.
- `/otp/send` has only an email-format check — no throttle. No rate limiting anywhere: a search for `RateLimiter|AddRateLimiter|RequireRateLimiting` across `src/` returns nothing; the `Program.cs` pipeline is `UseSession → UseAuthentication → UseAuthorization`.

**Why it matters:** the per-code attempt cap is the *only* brute-force defense and it's trivially
reset by requesting a new code. With no send/verify rate limiting, `send → verify×5 → repeat` is
unbounded against a 10⁶ keyspace. (Nuance: each resend mints a *new* random code, so the attacker
can't accumulate progress against a fixed secret — ~5 guesses/round at ~1/200k each — but unbounded
rounds still make it an account-takeover surface.) Separately, `/otp/send` and `/magic-link/send`
are an unthrottled **email-bomb / outbound-cost amplifier**. For a template whose headline feature
is a custom passwordless auth stack, this is the most important security gap.

**Fix:** per-email **and** per-IP rate limiting on `/otp/send`, `/magic-link/send`, and `/otp/verify`
(ASP.NET Core rate limiter or a counter in the `LoginToken` store: max N issues per window). Track
cumulative failed attempts per email/window **across** codes so resending cannot reset the lockout.
Consider increasing OTP entropy. → **Tasks B2-1, B2-2**.

---

### MITI-3 — `email_verified` fails **open** for any provider that omits the claim · **High**
**Files:** `src/Api/Services/ClaimsExtractor.cs:37-38`, `src/Api/Services/UserService.cs:90`, `src/Infrastructure/ServiceCollectionExtensions.cs:94`

**Evidence:** `IsEmailVerified` returns `!string.Equals(claim, "false", ...)` (`ClaimsExtractor.cs:38`),
so an **absent** `email_verified` claim ⇒ `true` (verified). This contradicts the documented
fail-closed intent in `UserService.GetOrCreateUserAsync` ("emailVerified defaults to false … must
NOT silently bypass the takeover guard"). Google asserts the claim; **Microsoft does not always**,
and the template advertises "new OAuth provider = one line" — so a provider that omits it would
silently auto-merge accounts via the takeover path.

**Why it matters:** the takeover guard (`UserService.cs:90`) is the exact security control at stake,
and the extractor undoes its fail-closed default. (Bucketed "mitigated/intentional" by the workflow
only because Google — the primary provider — does send the claim; the materiality judge still rated
it **High**.)

**Fix:** return `true` **only** when the claim is explicitly present and equals `"true"`
(case-insensitive); absent/unknown ⇒ `false`. If Microsoft must stay trusted, gate trust
per-provider via an allow-list from the callback route. Reconcile the contradicting XML comments and
add a unit test proving an absent claim does not bypass the merge guard. → **Task B3-1**.

---

## 4. Medium & Low findings (catalog)

Each entry: ID · severity (claim→adjusted) · one-line description · files · fix pointer → task.

### Architecture
- **MITI-1** · High→Med · `Query()` exposes raw `IQueryable`, so any feature can chain `IgnoreQueryFilters()` and read every tenant's rows — the "can't forget to scope" promise is bypassable. `src/Core/Repositories/IRepository.cs:19`. Fix: keep `Query()` un-unscopable; add a named `QueryAllTenants()` for the rare cross-tenant path (contributors). → **B1-3**.
- **CONF-2** · Med→Low · `WipeDataAsync` deletes `TenantInvitation` (ITenantScoped, double-filtered by the global filter) and `TenantMembership` (not scoped) differently; correct only because the sole caller's `CurrentTenantId == tenantId`. A future off-tenant caller silently orphans invitations. `src/Infrastructure/Repositories/TenantRepository.cs:108-109`. Fix: `IgnoreQueryFilters().Where(... == tenantId)` (ideally `ExecuteDeleteAsync`) uniformly. → **B1-2**.
- **CONF-3** · Low · Two HTTP styles (controllers vs minimal-API) with no shared feature scaffolding; the reference slice re-implements auth inline (`new Microsoft.AspNetCore.Authorization.AuthorizeAttribute{...}`), and a forgotten `.RequireAuthorization` on a minimal-API group **fails open** (no inherited attribute, `AddAuthorization()` has no fallback policy). `src/Api/Features/Notes/NotesEndpoints.cs:14`, `src/Api/Controllers/TenantApiControllerBase.cs:17`, `src/Api/Program.cs:122`. Fix: a `MapTenantFeatureGroup(prefix)` extension + a single named authz policy shared by controllers and features; consider a fallback policy. → **B4-2**.
- **MITI-2** · Low · `IRepository<T>.SaveChangesAsync` and `IUnitOfWork` both commit the same shared `DbContext`; per-method `db.SaveChangesAsync()` in hand-written repos muddies the transaction boundary. `src/Core/Repositories/IUnitOfWork.cs`, `EfRepository.cs:22`. Fix: make `IUnitOfWork` the sole commit authority (or document the flush semantics explicitly). → **B5-4**.

### Auth / security
- **CONF-4** · High→Med · Refresh-token rotation has no reuse/theft detection: a replayed rotated token returns null (filtered by `!IsRevoked`) → silent 401, no family revoke. `RevokeAllUserTokensAsync` is only wired to logout. `src/Api/Services/RefreshTokenService.cs:59`, `src/Infrastructure/Repositories/RefreshTokenRepository.cs:21`, `src/Api/Controllers/AuthController.cs:149-158`. Fix: token family/lineage; on a revoked-but-presented hash, revoke the whole family + audit-log; distinguish "reused" from "unknown". → **B3-2**.
- **CONF-6** · Low · OTP verify returns `code_expired` (no active code) vs `invalid_code` (wrong code), letting an attacker probe whether an address has an **outstanding** OTP — partially defeats the deliberately enumeration-safe `/otp/send`. `src/Api/Services/PasswordlessService.cs:85-87`, `src/Api/Controllers/AuthController.cs:380`. Fix: collapse both to one client-facing error; keep the distinction server-side only. → **B2-3**.
- **CONF-7** · Low · `TokenValidationParameters` defined twice (Program.cs bearer handler + `JwtTokenService.ValidateToken`, which is test-only); can silently diverge. `src/Api/Program.cs:109`, `src/Api/Services/JwtTokenService.cs:78`. Fix: one shared factory consumed by both, or delete the service copy and point tests at the real pipeline. → **B3-4**.
- **CONF-8** · Med · `JwtTokenService` stamps token expiry with ambient `DateTime.UtcNow` instead of the injected `TimeProvider` every sibling service uses — access-token lifetime untestable, breaks the DI/testability convention. `src/Api/Services/JwtTokenService.cs:61`. Fix: inject `TimeProvider`; `clock.GetUtcNow().UtcDateTime.AddMinutes(...)`. → **B3-3**.

### Core / Infrastructure / EF
- **CONF-9** · Med→Low · Notes reference slice (the copy-me exemplar) uses ambient `DateTimeOffset.UtcNow`; propagates the anti-pattern to every feature. `src/Api/Features/Notes/NotesHandler.cs:33`. Fix: inject `TimeProvider`. → **B4-1**.
- **CONF-10** · Med→Low · `LinkTokenService` and `NativeAuthCodeService` are near-duplicate single-use-cache-token implementations. `src/Api/Services/LinkTokenService.cs:19-45`, `NativeAuthCodeService.cs:24-50`. Fix: extract a generic `SingleUseCacheToken<T>` both delegate to. → **B4-3**.
- **CONF-11** · Med→Low · `InitialCreate` migration declares `RefreshToken.IssuedAt/ExpiresAt` as `DateTime`; model + snapshot use `DateTimeOffset` (same Npgsql column, so no runtime drift — cosmetic). `…Migrations/20260617235537_InitialCreate.cs:54-55`. Fix: regenerate the throwaway initial migration (or edit the two columns); verify `dotnet ef migrations has-pending-model-changes` is clean. → **B5-1**.
- **CONF-12** · Med→Low · `RevokeAllForUserAsync` / `InvalidateActiveAsync` load rows to flip a flag where `ExecuteUpdateAsync` fits (the house style elsewhere). `RefreshTokenRepository.cs:39`, `LoginTokenRepository.cs:39`. Fix: set-based `ExecuteUpdateAsync` (confirm standalone-commit semantics). → **B5-2**.
- **CONF-13** · Low · `SmtpEmailSender` has no per-send timeout, no `CancellationToken`, no error handling; a hung SMTP server stalls the request and surfaces raw exceptions. `src/Infrastructure/Email/SmtpEmailSender.cs:13,33`. Fix: `client.Timeout`, thread a token through `IEmailSender.SendAsync`, try/catch + log. → **B5-3**.
- **CONF-14** · Low · `RefreshToken.TokenHash` and `TenantInvitation.TokenHash` use **non-unique** indexes for single-row credential lookups. `src/Infrastructure/Persistence/AppDbContext.cs:103,68`. Fix: make them unique (`.IsUnique()`) + migration — cheap insurance and self-documenting. *(Reverses audit #1's "leave it as deliberate"; the deep pass judged unique the better default.)* → **B5-5**.

### Tests
- **CONF-16** · High→Med · `Core.Tests` is an empty shell (only a `.csproj`) despite three docs naming it the home for derived-rule/entity-invariant tests; the computed properties (`LoginToken.IsExpired/IsValid`, `TenantInvitation.IsValid`) have no unit tests. `tests/Core.Tests/Template.Core.Tests.csproj`. Fix: add boundary tests; to make them deterministic, refactor the derived props to accept an injected `now` (or `IClock`). → **B6-1**.
- **CONF-17** · Med · `PLAYWRIGHT_BASE_URL` is read from the environment, but `playwright.runsettings` sets it as an NUnit `TestRunParameter` — the override silently no-ops; the default port also disagrees. `tests/E2E.Tests/E2ETestBase.cs:16`, `playwright.runsettings:14`. Fix: read `TestContext.Parameters.Get("PLAYWRIGHT_BASE_URL")` first, then env, then default; reconcile the port. → **B6-4**.
- **CONF-18** · Med · Migrations are never exercised — the test schema is built with `EnsureCreated`, so migration/snapshot drift (e.g. CONF-11) is invisible. `tests/Api.Tests/Infrastructure/PostgresFixture.cs:35`. Fix: one test that runs `Database.MigrateAsync()` + asserts `GetPendingMigrations()` empty. → **B6-2**.
- **CONF-19** · Med→Low · Shared Postgres fixture relies on each test remembering `ResetAsync()`; absolute-count assertions are order-fragile. `PostgresFixture.cs:57`, `TenantInvariantTests.cs:74,175`. Fix: `IAsyncLifetime.InitializeAsync` (or a base class) resets automatically; delete scattered manual calls. → **B6-3**.

### UI / Blazor
- **CONF-15** · High→Med · Destructive confirm dialog **fails open**: `Confirm()` returns `true` on JS-interop exception, so remove-member / leave / dissolve proceed without confirmation. `src/Shared.Ui/Pages/Household.razor:388-392` (callers `:313,:349,:366`). Fix: `catch { return false; }`; route `Settings.razor` Unlink (currently no confirmation) through the same guarded helper. → **B7-1**.
- **MITI-4** · Low · `AuthController.Refresh`/`Logout` wrap the whole body in `catch(Exception)` → generic 500, swallowing `OperationCanceledException` too. `AuthController.cs:170-174,206-210`. Fix: `UseExceptionHandler` middleware, or at least `when (ex is not OperationCanceledException)`. → **B7-2**.
- **MITI-5** · Low · Guaranteed double full-page reload on first authenticated visit when saved locale ≠ device culture. `src/Shared.Ui/Layout/MainLayout.razor:46-52`, `src/Web/Program.cs:56-59`. Fix: apply culture in-process + persist, re-render without `forceLoad` (or apply once at startup from the token). → **B7-3**.

### Docs
- **CONF-20** · Low · `CLAUDE.md:99` doc-map references `SCHEMA.sql`, which does not exist. Fix: point at the EF migrations folder instead. → **B8-1**.
- **CONF-21** · Med→Low · Docs say `LoginToken.purpose` stores `magic_link`; code stores `magic-link`. `docs/DATA_MODEL.md:56`, `docs/FEATURES.md:52`, `src/Core/Entities/LoginToken.cs:40`. Fix: change docs to `magic-link` (code is internally consistent). → **B8-2**.
- **CONF-22** · Med→Low · Default email `FromName` is `"App"` (`appsettings.json:53`) but `REBRANDING.md:24` and `.env.example:33` say `Perezosoft`; three sources disagree. Fix: make `FromName` brand-consistent and have REBRANDING list `appsettings*.json` as the runtime-default source. → **B8-3**.

---

## 5. Refuted & corrected (do **not** re-file these)

The adversarial pass rejected 13 raw findings. Recorded here so future audits don't re-raise them.

**Genuinely false premises (the finder was wrong):**
- **REF-2** "ValidateToken is dead code" — it *is* called (by tests). The real issue is the duplicated validation params, captured as **CONF-7**.
- **REF-6** "`SmtpSettings.Port` default 1025 risks prod" — **false**: `appsettings.json` hard-codes `587`, which always loads, so the POCO default never applies. *(This overturns a nit from audit #1.)*
- **REF-9** "pages run `OnInitializedAsync` while unauthenticated" — **false**: `MainLayout` renders only a spinner until `_initialized`, so no premature API calls.
- **REF-11** "`AUDIT_TASKS.md` ships in the template" — **false**: it's git-untracked, not part of the template tree.
- **REF-13** "ADR-C15 contradicts the stack" — that's correct ADR practice (historical decision preserved + dated supersede note), not a defect.

**Real but judged immaterial / already covered (below the line for a template):**
- REF-1 (controllers read via repository — deliberate `TenantApiControllerBase` design), REF-3 (`JwtSettings` built twice at startup — harmless), REF-4 (email-format check duplicated in 2 spots), REF-5 (missing `AsNoTracking` on some reads), REF-7 (JWT re-parsed per render — WASM, small), REF-8 (role literals in Razor — overlaps the contracts concern), REF-10 (expiry tests backdate via SQL instead of `FakeTimeProvider`), REF-12 (`saas-template/` naming in README — trivial doc nit).

If you want maximal polish, REF-5/REF-7/REF-8/REF-10 are legitimate Low cleanups — they were dropped only because the materiality bar for a template is higher than for an app.

---

## 6. Definition of "Solid" (the finish line)

The template is **solid** — and further audits stop returning anything but opinion — when:

1. **No open High.** CONF-1, CONF-5, MITI-3 fixed.
2. **Tenant safety is structural in both directions.** Write-stamping interceptor (CONF-1) **plus** an
   architecture test that fails the build if `IgnoreQueryFilters` appears in `src/Api/Features/**`
   (MITI-1) **plus** the live two-tenant isolation tests.
3. **Passwordless auth is rate-limited** (CONF-5) and the takeover guard fails closed (MITI-3), each
   with a test.
4. **The reference slice (Notes) is exemplary** — correct clock, correct scaffolding — because every
   feature copies it (CONF-9, CONF-3).
5. **Docs don't contradict code** (CONF-20/21/22) and the invariants are **enforced in CI**
   (analyzers at warning-as-error + the arch tests + the migrate-drift test CONF-18).

When 1–5 hold, "solid" is provable by the test suite and CI gates, not by a reviewer's opinion.
**That is the stopping point — do not run a 4th discovery audit; run the enforcement (Batch 9) instead.**

---

## 7. Provenance
- Sweep: 87 agents, 7 finder dimensions, 2 adversarial lenses/finding, 0 contested survivors.
- Full machine output retained at run time under the session's task output; this report is the
  human-curated distillation. Finding IDs (`CONF-n`/`MITI-n`) are stable and referenced by
  [`AUDIT_TASKS.md`](AUDIT_TASKS.md).
