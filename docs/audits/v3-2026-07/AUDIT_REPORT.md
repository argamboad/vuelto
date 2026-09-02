# v3 Delta Audit — Phase 1: Comprehensive Static Audit

> **Status: COMPLETE.** Diagnose-only. Companion outputs: `FOUNDATION_RULES.md` (candidate R36–R76),
> `RULE_CONFLICTS.md`. Phases 2–5 not yet run.

## SUMMARY

- **Commit SHA (pinned):** `5fc1762dc5487de26af0e515c34c264efaaa11a7` (branch `audit/v3-2026-07-phase1`, cut from `develop` 2026-07-14). Every phase of this suite must audit this SHA and stop if it differs.
- **Findings by severity:** 0 Critical · **1 High** (RLS-1) · 19 Medium · 35 Low · 2 Info. Plus **7 gate-coverage gaps** and **1 partial v1/v2 doc item** from Step 0.
- **v2 regressions found:** **0.** 46 of 47 v2 remediations Held; 1 Partial (a never-created doc sub-item, not a regression). Every executed standing gate is green on this commit.
- **Rules added:** candidate rules R36–R66 proposed across the seven audit areas (seeded into `FOUNDATION_RULES.md` as the v3 candidate block; consolidated/numbered at Phase 5). None of R1–R35 flagged for revision.
- **Conflicts logged:** see `RULE_CONFLICTS.md` (candidate-rule overlaps that Phase 5 must merge; no rule *reversals*).

**One-line verdict:** The post-v2 platform is fundamentally solid — no cross-tenant breach, no privilege-escalation-to-staff, no regression of a v2 fix. The single High is a **broken enforcement guarantee** (the RLS migration-parity gate cannot actually fail), and the Mediums cluster into a few recurring themes: enforcement scans that don't cover the newer code surfaces, second-factor/brute-force hardening gaps on the auth delta, web-host hardening the API never grew when it became an HTML host, and a handful of "documented-but-not-enforced" invariants.

## Scope and method

This is **Phase 1 of the v3 delta audit** — the same five-phase suite as v2 (`docs/audits/v2-2026-07/`), re-scoped to everything that landed on `develop` **after the v2 pass closed** (`852c27f`, 2026-07-02). v2 outputs are settled: `FOUNDATION_RULES.md` v1.0 (R1–R35) is the binding bar, and items adjudicated in v2's `AUDIT_REPORT.md` / `RULE_CONFLICTS.md` are not re-filed. v1 (2026-06) remains archived at `docs/audits/v1-2026-06/`.

Step 0 verified the v2 remediations (B1–B11) and their standing gates still hold. Fresh discovery then covered the post-v2 delta across seven parallel diagnose-only auditors: **S0** (v2 verification), **RLS** (ADR-020 backstop + BILLING-9 tenancy), **DEP** (DEPLOY epic + CI), **NAT** (NATIVE epic), **ADM** (admin/auth surface, ADR-021), **UX** (THEME/PREFS/NOTIFY/BILLING feature logic), **TR** (template-readiness + docs/Postman sync).

Finding IDs: `RLS-n`, `DEP-n`, `NAT-n`, `ADM-n`, `UX-n`, `TR-n`; Step-0 gate gaps as `S0-Gn`. Candidate rules `CAND-<area>-n`, consolidated into `FOUNDATION_RULES.md` (continuing from R35). This phase is **report-only** — no production code was modified. Full per-auditor detail is retained; this document is the consolidated register Phase 5 reconciles from.

## Delta inventory (852c27f..develop, 81 first-parent commits)

