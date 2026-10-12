# FOUNDATION_RULES — v3.0 (final, post-v4-consolidation)

> **Status: FINAL and BINDING since 2026-10-01** (the v4 enforcement close-out — T11/T62/T67/T68/T69 — landed on
> develop; `CONTRIBUTING.md` and `CLAUDE.md` point here). `docs/audits/v3-2026-07/FOUNDATION_RULES_v2.md` is the
> v2.0 historical layer.
> **Range.** R1–R35 (v1.0, `docs/audits/v2-2026-07/FOUNDATION_RULES.md`) and R36–R76 + R80 (v2.0,
> `docs/audits/v3-2026-07/FOUNDATION_RULES_v2.md`) carry forward **unchanged except for the amendments in §1**.
> **Amended 2026-10-07 by Arch A6 (#366):** R9 extended — no platform migration names the sample (§1).
> **Amended 2026-10-07 by Arch A4 (#364):** R2 and R145 extended to the shared-or-tenant shape (`ISharedOrTenantScoped`, §1).
> **Amended 2026-10-06 by Arch A1 (#362):** the Architecture milestone adds **R159–R162** as finals (R159 the composition seam, R160 the ownership map and stamp, R161 the schema parity gate, R162 the entity writer, all §10); R8 amended (§1).
> **Amended 2026-10-06 by Arch A12 (#371):** R149 admits a component that takes its test id as a parameter, with literal callers.
> **Amended 2026-10-02 by ADR-031:** R80 and R138 retired with `.forgejo/` (GitHub is the only forge again,
> ADR-030); R98, R137 and R139 now speak of the one workflow; R98 no longer asks for branch protection (a private
> repo on GitHub Free has none, and none is wanted).
> This file adds **R81–R158** as finals, with **R77, R78, R79, R94 and R156 retired** (R77–R79: the LOCALCI-1/2
> reservation is withdrawn — LOCALCI-1 is superseded by LOCALCI-4 and R79's intent is R114; R94 merged into R130;
> R156 merged into R86). A number, once retired, is never reused. Candidate ids (`-cand`, letter suffixes) never
> appear outside the audit folder (R116).
> **Integrity:** all v4 phases audited `8c8ed4a746c57a6df8bac5f31195f011dc0758c4` (Phase 5 verified). Nothing here
> overturns a v1/v2 invariant; every v4 rule is additive or completes a previously review-only rule.

Each rule: **[machine]** (arch test / analyzer / CI gate) or **[review]** · text · enforcement mechanism ·
the findings it subsumes. **Enforcement manifest (T67, 2026-10-01):** `tests/Api.Tests/RulesEnforcement.cs` names,
per `[machine]` rule, the test or CI step that holds it — or the tracker issue that still owes it — and
`EnforcementGateTests.EveryMachineRule_NamesAStandingCheck` fails when a machine rule is missing from it or a named
check does not exist; the mechanism text below is the intent, the manifest is the fact. The full candidate text with evidence is in `FOUNDATION_RULES.md` (Phases 1–4).

## 1. Amendments to carried rules (from `PHASE5_GATE.md` §2)
| Rule | Amendment |
|------|-----------|
| R16 [review] | Machine half added: **R89**. |
| R36 [machine] | CI scan retained; runtime structural check added by **R155**. |
| R43 [machine] | "…**and every dissolve call site uses `ITenantDissolutionService`**" — machine half **R123**; canary extends to nullable-`TenantId` tables carrying tenant content (**R145**). |
| R45/R50 [machine] | Scope includes minimal-API (`MapGroup`) endpoints: tenant-visible writes under impersonation carry `impersonated_by` attribution (C21 decision). |
| R51 [machine] | Family revocation restored in bounded form by **R81**; logout leg stated by **R124**. |
| R53 [machine] | "…closed under empty configuration, **except enumerated exceptions whose closed state is non-empty (today: `SignupSettings`), listed by name in the reflective posture gate (R122)**." |
| R57 [machine] | "…advances **durably (in the claim statement)** on any completion failure and **distinguishes permanent from transient failures**" — **R130, R131**. |
| R63 [machine] | "…every workflow declares a least-privilege `permissions:` **on forges that honour it; on forges that ignore it, R98 applies**." |
| R67 [machine] | Text unchanged; **enforcement pending R103/R141** (today only a csproj `<Error>` reached by a human Release build). |
| R68 [machine] | Gate widened to its full text: script set **and** ordering **and** `wwwroot/lib` byte-identity. |
| R70 [machine] | Per-page floor: **every `src/Shared.Ui/Pages/*.razor` has ≥1 bUnit test**. |
| R73 [review] | Gate-state proof is owned by bUnit (both states) + one gate-off E2E lane (**R147**); a gate-on E2E suite does not satisfy it. |
| R75 [machine] | "…covers C# reads (`IConfiguration`, `GetEnvironmentVariable`, flat indexers — R95, `SectionName` consts — R153); deploy-time YAML keys are a DEPLOYMENT §10 review item (R100)"; the QA run-log clause is **unverified on Forgejo** until the `guard=ran/skipped` marker lands (C18). |
| R80 [retired] | Retired 2026-10-02 (ADR-031): `.forgejo/` was removed, so there is no second workflow copy to hold together. |
| R15 | Clock-injection scan widened to `src/Shared.Ui` (**R148**). |
| R2 [machine] | "…an entity carrying a `TenantId` implements `ITenantScoped`, **or `ISharedOrTenantScoped` when its rows are either shared (null) or one tenant's (Arch A4, 2026-10-07)**, or is allowlisted by name; a nullable `TenantId` is one of the two or allowlisted infrastructure." Gate: `EveryEntityWithATenantId_IsScopedOrAllowlisted`, `EveryTenantScopedEntity_HasAGlobalQueryFilter` (both markers). |
| R9 [machine] | "…and no platform migration names the sample's table: the sample is its own removable unit (`AddNotesSample` + `NotesSampleRlsPolicy`), removed by an app migration (Arch A6, 2026-10-07)." Gate: `PlatformMigrations_DoNotNameTheSample`. |
| R145 [machine] | A shared-or-tenant entity ships its own four facets instead — `<Entity>_SharedOrTenant_{Dissolve,Export,SharedWrites,Erasure}_*` (`EverySharedOrTenantEntity_ShipsItsLifecycleSpec`) — and its four command-scoped RLS policies by migration (`EverySharedOrTenantTable_HasForcedRlsAndAllFourPolicies_AfterMigrations`); Arch A4, 2026-10-07. |
| R8 [machine] | "Only **`src/Api/AppComposition.cs`** references `Vuelto.Api.Features.*` from outside `src/Api/Features/`" — the composition seam (**R159**, Arch A1, 2026-10-06); `Program.cs` names no slice. |

## 2. Auth / session (Critical/High)
- **R81 [machine]** — A rotated-out refresh token is honoured at most **once** inside the reuse grace; a second presentation, or any presentation after the grace, is `Reuse` and revokes the family; the grace outcome is logged at Warning with a per-user count. — `RefreshReplayTests` (3rd presentation ⇒ 401 + revoke-all) + `TokenServiceTests`. — AUTH-1, AUTH-13.
- **R82 [machine]** — List-typed allow/deny settings are normalized at bind (trim, case-fold, blanks dropped, leading `@` stripped) and exposed only normalized. — reflective theory in `ConfigPostureTests`. — AUTH-7.
- **R84 [review→machine]** — A client session-continuity change ships a fake-clock test crossing the access token's `exp` under an unreachable server (no `SignedOut` until the server rejects; exactly one when it does; no `/login` bounce on a later renewal). — test-name manifest in `SessionKeepAliveTests`. — AUTH-5.
- **R107 [machine]** — The client's refresh call carries an explicit timeout `T` and retry pause `D` with `T + D < RefreshToken:ReuseGraceSeconds`. — cross-project constant test in `ConfigPostureTests` + `SessionKeepAliveTests`. — UX-6. (Concrete instance of R144.)
- **R108 [machine]** — "The server said no" = 401, or 400/403 whose body parses to an `ErrorResponse` with a known code; anything else is `Unreachable`, keeps the stored credential, and retries with capped exponential backoff. — `SessionKeepAliveTests` theory. — UX-7, UX-12.
- **R111 [machine]** — A `SignedOut` transition on a protected route navigates to `/login` from one place (`MainLayout.OnSignedOut`); the layout gates on session-held, not token expiry. — bUnit `MainLayout` test. — UX-11, AUTH-5.
- **R112 [machine]** — Server auth error codes map to copy in one table (`AuthErrorCopy`) used by every surface; no inline code-literal switches in `.razor`. — `SignupRefusedCopyTests` + arch grep. — UX-14.
- **R123 [machine]** — A tenant is removed only through `ITenantDissolutionService.DissolveAsync`; `ITenantRepository.DeleteTenantAsync` is deleted (or referenced from `WipeDataAsync` only); the tenant-axis canary gains the invitation-accept solo-dissolve leg. — `ArchitectureTests` caller scan + TB-AUTH-20/21, TB-BILL-28. — LB-AUTH-5 ≡ LB-BILL-19.
- **R124 [machine]** — Logout revokes the family of ANY known refresh token (valid, expired, rotated-out, revoked); only an unknown hash is a no-op. — TB-AUTH-18/19. — LB-AUTH-4.
- **R125 [machine]** — The client session carries an epoch: a refresh started before the latest `ClearSessionAsync`/`BeginImpersonation` is discarded on completion; impersonation is a state field, never an expiry-gated claim read; its expiry raises `IdentityChanged` instead of renewing. — TB-UI-22/23/24. — LB-UI-11/12/13.
- **R126 [machine]** — Client token lifetime is server-relative (`expires_in` at receipt on the injected clock), never device clock vs `exp`; each bearer handler retries once after a refresh on 401; the chassis exposes a clock-offset seam. — TB-UI-25/26/27. — LB-UI-14/15.
- **R127 [machine]** — Invitation validity is one predicate (`Pending && ExpiresAt > now`) on the entity, used by the signup gate, the accept, the seat count and the refresh-not-duplicate lookup; the seat-usage test seeds an expired pending invite. — TB-AUTH-22/23 + arch grep. — LB-AUTH-6/7 ≡ LB-BILL-20/25.

## 3. Tenancy / erasure completeness / RLS (Critical)
- **R90 [machine]** — Outbox payload lifecycle: `Payload` is scrubbed when a row leaves `Pending`; a scheduled job purges terminal rows older than `Outbox:RetentionDays`; **the `admin.broadcast` handler writes a system-scope audit row before the scrub applies** (C14). — `OutboxProcessorTests` + job test + TB-JOBS-12. — JOBS-2, ADV-P4-7.
- **R91 [machine]** — Every outbox enqueue carrying per-user or per-tenant content stamps `TenantId` (the broadcast is the single allowlisted tenant-less origin, by name); tenant dissolve wipes that tenant's pending rows via an `OutboxDataContributor`; the R43 canary reads `OutboxMessages WHERE TenantId = t` after dissolve. — TB-TEN-20/21/22, TB-JOBS-10. — JOBS-2, ADV-P4-7.
- **R129 [machine]** — `ITenantContext.EnterTenant(x)` where `x` originates off the wire is preceded by a tenant-existence check in the same transaction; an event for an unknown/dissolved tenant creates no rows. — TB-BILL-32 + arch scan. — LB-BILL-23.
- **R145 [machine]** — A new data-bearing column or table ships its lifecycle spec in the same PR (dissolve, erasure, export exclusion; stamping rule + allowlisted tenant-less origins for nullable-`TenantId` tables). — canary extension + `<Entity>_Lifecycle_*` test per such migration. — TB-TEN-20..23.
- **R146 [machine]** — Every `OtherTenant`/`CrossTenant`/`IsTenantScoped` test seeds two tenants through the shared `TwoTenants.SeedAsync` helper; a random-GUID "unknown" arrange under such a name fails an arch scan. — TB-TEN-16/17, TB-DOC-11. — the vacuous `Replay_UnknownOrOtherTenant_ReturnsFalse` class.
- **R150 [machine]** — Every migration ships a Down-on-data walk (seed at N−1, Up, mutate, Down, assert survival; a column-dropping Down states its data loss). — `MigrationsTests` extension (TB-TEN-27). 
- **R151 [review→machine]** — Pre-auth cross-tenant reads are proven narrow with a recording double; each `TagWith(RlsTags.CrossTenant)` site is paired with a test naming it. — `ServiceHarness` `Recording<T>` (TB-TEN-11/12).

## 4. Admin / config gates / billing
- **R85 [review]** — A deliberate exception to a numbered R-rule is written into this file (rule text + pinning test) in the same PR as the ADR that argues it. — PR-template checkbox. — AUTH-11, UX-18.
- **R86 [machine]** — A config-gated HTTP surface is proven absent by the **route table**, by **prefix**: with the gate bound from empty config, no endpoint of any kind (controller, `MapGroup`, standalone `Map*`) whose `RoutePattern.RawText` starts with the gated prefix (`/api/billing`, `/api/webhooks`, `/api/apikeys`, `/api/public`) exists in `EndpointDataSource`; a slice may not map under a platform-gated prefix; ADR-021's admin billing writes are gated with the billing surface; the narrowed GAP-1 Stripe fail-fast rests on this proof. (Absorbs R156.) — `GatedSurfaceTests` over BILLING/PUBAPI/HOOKS. — BILL-2, BILL-3, ADV-P4-6, C10.
- **R87 [machine]** — A config gate has exactly one read site. — `DocAndConfigSyncTests` regex. — BILL-4.
- **R88 [review→machine]** — Catalog numbers are read from `PlanCatalog`, never copied; QA prose expresses counts relative to the cap. — doc-grep gate. — BILL-6.
- **R122 [machine]** — The empty-config posture gate is reflective over every `*Settings.Enabled` (and nullable-endpoint gates), with the R53 exceptions listed by name. — `ConfigPostureTests`. — S0-G8, AUTH-11, ADV-P4-15.
- **R128 [machine]** — Provider-mapped fields fail safe **and loud**: an unmapped price, an absent/`default` period end, or a livemode mismatch never becomes a projection value; the event is acknowledged-not-applied with a log naming the field (extends R64 to the webhook leg). — `StripeBillingProviderTests` over signed fixtures. — LB-BILL-21/22/27.
- **R133 [machine]** — A scheduled job that notifies and records that it notified does both in one explicit transaction; job/notifier tests wire the real outbox-backed `IEmailSender`. — TB-BILL-33 + grep gate. — LB-BILL-24.
- **R134 [machine]** — Persisted date/period keys use `CultureInfo.InvariantCulture`. — grep gate + TB-BILL-35. — LB-BILL-26.
- **R153 [machine]** — The config-catalog gate reflects over every `*Settings.SectionName` and single-segment section consts; `GetSection(<Ident>.SectionName)` counts as a read of that section (extends R95). — `DocAndConfigSyncTests`. — ADV-P4-10.
- **R154 [machine]** — The integration harness exposes a per-gate seam; `PostmanParityTests` enumerates the route table with every reflected gate ON; `[ModuleInitializer]` env switches in tests are banned. — ADV-P4-14.

## 5. Jobs / email / webhooks / observability
- **R89 [machine]** — Persisted diagnostics that a `*Response.From` maps to a client hold an enumerated reason code, never `Exception.Message` (machine half of R16). — `EnforcementGateTests` scan + `WebhookDeliveryLogTests`. — JOBS-1.
- **R92 [machine]** — Enqueue-time validation is at least as strict as dispatch-time parsing (media type grammar, address, safe basename with no control chars and a length cap). — `EmailAttachmentTests` + `OutboxEmailTests` zero-row theories. — JOBS-3/4, ADV-P4-8.
- **R93 [machine]** — Log state carries identifiers only: `{Email}`/`{To}`/`{Token}`/`{Code}`/`{Otp}`/`{Secret}`/`{Password}`/`{Body}` placeholders are banned in `src/**`. — `LogTemplates_NeverCarryPii`. — OBS-1, ADV-P4-16.
- **R95 [machine]** — The config-catalog gate captures flat indexer reads and the `OTEL_*` variables; the exporter protocol is set from the same value the path logic reads. — `DocAndConfigSyncTests` + `TelemetryLogsExportTests`. — OBS-3.
- **R96 [machine]** — Free-text diagnostics written to a `HasMaxLength` column are truncated at the write site **with the one shared rune-safe helper** (R135). — scan. — JOBS-5.
- **R130 [machine]** — Outbound HTTP to user-supplied URLs: `AllowAutoRedirect = false`, the connection is made to the address the guard vetted (connect-callback pinning), **any 3xx is a failed delivery (never the redirect target's status)**, and a guard refusal or parse failure is a **permanent** failure that dead-letters on the first attempt. (Absorbs R94.) — registration scan + TB-JOBS-1/2/3/17. — JOBS-9, LB-JOBS-1/2/3, ADV-P4-9.
- **R131 [machine]** — Outbox attempt accounting is durable independent of the handler and of any second transaction: counter and next-attempt time advance in the **claim** statement; dead-lettering decided at claim time; terminal rows carry a terminal timestamp. — TB-JOBS-7/8. — LB-JOBS-7.
- **R132 [machine]** — A limit mirroring an external cap is checked in the external cap's unit (built MIME wire bytes); attachment count and inline-image bytes are capped; `MaxTotalBytes` documents its derivation. — TB-JOBS-4. — LB-JOBS-4.
- **R135 [machine]** — One shared rune-safe `Truncate`; backoff exponents clamped. — grep + TB-JOBS-9. — LB-JOBS-8.

## 6. Deploy / CI / supply-chain
- **R97 [machine]** — The change classifier is safe-by-default: every path an arch test or CI gate reads is `code`; docs only by explicit allowlist. — positives + reflective read-path test. — DEP-15.
- **R98 [machine]** — No checkout leaves the job token behind: `persist-credentials: false` on every checkout unless allowlisted with a reason; no token to third-party code. (Amended by ADR-031: the branch-protection clause and the Forgejo protection script are retired — private repos on GitHub Free have no branch protection, by decision.)
- **R99 [machine]** — Every container image reference (Testcontainers, compose, workflow `services:`/`docker run`, Dockerfile `FROM`) carries at least `major.minor` or a digest. — widened image-pin gate. — DEP-18.
- **R100 [review]** — Operator procedures in `DEPLOYMENT.md` are executable by the scripts they name; runbook numbers are stated once. — PR checkbox + knobs test. — DEP-16/25.
- **R101 [review]** — A deploy push to a mirror never silently buys paid runner minutes: the mirror's Apple legs/smokes sit behind a `vars.` knob; "public/free" cost claims re-verified on visibility change. — DEP-17.
- **R136 [machine]** — Every CI step computing a verdict in shell/jq/PowerShell/python/JS has a fixture case in `tests/ci-logic/` that runs the **committed text** on the Linux `build-test` leg; the verdict-step inventory equals the fixture set. — `CiShellLogic_PassesItsFixtures`. — LB-DEP-1/3/4/5/6/7/9/10.
- **R137 [machine]** — Path classification is byte-safe: `git -c core.quotePath=false diff --name-only` (or `-z`) in the workflow, with a non-ASCII fixture. — LB-DEP-2.
- **R138 [retired]** — Retired 2026-10-02 (ADR-031) with the Forgejo already-green deploy it governed. Was: Matrix completeness is derived: the already-green deploy refuses any non-success task under a matrix prefix (`success|skipped` for smokes), takes the newest task per job, and expected leg counts are parsed from `ci.yml` (refines R98). — TB-DEP-3/10. — LB-DEP-3, DEP-19.
- **R139 [machine]** — Log-grep verdicts match the status **field** (`providers - 200 - `) in both probe sites (bash + PowerShell). — TB-DEP-6. — LB-DEP-4.
- **R140 [machine]** — Operator PowerShell under `tools/`: `#Requires -Version 7.0`, ASCII or BOM UTF-8, `$ErrorActionPreference='Stop'`, non-zero exit on every failure path; pwsh harness with fakes. — TB-DEP-9. — LB-DEP-8, DEP-13.
- **R143 [machine]** — The classifier's fail-open branch sets every output permissive. — TB-DEP-4. — LB-DEP-9.

## 7. Native
- **R102 [machine]** — Both hosts attach `Authorization: Bearer` only to requests whose origin equals the API base origin; third-party absolute URLs use a plain client. — shared `BearerScopedHandler` + `Ui.Tests` + arch ban. — NAT-12.
- **R103 [machine]** — CI exercises an Android **Release** build (dispatch + Monday) asserting `apksigner verify` v2/v3, the guard fires without `ApiBaseUrl`, and the Release APK carries the HTTPS-only config; `publish-native.ps1` fails unless verified (machine half of R67). — job in both copies + gate. — NAT-13/14/18.
- **R104 [machine]** — Signing material never in git (`.gitignore` patterns); passwords from env, never `-p:` literals in docs/scripts. — gitignore + doc-grep gates. — NAT-15.
- **R105 [machine]** — Android shell posture gate: `allowBackup="false"` (or backup rules), `networkSecurityConfig` set, no cleartext, no `debuggable`; Release config forbids cleartext. — `NativeShellGateTests`. — NAT-17.
- **R106 [machine]** — The `native` classifier regex is asserted positively per R60 file class (incl. `Directory.Build.props` and any script a native leg runs). — LB/NAT-16, S0-G6.
- **R141 [machine]** — Release-only MSBuild guards share ONE predicate (`!= 'Debug'`), validate values (trimmed, `https://`, origin-only), resolve a keystore on every host or error, and live in an importable `.targets` evaluated by a workload-free probe in `EnforcementGateTests`. — TB-NAT-2/3. — LB-NAT-1/2, NAT-13/18.
- **R142 [machine]** — Each `wwwroot/js` bootstrap file defines its public object before registering listeners, guards feature-gated APIs, and has a `node --test` stub test. — TB-NAT-4/5. — LB-NAT-3, NAT-19; machine half of R110.

## 8. Client / test-completeness / harness
- **R109 [machine]** — E2E full navigations go through `BlazorBoot`; a boot retry reloads the landed URL, never re-issues the original navigation; only network verdicts retry; retries are budgeted per shard on the **deduplicated** attempt set. — grep gate + Slowest-journeys threshold (both copies). — UX-8/9, DEP-21.
- **R110 [review]** — A `wwwroot/js` security control ships a behaviour test; a substring test is a presence gate and is named so. — UX-10 (R142 supplies the unit half).
- **R113 [machine]** — Sharding excludes `[Explicit]` fixtures by category; `e2e.md`'s suite size is asserted against the non-explicit `[Test]` count (CI-shape assertion). — TB-DOC-1. — UX-15.
- **R144 [machine]** — Coupled client+server changes ship one joint-invariant test reading both constants. — `ConfigPostureTests` cross-project fact + PR line. — UX-6/7, AUTH-1.
- **R147 [machine]** — A gate-off E2E lane exists for every deployment-config gate, in both workflow copies. — TB-DOC-2. — TB-UI-66/67/68, BILL-10.
- **R148 [machine]** — RCL components take the clock they schedule with (`TimeProvider`); the clock gate covers `src/Shared.Ui`. — TB-UI-65. — TOOL-7/8, C24.
- **R149 [machine]** — Test-id contract: every `data-testid` in `src/Shared.Ui` is referenced by a test or QA case and vice versa. *(Amended 2026-10-06, Arch A12 #371:* a reusable component may take its id as a `[Parameter] string TestId` and derive suffixed ids from it (`@($"{TestId}-input")`); every caller passes a literal, and the ids that exist — the callers' literals with the component's suffixes — are the ones a test or QA case must use. Any other computed id is refused.*)* — TB-DOC-5; `TestIdContractTests` with its fixtures.
- **R155 [machine]** — Route uniqueness is enforced at startup (duplicate method+pattern throws), not only in CI. — ADV-P4-13.
- **R157 [machine]** — Source-scan gates matching a namespace/prefix use a boundary, with an `X`/`X2` self-test. — ADV-P4-12.

## 9. Docs / course / rule hygiene / template
- **R83 [machine]** — Every `ErrorResponse("<code>")` and `?error=<code>` literal reachable from an action appears in that request's Postman description. — `PostmanParityTests` code parity. — AUTH-9, TR-21.
- **R114 [machine]** — `docs/tutorial/COVERAGE.md` equals the generator's output and reports 0 unmapped — CI step beside `qa-artifacts`, never code-gated. — TR-19 (the former R79 intent).
- **R115 [machine]** — Lesson code quotes are current and no bucketed file is an orphan (quote sweep + basename-in-prose check in `gen_coverage.py`). — TR-12/16/17/18/22/23/29.
- **R116 [machine]** — Rule-id hygiene: every `R\d+` cited in tests is a final rule; the binding file's stated range covers every rule present; `-cand` ids never appear in test comments. — `EnforcementGateTests`. — TR-15.
- **R117 [review]** — Deploy-trigger wording names the forge ("Forgejo dispatch `deploy=…`"); grep gate on `auto-deploys` outside DEPLOYMENT §10. — TR-13/14.
- **R118 [machine]** — The doc-map gate covers `docs/**/*.md` minus an allowlist and requires rows for `docs/tutorial/` and `docs/qa-runs/`; CLAUDE.md's "Read before you act" carries the course-reconcile rule. — TR-24.
- **R119 [machine]** — Postman gate/refusal description floor (gated controllers mention the gate key; refusing actions mention `signup_not_allowed`). — TR-21.
- **R120 [machine]** — Compiled-in limits are listed in `.env.example`'s "Not configurable" block. — TR-25.
- **R121 [machine]** — Diagram currency: ARCHITECTURE names every public class in `Services`/`Configuration`/`Observability`; FLOWS names every auth `ErrorResponse` code. — TR-20.
- **R152 [machine]** — Resource keys are unique per resx and slice-prefixed; MSB3568 promoted to an error (scoped). — ADV-P4-11.
- **R158 [machine]** — The add-a-slice checklist is gate-verified against every artifact a gate forces; ADR-004 states the touchpoints as a list. — ADV-P4-17.

## 10. Architecture · horizontal platform, vertical apps (2026-10)
- **R159 [machine]** — A slice is composed from app-owned files (`src/Api/AppComposition.cs`, `AppDbContext.App.cs`, `tests/Api.Tests/App/**`, `RulesEnforcement.App.cs`, `tests/Ui.Tests/App/**`); no platform composition file names a slice or an app type, so `Program.cs`, `AppDbContext.cs`, the architecture gates and the test chassis are identical in the platform and its apps. — Arch A1 (#362): `OnlyAppComposition_ReferencesFeatureNamespaces_FromOutsideFeatures`, `CompositionFiles_AreFreeOfTheSampleSlice`; downstream, A2's manifest gate.
- **R161 [machine]** — The platform publishes the shape its migrations build for its tables (`platform-schema.json`: columns, constraints, indexes, RLS policies, as Postgres reports them, extracted from the migrated database); every repo's migrated database matches it for those tables. — Arch A5 (#365): `MigratedDatabase_MatchesThePlatformSchema` (on the platform also the file's currency check; regenerate with `PLATFORM_SCHEMA_WRITE=1`).
- **R162 [machine]** — One owning slice writes an entity, declared in `AppAllowlists.EntityWriters`; every other slice reads it or goes through a Core contract the owner implements; platform entities are written by platform services, never by a slice. — Arch A8 (#367): `EveryEntity_HasOneWritingSlice` (`SliceWriteInspector` with its self-test).
- **R160 [machine]** — Every tracked file has an ownership class in `platform-ownership.json` (platform / adapts / app / sample); downstream, a `platform` file matches the manifest of the stamped platform commit (`tests/Api.Tests/App/platform-manifest.json`, written by `tools/port-platform.ps1`) or is listed in `PlatformDivergences.json` with a reason; a port updates the stamp and the manifest together; `/api/version` reports the platform commit. — Arch A2 (#363): `OwnershipMap_ClassifiesEveryTrackedFile`, `PlatformFiles_MatchTheStampedManifest_OrAreAllowlisted`, `PlatformStamp_MatchesTheManifest`, `PortTool_MergesPlatformAndAdaptsFiles_AndWritesTheManifestTheGateAccepts`.

## Standing TDD mandate (review, carried from R7/CONTRIBUTING, restated with v4 evidence)
No production code without the failing test at the right layer; a slice ships happy-path + permission-denied +
**two-tenant** isolation (R146) before "done"; every public method tested per branch **and per error path**; a test
whose name promises a negative arranges the negative; a coupled change pins its joint invariant (R144); a new
data-bearing column ships its lifecycle spec (R145); the harness is reused, not rebuilt (the DB-fault injector and
concurrency runner are built once, in B8); QA plan + PDFs and the course lesson that quotes the changed code are
reconciled in the same PR (R114/R115); run log append-only.

---
*R1–R35 (v1.0) + R36–R76, R80 (v2.0, amended §1) + R81–R158 minus {R94, R156} (v4) = FOUNDATION_RULES v3.0;
+ R159–R162 (Architecture A1, A2, A5 and A8, 2026-10-06/07).
Retired numbers: R77, R78, R79, R80, R94, R138, R156.*
