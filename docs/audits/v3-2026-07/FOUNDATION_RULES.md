# FOUNDATION_RULES — v3 candidate block (Phase 1)

> **Status: CANDIDATE (Phase 1).** These are proposed additions from the v3 delta audit, continuing the
> numbering after `docs/audits/v2-2026-07/FOUNDATION_RULES.md` v1.0 (R1–R35, which remain **binding** and
> unchanged — no v1.0 rule is revised or removed here). Phases 2–4 may add to or flag entries in this
> block; **Phase 5 is the only phase that de-duplicates, resolves conflicts, and promotes this block into
> the consolidated `FOUNDATION_RULES.md` v2.0.** Overlaps between candidates are recorded in
> `RULE_CONFLICTS.md`, not silently merged here.
>
> **Candidate blocks so far:** Phase 1 → R36–R76 (tenancy/RLS, auth, deploy/CI, native, docs). Phase 2 →
> R77–R81 (supply-chain, coverage, tooling). Phase 3 → R82–R99 (logic-correctness + test-completeness TDD
> invariants). Phase 4 → R100 (composability enforcement, adversarially proven).

Each rule: imperative · tied to its finding · category · **[machine]** (arch test / analyzer / CI gate —
prefer extending `tests/Api.Tests/ArchitectureTests.cs`, `DocAndConfigSyncTests.cs`, or `ci.yml`) vs
**[review]**.

## Tenancy / RLS

- **R36 [machine] — RLS parity gate must test migrations alone.** The migration-gated integration
  database receives RLS policies **only from migrations**; the test harness may provision roles/grants but
  never model-derived policy DDL. Add a meta-assertion that `IntegrationTestFactory` does not reference
  `RlsDdl.StatementsFor`. *(RLS-1 — the keystone; the gate is currently tautological.)*
- **R37 [machine] — Dissolve/teardown enters its target tenant.** `ITenantDissolutionService.DissolveAsync`
  (and any set-based teardown of `ITenantScoped` data) executes under `EnterTenant(targetTenantId)`; an
  RLS-harness test dissolves tenant B while tenant A is current and asserts B's rows are gone. *(RLS-2)*
- **R38 [review→machine] — No implicit RLS bypass.** The RLS bypass GUC is asserted only for an
  explicitly-entered system scope (`EnterSystem()`/`ISystemScope`) or a sanctioned tagged command — never
  inferred from a null tenant on a request-scoped context. Review until the seam lands, then arch-test the
  interceptor inputs + assert jobs/dispatcher/pre-auth enter system scope. *(RLS-3)*
- **R39 [machine] — Never compose the cross-tenant hatch with set-based writes.** No statement chains
  `QueryAllTenants()`/`IgnoreQueryFilters()` into `ExecuteUpdateAsync`/`ExecuteDeleteAsync`; set-based
  cross-tenant writes require `EnterTenant`/system scope first. Multiline source scan over `src/**`.
  *(RLS-4, RLS-8)*
- **R40 [machine] — Invalidate the RLS GUC cache on every revert path.** `RlsSessionInterceptor` implements
  `TransactionFailed`/`TransactionFailedAsync` → invalidate, alongside the existing rollback/savepoint
  handlers. Integration test forces a commit failure and asserts the next command re-asserts the GUCs.
  *(RLS-5)*
- **R41 [machine] — The Features RLS-tag ban covers the literal.** The `Features/**` cross-tenant ban
  matches the tag **literal** `rls:cross-tenant` (any string containing it), not only the `RlsTags`
  identifier. *(RLS-6)*
- **R42 [machine] — RLS tag recognition is anchored.** The interceptor recognizes the cross-tenant tag only
  in the leading EF tag-comment block of the command text. Unit test: a marker inside a query literal does
  not set the bypass GUC. *(RLS-7)*
- **R43 [machine] — Tenant-hatch bans cover `src/Api/Endpoints/`.** Extend the R5 hatch ban and the R35
  route-uniqueness scan to `src/Api/Endpoints/` (request-path tenant-scoped code, same as a slice).
  *(S0-G1, S0-G2)*