| Area | PRs / commits |
|------|----------------|
| DEPLOY epic (ADR-017) | #81–#90 (single-origin, container, render.yaml, pipeline, version smoke), #86/#87 email fixes, #88 OAuth provider discovery |
| E2E epic | #91–#96 (roster, seat-quota, notifications, magic-link, membership lifecycle), #101 magic-link base-URL fix |
| BILLING | #98 (BILLING-8 billing page), #141 (BILLING-9 seat re-check at accept) |
| ADMIN | #97 (ADMIN-3 announcements), #133 (ADR-021 enumerated writes incl. comp/revert `c6c9617`), #138 (admin MFA reset) |
| NATIVE epic (ADR-018) | #100–#117 (CI build gate, parity audit, G1–G6 closures, Apple boot fix, Windows/Android/Apple smokes); direct: edge-to-edge padding `92d3595`, Custom Tab queries `a3bad29`, adb reverse `cca9c1d`, MAUI lockfile gitignore `2b068a0` |
| Rename + brand (ADR-019) | #119–#122 (Template→Perezosoft), sloth mark kit `b972f31`/`2909a49` |
| RLS backstop (ADR-020) | #123 (docs), #124 (implementation `fa10126`) |
| Auth/QA-pass fixes | #130/#131 (OTP lockout), #133 (rate-limit split, typeable MFA recovery codes, NOTIFY delete/clear `fcfd8ac`), PR #125 (SMTP revocation knob + Catalyst Debug session store) |
| THEME / PREFS | #137 (THEME-1), #139 (theme E2E PUT race), #140 (PREFS-1, ADR-022) |
| OBS | `7319c33` (null RequestServices tolerance) |
| CI toolchain | #142–#144 (toolchain drift, Apple workload pin, merge-discipline docs) |
| Docs-only | #99, #104, #110, #118, #132, #134–#136, tutorial course, OVERVIEW/NEW_APP_GUIDE `e340467`, QA PDF fixes |

> **Correction:** the task brief referenced "ADR-017 through ADR-023." **ADR-023 does not exist at this commit** — `docs/DECISIONS.md` ends at ADR-022 and `grep -rn ADR-023` returns nothing repo-wide. The "distribution is downstream" decision lives in the **ADR-018 amendment (2026-07-06)** + ADR-017 point 5, and in `NEW_APP_GUIDE.md` Phases 8–9. (The session memory index cites ADR-023; memory is ahead of this branch.)

---

## STEP 0 — v2 remediation status

**47 v2 items checked (B1–B11): 46 Held · 0 Regressed · 0 Superseded · 1 Partial.** Docker was available, so the Testcontainers-backed gates (incl. the RLS parity gate, `TenantStampingInterceptorTests`, `QuotaServiceTests`, MFA/billing pins) were **executed** — 89 tests green — not merely assumed. The remaining CI jobs (e2e, gitleaks, license-scan, qa-artifacts, docker-build, migrate-drift) were confirmed present and unweakened in `ci.yml` but not executed here.

**No regressions.** Every v2 invariant with a standing gate (tenant write-stamping interceptor, `IgnoreQueryFilters`/`QueryAllTenants` Features-ban, warnings-as-error, migrate-drift, config-catalog, MailKit-boundary, entity/DataModel doc-sync, account-erasure canary, atomic-quota, injected-clock scan, unique export keys, QA-PDF drift) exists today and passes. The new NATIVE/deploy CI jobs are additive; the deploy gates still require all six original build jobs. The RLS backstop (ADR-020) is an **additive strengthening** of R2/R5/R32, not a replacement.

**The one Partial:** B10-4 (DOC-22) — the `docs/stories/ui.md` retrospective was never created (only the ROADMAP note half landed). It never existed, including in the "done" commit `41cf3eb`, so this is an unfinished doc item, not rot. Low.

### Step-0 gate-coverage gaps (gates that pass but no longer cover code that landed since v2)

| ID | Gap | Why it matters |
|----|-----|----------------|
| **S0-G1** | `RouteGroupPrefixes_AreUnique` (R35) scans `Controllers/` `[Route]` + `Features/` `MapGroup` only — **misses `src/Api/Endpoints/`** where PUBAPI/HOOKS `MapGroup("/api/webhooks" | "/api/apikeys" | "/api/public")` now live (B9-6 move). A new group claiming one of those prefixes wouldn't be caught. No live collision today. |
| **S0-G2** | The tenant-hatch ban (`IgnoreQueryFilters`/`QueryAllTenants`/`RlsTags`, R5) scans `Features/**` only — **not `src/Api/Endpoints/`**, which is request-path tenant-scoped code just like a slice. No offenders today; nothing prevents one. |
| **S0-G3** | **R21 has no dedicated standing gate.** Config-gated `*Settings` posture (PublicApi/Webhooks/Admin/Hosting/S3) holds only by `= false` defaults; a future `Enabled { get; set; } = true` would pass CI. |
| **S0-G4** | **R3 machine half never landed.** The `IOutboundUrlGuard` seam exists and both callers use it, but no scan asserts new outbound-HTTP-to-user-URL code must. |
| **S0-G5** | R31 QA run-log append-only is not machine-checked (only plan⇄PDF drift is). Documented as accepted at B11-8. |
| **S0-G6** | R15 injected-clock scan covers `src/Api`+`src/Infrastructure` only; `src/Maui`/`src/Shared.Ui` have `DateTime.UtcNow` — consistent with the client allowlist, noted not filed. |
| **S0-G7** | R23 story-✅⇄ROADMAP/doc-map sync remains review-only; the review surface grew by 6+ epics and **is already drifting** (see TR-2). |

---

## Findings register (fresh discovery)

Severity legend: **High** re-broken/near-breach guarantee · **Medium** real defect, bounded blast radius · **Low** hardening/hygiene/doc drift · **Info** noted, no action required.

### High

| ID | File:line | Finding | Fix | Conf |
|----|-----------|---------|-----|------|
| **RLS-1** | `tests/Api.Tests/Infrastructure/IntegrationTestFactory.cs:66-67` + `Rls/RlsTestSetup.cs:43` + `Rls/RlsMigrationGateTests.cs:26` | **The RLS migration-parity gate is tautological — it can never fail for the drift it exists to catch.** The factory runs real migrations, then `ProvisionAsync` applies `RlsDdl.StatementsFor(db.Model)` (model-derived policies for *every* `ITenantScoped` table) to the **same** container the gate inspects. A new tenant table shipped *without* an RLS policy migration gets the policy back-filled from the model before the gate reads `pg_policies` → gate passes → prod (migrations only) ships the table with **no DB-level wall**. CLAUDE.md golden rule 1 + ADR-020 both name this gate as the enforcement; the promise is not kept. **Independently verified by code trace.** | Split `RlsTestSetup`: the integration factory applies **only** role+grants (migrations already create policies); keep the policy-DDL variant for the `EnsureCreated` `RlsBackstopTests` harness. Add a meta-assertion that `IntegrationTestFactory` never references `RlsDdl.StatementsFor`. | Certain |

### Medium