## Auth / admin surface

- **R44 [machine] — Security-class notifications bypass preferences.** Notifications whose kind starts with
  `security.` are delivered on both channels regardless of `NotificationPreferences`. Integration test:
  prefs both-off + `admin.mfa.reset` still produces an in-app row and an email. *(ADM-1)*
- **R45 [machine] — Staff endpoints reject impersonation tokens.** Any endpoint gated by
  `RequireStaffAsync` rejects a principal carrying `impersonated_by` (403). Integration test over every
  `/api/admin/*` route with an impersonation token for an allowlisted target. *(ADM-2)*
- **R46 [machine] — Every second-factor verify path has a per-user, IP-independent attempt cap.** Mirror
  the OTP lockout for MFA step-up. Integration test: N wrong TOTPs across fresh challenges from distinct
  client IPs ⇒ locked. *(ADM-3)*
- **R47 [machine] — Long-lived credential material is stored under a keyed/slow hash or carries ≥96 bits of
  entropy.** Applies to recovery codes, API keys, refresh tokens. Arch scan classifying `ITokenHasher`
  call sites by token lifetime. *(ADM-4)*
- **R48 [review] — "Provider-managed ⇒ 409" tests liveness, not id-presence.** A staff override guard keyed
  on an external subscription must check status/liveness, so churned tenants stay reachable. *(ADM-5)*
- **R49 [machine] — Every enumerated admin write is attributably recorded.** Each ADR-021 mutating route
  produces a durable record containing the acting staff user id — including platform-wide (`announce-all`)
  and tenant-less-user actions. *(ADM-6, ADM-11)*
- **R50 [review] — Mutating account-state endpoints reject impersonated principals** (or audit the write
  with the `impersonated_by` actor). Applies to preferences, profile, erasure. *(ADM-8)*
- **R51 [review] — Admin MFA reset defines its session-revocation posture.** Either revoke the target's
  refresh tokens on reset, or document that reset is not a compromise-recovery tool. *(ADM-7)*
- **R52 [machine] — Config-gated features are closed under empty configuration.** Bind every `*Settings`
  (PublicApi, Webhooks, Admin, Hosting single-origin, S3) from an empty `IConfiguration` and assert the
  feature is disabled/closed — the standing R21 gate that never shipped. *(S0-G3)*

## Deploy / CI / supply-chain

- **R53 [machine] — The single-origin host ships security headers.** When `Hosting:ServeWebClient` is on
  outside Development, responses to `/` and `/_framework/*` carry HSTS, `X-Content-Type-Options: nosniff`,
  and a frame-ancestors policy. Integration test over the host. *(DEP-2)*
- **R54 [machine] — SPA cache policy is explicit.** The `index.html` fallback is served `no-cache`;
  fingerprinted framework assets get an `immutable` long max-age. *(DEP-3)*
- **R55 [machine] — One SDK pin source.** `global.json` (or the single pinned `ci.yml` `dotnet-version`),
  the Dockerfile build-stage tag, and TECH_STACK's verified line must agree; a CI consistency check greps
  them. A bump-together playbook lists every location (incl. Apple workload-set `--version`). *(DEP-4,
  DEP-11)*
- **R56 [machine] — Every CI test-with-filter proves non-vacuous execution** (≥1 test ran, TRX/console
  parse). *(NAT-4)*
- **R57 [machine] — A deploy job that fires a hook fails (not skips) when its post-deploy smoke config is
  absent.** Partial deploy config never yields a green run. *(DEP-6, DEP-7)*
- **R58 [machine] — Every workflow declares a least-privilege top-level `permissions:` block.** *(DEP-8)*
- **R59 [machine] — No `releases/latest` URLs and no `:latest` image tags in `.github/workflows/**`; raw
  binary downloads are checksum-verified.** *(DEP-9)*
- **R60 [machine] — Stripe key mode matches an explicit expectation outside Development.** Fail-closed
  startup guard on `sk_live_`/`sk_test_` vs `Billing:Stripe:ExpectLiveKey`. *(DEP-10)*
- **R61 [machine] — The `native-paths` filter regex names every file class that can affect a native binary**
  (`Directory.Build.props`, `Directory.Packages.props`, `global.json` iff present, `src/`, the smoke
  harnesses, the workflow). A grep check validates the regex against the tree. *(DEP-5 / NAT-5)*
- **R62 [machine] — Every `dotnet workload restore` in CI carries `--version`** (one pinned workload set per
  runner family), closing the MAUI graph-drift channel the lockfile exclusion left open. *(NAT-2)*

## Native

- **R63 [machine] — Native Release builds fail when dev wiring survives.** MSBuild error if
  `Configuration==Release` and the compiled API base is `localhost`; cleartext network config and the
  API-base env override are Debug-only. *(NAT-3)*
- **R64 [machine] — Host parity gate.** Both `index.html` files reference the identical set of RCL
  `js/*.js` scripts with `theme.js` before the first stylesheet, and the two vendored `wwwroot/lib` trees
  are byte-identical. *(NAT — pins the honor-system maintainer rule.)*
- **R65 [review] — Built-but-unmerged slice work is tracked.** A slice marked built (QA drill recorded,
  tests written) reaches `origin` in the same session or is logged as in-flight in its epic story file.
  *(NAT-1)*

## Docs / template-readiness

- **R66 [machine] — Postman parity gate.** An `Api.Tests` fact reflects over the endpoint sources
  (controllers + `MapTenantFeatureGroup`/Endpoints groups), extracts `VERB path`, and asserts each appears
  in the collection (normalizing `{id}`↔`{{var}}`), with a commented allowlist for browser-redirect flows.
  Turns the binding Postman rule from PR-checklist into a red build. *(TR-6)*
- **R67 [machine] — Doc-map + count sync.** Extend `DocAndConfigSyncTests` to (a) assert every `docs/*.md`
  and `docs/stories/*.md` (allowlist: tutorial/, audits/, PDFs, `_EXAMPLE_epic.md`) appears in the CLAUDE.md
  doc map; (b) parse the "N cases" figure and compare to the `^### QA-` heading count. *(TR-1, TR-2, S0-G7)*
- **R68 [machine] — Config-catalog gate covers env-var and section-bind reads.** Extend
  `ConfigKeys_ReadInCode_AreDocumented` with a regex for `GetEnvironmentVariable("([A-Z_]+)")` and a
  ≥1-documented-key assertion for `GetSection("X")` binds. *(TR-9)*
- **R69 [machine] — DataProtection identity strings are frozen by test.** A fact pins the exact five
  `Template.*`/`"template"` DataProtection strings with a failure message quoting ADR-019, so the rename
  guard survives downstream find/replace regardless of comments. *(TR-8)*