| ID | File:line | Finding | Conf |
|----|-----------|---------|------|
| **RLS-2** | `src/Api/Services/TenantDissolutionService.cs:30-36` (+ erasure/leave callers) | Dissolve relies on an unenforced "ambient tenant == argument tenant" invariant; under RLS a stale-JWT violation makes every `ExecuteDelete` on RLS'd tables a **silent 0-row no-op** while non-RLS core teardown succeeds → orphaned audit/subscription/invitation/domain rows = silent GDPR-erasure failure, no exception, no log. Fix: wrap `DissolveAsync` body in `EnterTenant(tenantId)`. | Likely |
| **RLS-3** | `src/Infrastructure/Persistence/RlsSessionInterceptor.cs:146-156` | The system-context bypass is **inferred from a null tenant**, not explicitly entered. The EF wall treats null tenant as fail-closed (`Guid.Empty`); the RLS wall treats the same state as **fail-open**. A future job/detached async that loses tenant context gets a DB-wide bypass — for the exact filter-escape class the backstop exists to catch. Fix: explicit `EnterSystem()`/`ISystemScope` seam. | Certain |
| **RLS-4** | `src/Infrastructure/Repositories/EfRepository.cs:16-19` (+ 4 live sites) | `QueryAllTenants()`'s RLS sanction (a query tag) **silently evaporates** when composed into `ExecuteUpdate`/`ExecuteDelete` (tags don't render there — pinned by test). The API reads as sanctioned cross-tenant, compiles, and silently affects 0 rows. Four production sites do this today (work only by ambient posture). Fix: machine-ban the composition; add an explicit `ExecuteAllTenants…` API. | Certain |
| **ADM-1** | `src/Api/Controllers/AdminController.cs:348-350` + `NotificationService.cs:59-86` | Staff MFA-reset notification (an account-takeover primitive) is **suppressible by the target's own notification prefs** — both channels off ⇒ no in-app row, no email. "A malicious reset cannot be silent" is false. Fix: `security.*` kinds bypass prefs. | Certain |
| **ADM-2** | `src/Api/Controllers/AdminApiControllerBase.cs:33-43` | The staff gate accepts **impersonation tokens**: staff A impersonating staff B (both allowlisted) can comp/reset-MFA/broadcast/mint-impersonation, every audit row attributing **B**. Defeats the audit log on the highest-privilege surface. Fix: reject principals carrying `impersonated_by` in `RequireStaffAsync`. | Certain |
| **ADM-3** | `src/Api/Services/MfaLoginService.cs:40-56` + `RateLimiting.cs:80-89` | MFA step-up verify has **no per-user/per-challenge attempt cap** — only a per-IP window. A wrong code doesn't consume the challenge, so an attacker with factor 1 sprays TOTPs across IPs (~333k expected guesses, hours with a botnet). v2's R28 fixed replay, not brute force. Fix: per-user cumulative MFA-failure lockout (mirror OTP) and/or per-challenge cap. | Certain |
| **ADM-4** | `src/Api/Services/MfaService.cs:63-64` + `TokenHasher.cs:11-16` | Typeable recovery codes are ~49.5 bits stored under **unsalted, unkeyed SHA-256**, and long-lived. A leaked DB row is offline-crackable (day-scale on a GPU, less amortized over 10 codes/user) → a second factor. Fix: HMAC-SHA256 under a server pepper, or lengthen to ~74 bits. | Certain |
| **ADM-5** | `src/Api/Controllers/AdminController.cs:133-135,190-192` | Comp/revert 409 keys on `StripeSubscriptionId` **presence, not liveness**; canceled events persist the id and it's never nulled → a churned tenant (the exact goodwill-comp target) can **never** be comped or cleaned up. Stripe-wins direction is sound. Fix: treat `Status==canceled`/lapsed period as not provider-managed, or clear ids on terminal cancel. | Likely |
| **DEP-2** | `src/Api/Program.cs:218-242` | The single-origin host serves the SPA in Production with **zero security headers**: no HSTS (despite HTTPS redirect), no `nosniff`, no frame-ancestors, no Referrer-Policy, no CSP. The API became a browser HTML host in DEPLOY-1 and never grew them. Fix: `UseHsts()` + a header middleware when `ServeWebClient` outside Development. | Certain |
| **DEP-4** | `Dockerfile:8-10` / `ci.yml:17` / `TECH_STACK.md:25` | **Three-way SDK-pin drift**: CI floats `10.0.x`, the Dockerfile pins `sdk:10.0.301` with a now-false comment (lockfile was regenerated under 10.0.302), TECH_STACK says 10.0.301, CLAUDE.md says 10.0.302, and the `native-paths` filter references a `global.json` that doesn't exist. This class went red twice in one day (#142/#143). Fix: one pin source (`global.json`); a bump-together playbook in CLAUDE.md. | Certain |
| **DEP-7** | `.github/workflows/ci.yml:787-806` | `deploy-prod` fires the hook with **no version-gated smoke, no health assertion, no concurrency group**; its "gated" property depends entirely on a `production` Environment reviewer that lives only in repo settings, not the tree. A clone that skips that setup gets un-gated auto-deploy-to-prod on any `main` push. Fix: mirror the staging smoke + `PROD_BASE_URL`; make the reviewer step an explicit downstream checklist item. | Certain |
| **NAT-1** | branch `feat/native-oauth-resilience` (unmerged) | NATIVE-12 (OAuth process-death resilience) is **built but only on an unpushed local branch** — no `origin/` ref, zero `docs/` mention on develop. The failure mode is documented in shipped `AndroidManifest.xml:6-11` (mid-OAuth process kill → app closes). On develop the bug is open, untracked, and the completed +838-line fix is one disk failure from loss. Fix: push the branch; merge or log NATIVE-12 as in-flight in `docs/stories/native.md`. | Certain (branch) / Likely (impact) |
| **NAT-2** | `src/Maui/Perezosoft.Maui.csproj:13-19` + `ci.yml` workload restores | The MAUI lockfile-exclusion is properly ADR'd, but its compensating claim ("CPM pins the versions") is **inaccurate**: both `PackageVersion`s resolve to `$(MauiVersion)`, a workload-supplied property that floats with the SDK; Android/Windows workload restores are unpinned; transitive pinning is off. A workload/SDK bump silently changes the shipped native dependency graph — exactly what R25 prevents elsewhere. Fix: `--version`-pin all native legs; fix the wording; consider transitive pinning. | Certain |
| **NAT-3** | `src/Maui/MauiProgram.cs:21-27` + `network_security_config.xml:2-9` | Release builds inherit dev wiring: API base `http://localhost:5238` compiled into **all** configs, cleartext network config + `PEREZOSOFT_API_BASE_URL` override ship in Release. A downstream Release AAB built without touching this sends OTP codes + refresh tokens cleartext to whatever binds device-localhost:5238. Fix: fail Release builds until a real base URL is supplied; scope cleartext + override to Debug. | Likely |
| **UX-1** | `src/Shared.Ui/Layout/MainLayout.razor:135-137` | The locale-mismatch reload redirects **any anonymous path to `/`** — including `/join` and `/auth-callback` — destroying the invite-acceptance journey in PREFS-1's own flagship scenario (account locale ≠ device locale), and leaving a stale `post_login_redirect` landmine for the next sign-in. Fix: reload into `Nav.Uri` for `/join`/`/auth-callback`; only `/login`/`/auth-error` need the `/` override. | Likely |
| **UX-2** | `MainLayout.razor:119-137` + `LocalStorageCulturePersistence.cs:13-16` | Possible **infinite reload loop** when the culture store is readable but not writable (quota/policy): `PersistAsync` swallows the write failure, `forceLoad` fires, the reboot reads the old value, mismatch → reload forever. Fix: read-back after persist and only reload on a successful round-trip, or bound with a one-shot marker. | Suspected |
| **TR-1** | CLAUDE.md doc map | Three post-v2 top-level docs — `NEW_APP_GUIDE.md` (the onboarding "spine"), `OVERVIEW.md`, `_PLATFORM_PRIMER.md` — are **missing from the auto-loaded doc map**, so the template's primary onboarding path is invisible to every Claude session. Fix: add three rows. | Certain |
| **TR-4** | `WAYS_OF_WORKING.md:70-78` + PR template | The add-a-slice checklist and PR template **never mention the ADR-020 requirement** to ship an `ITenantScoped` entity's RLS policy in the same migration. It's not automatic; the Notes exemplar can't demonstrate it (its policy is in the frozen platform migration); the only backstop is the late CI gate — which **RLS-1 shows can't fire.** Fix: add the `RlsDdl.StatementsFor` step + a PR checkbox. (Pairs with RLS-1: the recipe is undocumented *and* unenforced.) | Certain |
| **TR-6** | `docs/postman/README.md:3` + collection | README claims "every HTTP surface", but six Auth/NativeAuth browser-redirect/native endpoints are absent with **no exclusion note** — under the binding Postman rule this reads as "incomplete or nonexistent." Fix: add doc-only requests or an explicit exclusion list. | Certain |

### Low

| ID | File:line | Finding |
|----|-----------|---------|
| **RLS-5** | `RlsSessionInterceptor.cs:99-129` | GUC cache not invalidated on `TransactionFailed` (commit throws) — a bypass GUC can revert while the cache believes it's still set, leaving the backstop actually-off on that connection. Add `TransactionFailed`→invalidate. |
| **RLS-6** | `ArchitectureTests.cs:48-53` | The Features RLS-tag ban scans the `RlsTags` **identifier** but not the tag **literal** — `.TagWith("rls:cross-tenant")` in a slice passes the scan and the interceptor honors it. Extend to the literal. |
| **RLS-7** | `RlsSessionInterceptor.cs:40,154` | Tag detection is `Contains("-- rls:cross-tenant")` anywhere in command text, not anchored to EF's leading tag block — an injection-shaped bug embedding the marker disarms the backstop. Anchor to the leading comment block. |
| **RLS-8** | `ApiKeyService.cs:92-93` | API-key last-used stamp works only because auth runs tenantless (RLS-3's implicit bypass); a 4th instance of RLS-4's foot-gun. Stamp under `EnterTenant(key.TenantId)`. |
| **DEP-1 / ADM-10** | `ProxyForwardingExtensions.cs:26-31` + `RateLimiting.cs:68,82` | With `Proxy:Enabled=true` the known-proxy lists are cleared (trust-any-peer); safe behind Render as sole ingress (default `ForwardLimit=1`), but a directly-reachable clone lets any client forge `X-Forwarded-For` → per-IP rate-limit bypass (and, via ADM-3, unbounded MFA guessing). Document the sole-ingress assumption; expose `KnownNetworks`/`ForwardLimit` config. *(Same root, filed by two auditors.)* |
| **DEP-3** | `Program.cs:221-222,302` | No cache policy on the SPA host — heuristic caching of `index.html` can pin a stale shell (Blazor integrity mismatch after deploy); fingerprinted assets get no long-lived `Cache-Control`. Add `no-cache` on the fallback, `immutable` on `_framework`. |
| **DEP-5 / NAT-5** | `ci.yml:206-207` | `native-paths` filter omits **`Directory.Build.props`** (warnings-as-errors/analyzer config that can break the Apple compile); a push touching only it skips the Apple build + all smokes. Add it to the regex. *(Filed by two auditors — one finding.)* |
| **DEP-6** | `ci.yml:748-757` | Vacuous smoke pass: if the hook secret is set but `STAGING_BASE_URL` isn't, the deploy fires and the smoke exits 0 with a notice → real unverified deploys, green pipeline. Fail (not skip) when hook is set and base-URL isn't. |
| **DEP-8** | `ci.yml:1-9` | No `permissions:` block — every job runs with the write-all default `GITHUB_TOKEN`, including jobs that execute freshly-downloaded third-party code. Add top-level `permissions: contents: read`. |
| **DEP-9** | `ci.yml:77,440,509,650` | Inconsistent supply-chain pins: gitleaks version-pinned-not-checksummed, mailpit from `releases/latest` + `:latest` image (non-reproducible), unpinned dotnet tools, tag-pinned (not SHA) actions. Pin mailpit; checksum raw downloads. |
| **DEP-10** | `ServiceCollectionExtensions.cs:107-116` | Stripe-key guard is presence-only — no `sk_live_`/`sk_test_` sanity. A real prod deploy with a test key boots silently in test mode; a live key on staging can make real charges. Add an `ExpectLiveKey` fail-closed guard. |
| **DEP-11** | `Dockerfile:45` | Runtime stage floats `aspnet:10.0` while the build stage is patch-pinned — non-reproducible runtime layer; same drift confusion as DEP-4. Pin to the same discipline. |
| **DEP-12 / TR-7** | `render.yaml`, `ci.yml` smokes, `smoke.js`, `Maui.csproj:45` | Rebrand gaps not in REBRANDING.md §5: `render.yaml` FromName/service name, the hardcoded `com.perezosoft.platform` in CI/Android smoke, Catalyst `keychain-access-groups`, and `<ApplicationTitle>`. A by-the-book rebrand breaks the native smokes + Catalyst SecureStorage and ships "Perezosoft" labels. Enumerate them in §5. |
| **NAT-4** | `ci.yml:479` + `NativeSmokeTests.cs:20-21` | Windows smoke asserts only `dotnet test --filter Category=NativeSmoke`; a zero-match filter historically exits 0 → silent-green canary. Assert ≥1 test ran. |
| **NAT-6** | `ci.yml:615,629` | Android smoke crash diagnostics grep logcat for `"companyname"` (pre-rename leftover) — never matches; app-tagged crash lines silently dropped from failure logs. Replace with `perezosoft`. |
| **NAT-8** | `Entitlements.Debug.plist:4-9` vs `MauiProgram.cs:90-93` | Contradiction: the plist comment says Catalyst Debug SecureStorage works without entitlement; the DI code + `DebugFileSessionStore` (PR #125) say it fails. A maintainer trusting the comment could delete the file store and re-break local dev. Correct the comment. |
| **NAT-9** | `DebugFileSessionStore.cs:36-42` + `AndroidManifest.xml:3` | (a) Catalyst-Debug refresh-token file written before `chmod 0600` (brief umask window); (b) `allowBackup="true"` on an auth-bearing app. Debug-only/defense-in-depth. Create 0600 atomically; set `allowBackup="false"`. |
| **NAT-10** | `LoopbackOAuthInitiator.cs:26,48-87` | Windows loopback OAuth: port-probe TOCTOU + **no `state` binding** the callback to the pending listener → local login-CSRF (victim signs into attacker's account; token theft blocked by single-use server-side exchange). Add a `state` nonce (RFC 8252 §8.9). |
| **NAT-11** | `docs/NATIVE_PARITY.md:11-12,29,45-50` | Stale: still says iOS/macCatalyst "never run / 🔍" though the Apple QA pass (2026-07-06) and NATIVE-6 device pass (2026-07-14) are done. Add a dated closure note. |
| **ADM-6** | `AdminController.cs:259-283` | `announce-all` (largest blast radius) is the **only** admin write with no durable actor attribution and no request dedupe. Include `staffUserId` in the payload / audit; optional cooldown. |
| **ADM-7** | `AdminController.cs:332-366` | Admin MFA reset doesn't revoke the target's refresh tokens/sessions — fine for "lost authenticator", wrong for compromise recovery (attacker sessions survive). Revoke on reset, or document the limitation. |
| **ADM-8** | `AccountController.cs:81-113` | Preference writes accept impersonation tokens; the no-write-while-impersonating guard is **client-side only**. Cosmetic data, but the ADR-mandated guard shouldn't be one client bug from failing. Reject `impersonated_by` server-side. |
| **ADM-9** | `MainLayout.razor:108-115,131-143` | Shared-device pref adoption caches user A's account prefs into device storage and never clears them → user B (never chose) signing in on the same device PUTs A's pref into B's account. Cosmetic, but broader than the ADR's documented behavior. Tag device values by origin; adopt only explicit picks. |
| **UX-3** | `ThemeSwitcher.razor:34-41`, `LanguageSwitcher.razor:38-49` | PR #139 fixed only the E2E test; the product race remains — a switcher pick lost before its best-effort PUT lands is silently reverted by the next sign-in's server-wins reconcile ("it keeps switching back"). Surface PUT failure / pending-adoption marker; record the accepted race as an ADR-022 amendment. |
| **UX-4** | `ThemeSwitcher.razor:22-26` vs `MainLayout.razor:77-88` | After a soft sign-in, the switcher reads `appTheme.current` once; if reconcile applies the server theme after init, the select shows a stale value all session. Re-read on a `ThemeChanged` event. |
| **UX-5** | `Billing.razor:40,42` | i18n gap: plan key/status render raw machine tokens ("free"/"active") untranslated on the money page; minor `Loading…`/relative-time nits alongside. Map through localized lookups with raw fallback. |
| **TR-2** | CLAUDE.md:134 | Doc map says "123 cases"; the plan has **125** `### QA-*` headings — the review-only sync (S0-G7) drifted within 4 days. Update or drop the count. |
| **TR-3** | CLAUDE.md:118 | "Status / not yet decided" still lists the non-web framework as deferred, but ADR-018 committed to MAUI parity and TECH_STACK says "Committed". Auto-loaded manual contradicts the ADR log. Delete the stale bullet. |
| **TR-5** | `Notes/NotesEndpoints.cs:24` | The exemplar slice returns an anonymous error object while the R18-blessed shared `ErrorResponse` record is used everywhere else — the exemplar every slice copies teaches the banned shape. One-line change to `new ErrorResponse(...)`. |
| **TR-8** | `MfaChallengeService.cs:30`, `FileDownloadTokenizer.cs:17` | ADR-019 claims all frozen `Template.*` DataProtection strings "are guarded by comments" — only 2 of 4 are. A downstream "finish the rename" sweep could rename them and invalidate in-flight tokens. Add the guard comments (or amend the ADR). |
| **TR-9** | `DocAndConfigSyncTests.cs:54` | The config-catalog gate matches only dotted `"Section:Key"` at `config(...)` sites — blind to `Environment.GetEnvironmentVariable` reads and `GetSection("X")` binds. No live drift; the gate can't catch the next one. Add a second regex. |
| **TR-10** | `docs/DECISIONS.md` (absence) | The Postman-governance decision (repo canonical, one-way mirror, native sync rejected) is a real reversible decision with recorded alternatives but has no ADR, unlike every comparable post-v2 decision. Add a short ADR/index pointer. |

### Info (noted, no action)

- **ADM-11** — MFA-reset for a tenant-less user leaves no audit trail (audit log is tenant-scoped; same gap as impersonation). Consider a platform-scoped audit sink.
- **TR-11** — Static `[Test]` count (34) vs the documented "suite 32" narrative — reconcile once via `dotnet test --list-tests` (Phase 2 tool pass).

---

## Findings mapped to the six audit dimensions

- **0. Template-readiness** — Strong overall (rename complete, Postman ~complete, config catalog ~complete). Gaps: the RLS slice recipe is undocumented *and* unenforced (TR-4 + RLS-1); the Notes exemplar teaches a banned error shape (TR-5); onboarding docs are unlinked (TR-1); rebrand touchpoints incomplete (DEP-12/TR-7, NAT-7). New slices **do** auto-inherit tenancy filter, stamping, authz, i18n, audit, outbox — but **not** an RLS policy (manual migration step, gated only by the broken RLS-1 gate).
- **1. Architecture** — Horizontal-core/vertical-slice separation holds; no core→slice inversion found. The B9-6 move of PUBAPI/HOOKS to `src/Api/Endpoints/` is legitimate but left enforcement scans behind (S0-G1/G2).
- **2. Docs ↔ code sync** — Unusually good for a delta this size. Residuals are edge drift: TR-1/2/3/8/10, NAT-11, ADR-020 header still says "DEFERRED", ADR-023 referenced but nonexistent.
- **3. Contradictions** — NAT-8 (plist vs code), the `Proxy` trust-model split (DEP-1/ADM-10), and the EF-fail-closed vs RLS-fail-open inversion (RLS-3).
- **4. Gaps (validation/authz/security)** — The bulk of the Mediums: MFA brute-force (ADM-3), recovery-code hashing (ADM-4), impersonation on privileged/mutating endpoints (ADM-2/8), notification suppression (ADM-1), web-host headers (DEP-2), silent set-based-write posture loss (RLS-2/4).
- **5. Debt & smells** — Low. The MAUI singleton-with-scoped-JSRuntime smell (UX notes) and the four repeated `QueryAllTenants→ExecuteDelete` sites (RLS-4) are the notable patterns; the new epics otherwise follow the reference shape.
- **6. CLEAN/SOLID** — No new god objects or boundary violations; the auth controller split (v2 B9-1) held. TR-5 is the one place the exemplar violates the shared-contract convention it's meant to model.

## Prioritized top 10

1. **RLS-1 (High)** — restore the RLS parity gate so it actually fails on a policy-less tenant table; add a standing meta-assertion so it can't go tautological again.
2. **ADM-2 (Med)** — reject impersonation tokens at the staff gate (audit-integrity + privilege containment).
3. **ADM-3 (Med)** — add a per-user MFA brute-force lockout.
4. **RLS-2 (Med)** — `EnterTenant` inside `DissolveAsync` so erasure can't silently no-op (GDPR).
5. **ADM-4 (Med)** — key/pepper or lengthen recovery codes.
6. **DEP-2 (Med)** — security headers + HSTS on the single-origin host.
7. **RLS-4 (Med)** — machine-ban `QueryAllTenants`/`IgnoreQueryFilters` composed with set-based writes.
8. **ADM-1 (Med)** — `security.*` notifications bypass user prefs.
9. **ADM-5 (Med)** — comp/revert liveness check (unblock churned tenants).
10. **UX-1 (Med)** — locale-reload preserves `/join`/`/auth-callback` deep-links.

Close behind: DEP-4 (SDK-pin drift, recurring red builds), NAT-1 (unpushed NATIVE-12), TR-4 (RLS slice recipe), the S0-G1/G2 scan-coverage gaps.

## Foundation-readiness verdict

**Ready with fixes — not a re-open of "Solid."** The template remains solid by construction: v2's guarantees held with zero regressions, and the post-v2 epics introduced no cross-tenant breach or privilege escalation. But one *enforcement* guarantee is broken (RLS-1), the auth delta needs brute-force/hashing hardening before a real app trusts it, and several enforcement scans need to grow to cover the code surfaces that appeared since v2. None of this requires structural rework; it's a bounded remediation batch plus enforcement backfill. Phase 4's adversarial build-a-slice pass should specifically re-test the RLS new-slice contract once RLS-1 is understood, since that is the guarantee most likely to bite the first downstream app.

## Unknowns / needs human decision

- **RLS-3 fix shape** — introducing an explicit `EnterSystem()`/`ISystemScope` seam touches the outbox dispatcher, scheduler, sweep jobs, and pre-auth flows. That's a core-surface change; Phase 5 should decide seam vs. the lighter "log-on-null-tenant-bypass" interim.
- **NAT-1** — is NATIVE-12 intended for develop, or deliberately parked? Determines whether it's "push+merge" or "log as in-flight."
- **DEP-7 / prod gating** — prod activation is downstream (ADR-018 amendment); decide whether to harden `deploy-prod` now (template hygiene) or defer with the rest of prod activation.
- **ADR-023** — referenced in the brief and session memory but absent from the branch. Confirm whether it was meant to be authored (distribution-downstream) or the ADR-018 amendment is the canonical record.
- **ADM-4 severity** — depends on the threat model for DB/backup exposure; if backups are strongly protected, the recovery-code weakness is lower priority than its Medium suggests.

---
*Phase 1 complete. Phases 2–4 add findings and defer to this report's rules; Phase 5 is the only reconciler and the only gate to implementation. No production code was modified in this phase.*