- **R70 [review] — RLS slice recipe is documented where slice authors look.** The `RlsDdl.StatementsFor`
  migration step is in the WAYS_OF_WORKING add-a-slice checklist and the PR template ("new `ITenantScoped`
  entity ⇒ RLS policy in the same migration"). Machine half is R36. *(TR-4)*
- **R71 [machine] — Exemplar hygiene.** Extend the Features string-scan to flag `new { error` (anonymous
  error objects) in `src/Api/Features/**`; the exemplar must model the shared `ErrorResponse` shape. Narrow
  subset of the deferred R18 scan, applied where downstream copies from. *(TR-5)*
- **R72 [machine] — resx satellite parity.** Every key in `AppStrings.resx` exists in every shipped
  satellite (`AppStrings.es.resx`) and vice versa. *(UX — currently convention-only.)*
- **R73 [machine] — Reconciliation reloads preserve deep-links and are non-looping.** A client full reload
  performed for state reconciliation preserves the current route+query (or a pending `post_login_redirect`)
  and verifies the persisted boot value round-trips before `forceLoad` (or carries a one-shot marker).
  E2E: invite-accept with a mismatched account locale still lands in the target household; a write-blocked
  store causes at most one reload. *(UX-1, UX-2)*
- **R74 [review] — Server-supplied enum-ish values are localized.** Plan keys, subscription status, roles
  rendered as display text pass through a localized mapping with raw fallback, never raw interpolation.
  *(UX-5)*
- **R75 [review] — Accepted client-side races are ADR'd, not commit-messaged.** A two-way-sync "server
  wins" policy states in its ADR what happens to a device value newer than the server's; any accepted
  lose-the-pick race is an ADR amendment. *(UX-3)*
- **R76 [machine] — R3 machine half: outbound-to-user-URL requests route through `IOutboundUrlGuard`.**
  Arch scan asserting no type outside the guard/sender allowlist issues an `HttpClient` request to a
  non-constant URL. *(S0-G4)*

## Supply-chain / coverage / tooling (Phase 2 — tool-only)

- **R77 [machine] — The license gate covers every shipped assembly graph.** `dotnet-project-licenses`
  inventories the transitive licenses of the server *and* client projects (Api, Web, Shared.Ui, Maui) — not
  `src/Api` alone. *(TOOL-1.)*
- **R78 [machine] — Client (RCL) logic has fast-test coverage, not E2E-only.** The shared harness provides a
  component-test seam (e.g. bUnit) so `Shared.Ui` reconcile/sync/switcher logic is unit/integration-testable;
  a minimum-coverage or must-have-a-test assertion guards the RCL. *(TOOL-2 — currently ~1% line, E2E-only.)*
- **R79 [review] — Load-bearing single-maintainer dependencies are declared and abstracted.** A critical-path
  dependency with concentrated maintainer risk (today: `Otp.NET` behind MFA) is documented in TECH_STACK and
  sits behind a swappable seam. *(TOOL-4.)*
- **R80 [machine] — CI toolchain version pins live in a pinned manifest, not inline.** Pinned tool/pip
  versions (e.g. the QA-PDF `reportlab`/`pypdf` pins) are in a committed requirements/manifest file so a bump
  is a reviewed diff — folds into R59's supply-chain-pin hygiene. *(Phase 2 §7 nit.)*
- **R81 [machine] — The QA run-log block is append-only.** A CI git-diff check asserts the run-log section of
  `QA_TEST_PLAN.md` only gains lines (never edits/removes prior SHA-stamped entries) — the machine half of R31
  / Step-0 gap S0-G5. *(Phase 2 §7.)*

## Logic-correctness & test-completeness (Phase 3 — TDD invariants)

- **R82 [machine]** — Every `ITenantScoped` entity is wired into tenant dissolution **and** export (a
  registered `ITenantDataContributor` or an explicit teardown/export allowlist entry); model-scan arch test
  mirroring the user-keyed erasure canary, extended to `TenantId`-carrying non-`ITenantScoped` entities.
  *(LB-TEN-1 keystone; extends R12/R13 to the tenant axis.)*
- **R83 [review→machine, = machine half of R37]** — Every `DissolveAsync`/erasure/sole-owner-leave executes
  inside `EnterTenant(target)`; scan each `DissolveAsync(` caller is lexically within an `EnterTenant(`
  scope. *(LB-TEN-1 partial, RLS-2.)*
- **R84 [review]** — An entity carrying `TenantId` but not `ITenantScoped` ships a dedicated cross-tenant
  isolation test (neither query filter nor RLS wall protects it). *(LB-TEN-2.)*
- **R85 [machine]** — Single-use credential consumption (magic-link, OTP, MFA challenge) is atomic
  (conditional `ExecuteUpdate … WHERE ConsumedAt IS NULL`, affected==1; session only on the winning update).
  Proven by a concurrent-redemption test. *(LB-AUTH-3.)*
- **R86 [machine]** — Brute-force/attempt counters increment atomically (server-side increment or
  RowVersion+retry; cap evaluated against the persisted post-increment value). *(LB-AUTH-2; pairs with the
  atomic-quota rule R30.)*
- **R87 [review→machine]** — Second-factor state is not mutated before the login challenge is confirmed
  single-use. *(LB-AUTH-1.)*
- **R88 [machine]** — Outbox attempt/dead-letter bookkeeping advances on **any** completion failure incl.
  post-handler commit/flush faults; a poison-at-commit message dead-letters, never loops `Pending`.
  *(LB-BILL-2.)*
- **R89 [machine]** — The webhook recency guard rejects only *strictly older* events; two distinct events
  sharing a timestamp both take effect in arrival order. *(LB-BILL-1.)*
- **R90 [machine]** — Quota insert-conflict recovery catches only the unique violation (`23505`); any other
  `DbUpdateException` propagates. *(LB-BILL-3.)*
- **R91 [review]** — Dunning/lapse notifications fire only on a transition *out of* a granting status
  (`active`/`trialing`), never on a cold-start into a bad state. *(LB-BILL-4.)*
- **R92 [machine]** — No audit write on a tenant-scoped mutation attributes an action to a principal without
  also recording `impersonated_by` when the principal carries it (requires an `AuditEvent` attribution
  column). *(LB-ADM-1.)*
- **R93 [review]** — Tenant-permission resolution keys on the request's `tenant_id`; membership lookups for
  authz are deterministic (tenant-scoped or ordered), never an unfiltered `FirstOrDefault`. *(LB-ADM-3.)*
- **R94 [review]** — Bulk/destructive endpoints define empty-selector semantics explicitly (empty ⇒ none)
  and never default to the widest blast radius. *(LB-ADM-2, LB-UI-10.)*
- **R95 [machine]** — A test project exercises `src/Shared.Ui` `.razor` components with a component-test
  host (bUnit or equivalent); enforced by a Shared.Ui coverage floor or a razor-rendering test assembly's
  existence. *(TOOL-2 keystone; the fast-test seam R73 depends on.)*
- **R96 [machine]** — `ReconcilePreferencesAsync` no-ops entirely while `IsImpersonating` (server-wins AND
  adopt branches gated). *(LB-UI-5, ADM-8/9.)*
- **R97 [machine]** — Device preference stores are scoped to the writing principal (or cleared on sign-out)
  before a null-server value is adopted into an account. *(LB-UI-4/ADM-9.)*
- **R98 [review]** — Reconcile/state reloads preserve the current deep link for non-terminal anonymous
  paths (`/join`, `/auth-callback`); only `/login`/`/auth-error` redirect to `/`. *(LB-UI-1/2; overlaps
  R73.)*
- **R99 [review]** — The shared harness provides, and a new slice reuses (never rebuilds): a concurrency
  runner, a DB-fault-injection seam, an injectable clock on every time-sensitive helper (webhook handler,
  MFA challenge), an impersonation-token client helper, and a client component-test chassis (bUnit +
  `IJSRuntime`/persistence doubles + JWT-claim builder). Plus the standing TDD mandate: no production code
  without a failing test first at the right level; every slice ships happy-path + permission-denied +
  cross-tenant-isolation tests before "done"; every new public method tested per branch + error path;
  `QA_TEST_PLAN.md` updated in the same PR; run log append-only (R81).

## Composability enforcement (Phase 4 — adversarial, proven by build-a-slice)

- **R100 [machine]** — The route-uniqueness gate must scan the **mandated** slice helper
  `MapTenantFeatureGroup("…")` (not only raw `MapGroup`), and must cover `src/Api/Controllers/`,
  `src/Api/Features/`, **and** `src/Api/Endpoints/`; two surfaces claiming the same `/api/<x>` prefix fail
  CI. *(ADV-P4-1 — proven: a deliberate cross-slice `/api/projects` collision passed the current gate because
  its regex never matches the helper every slice is required to use. Folds with CAND-S0-1.)*

*(Numbering is provisional; Phase 5 re-numbers on promotion. Do not treat R36–R100 as binding until
consolidated.)*
