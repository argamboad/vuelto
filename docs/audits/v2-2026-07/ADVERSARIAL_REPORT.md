# ADVERSARIAL_REPORT.md — v2 Phase 4 (Adversarial "Build-a-Slice")

## SUMMARY

- **Commit SHA (base):** `84c7ad838c8e7cdc8c9bfb0c4cb939646025040e`; work on throwaway branch `audit/v2-phase4-adversarial` (branch tip before this pass: `10f3e67`, docs-only).
- **Method:** scaffolded a realistic tenant-scoped slice **Projects** TEST-FIRST by the documented mechanism (WAYS_OF_WORKING recipe + ADR-004 + cloning `Features/Notes`), then attacked the inherited tenancy/auth/authz/migration boundaries, then scaffolded a second stub slice **Widgets** to surface cross-slice composability defects. No core/platform code was changed to make an attack pass.
- **HEADLINE — forced central edits per slice: 5** (`Core/Entities/<X>.cs`; `AppDbContext` DbSet + `OnModelCreating`; the churned `AppDbContextModelSnapshot.cs` + a new EF migration; `Program.cs` DI + `MapX()`; `PostgresFixture` TRUNCATE list). This directly validates **TR-2** ("zero central edits" is really ~5) and **TR-3** (hand-maintained TRUNCATE registry). The migration/snapshot/entity trio is irreducible EF reality; the `Program.cs` registration and the fixture TRUNCATE line are the two *removable* ones.
- **Findings by severity:**
  - **Critical: 0 new breaches.** Every tenancy attack that reached the request path was blocked by **core** (write-stamping interceptor + global read filter + inherited auth policy), not by the slice remembering to do anything.
  - **High: 1 (new, confirmed by attack) — ADV-1: the write-side interceptor guards INSERT only; a foreign-tenant UPDATE/DELETE is not refused.** Reachable only via the escape hatch (`IgnoreQueryFilters`/`QueryAllTenants`), which is banned in `Features/**` — so it is defence-in-depth, not an open request-path hole, but the "write isolation is structural in both directions" claim is INSERT-only in fact.
  - **Medium: 2 — ADV-2** the `Features/**` arch-scan bans `IgnoreQueryFilters` but NOT `QueryAllTenants`, so a careless request-path slice can bypass tenancy and still pass CI (validates the R5 concern); **ADV-3** the 5-touchpoint slice contract with two removable central edits (TR-2/TR-3, re-measured precisely).
  - **Low: 1 — ADV-4** no guard prevents two slices from colliding on a `/api/<x>` route prefix or a table name (didn't occur here; nothing prevents it).
- **Rules added:** **R32–R35** in `FOUNDATION_RULES.md` (all machine-enforceable). **Conflicts logged:** 0 (Phase 4 entry appended to `RULE_CONFLICTS.md`; one TR-8/DOC-17 wording item flagged for Phase 5, not a conflict).
- **Suite:** Core.Tests **42/42**, Api.Tests **349/349** (335 baseline + 14 new Projects tests; Widgets exercised via the existing migration/arch tests), all green, no flakiness across repeated runs.
- **Foundation-readiness verdict:** **Ready with fixes.** The slice mechanism is genuinely sound — a carelessly-written slice could not breach tenant isolation in the request path, and auth/authz/dissolve/export are inherited for free. Ship-blockers remain the **prior Critical GAP-1** (not re-tested here; still must-fix) plus **ADV-1** (INSERT-only write guard) and the TR-1 harness dependency on the DELETE-ME Notes entity, which a generated app inherits.

> This pass wrote DISPOSABLE slice + test code only. It modified 4 central files and added 2 migrations, all reverted/removed by the caller. The exact file list is at the end.

---

## Forced-core-edit log (HEADLINE)

Every edit outside the two slice folders (`src/Api/Features/Projects/`, `src/Api/Features/Widgets/`) that the documented mechanism *forced*, with precise location and why:

| # | File | Edit | Forced by | Removable? |
|---|------|------|-----------|-----------|
| 1 | `src/Core/Entities/Project.cs` (new) | The EF entity implementing `ITenantScoped` | The entity is the EF model + migration source; WoW:56 says it lives in Core | No — irreducible (entity must be a real type Core owns) |
| 2 | `src/Infrastructure/Persistence/AppDbContext.cs:63` | `public DbSet<Project> Projects => Set<Project>();` | EF needs the set to build the model/migration | No — irreducible without assembly-scan of `IEntityTypeConfiguration<>` |
| 3 | `src/Infrastructure/Persistence/AppDbContext.cs:~289` | `builder.Entity<Project>(...)` in `OnModelCreating` (the 230-line method every slice edits — DEBT-4) | EF model config; the shared method is a merge hotspot | Partly — `IEntityTypeConfiguration<Project>` + `ApplyConfigurationsFromAssembly` would move it into the slice |
| 4 | `src/Infrastructure/Persistence/Migrations/20260701221509_AddProjectsThrowaway.cs` (+ `.Designer.cs`) | New EF migration via `dotnet ef migrations add` | The migration-drift test (`MigrationsTests.cs:37` `HasPendingModelChanges`) fails the build without it | No — irreducible EF fact |
| 5 | `src/Infrastructure/Persistence/Migrations/AppDbContextModelSnapshot.cs` | Snapshot churn (auto-mutated by `ef migrations add`) — a **central shared file two slices both touch** | Same as #4 | No — irreducible; but note it is a guaranteed merge conflict between two slices developed in parallel |
| 6 | `src/Api/Program.cs:166` + `:288` | `AddScoped<ProjectsHandler>()`, `AddScoped<ITenantDataContributor, ProjectsDataContributor>()`, and `MapProjects(app)` | DI + route registration; `Program.cs` is the per-epic dumping ground (DEBT-3) | Yes — assembly-scan of endpoint modules + contributors would remove it |
| 7 | `tests/Api.Tests/Infrastructure/PostgresFixture.cs:62` | Added `"Projects"` to the hardcoded TRUNCATE list | TR-3: forgetting it → silent cross-test state leakage / flaky absolute-count asserts | **Yes** — derive from `AppDbContext.Model.GetEntityTypes()` (R11) |

**Count: 5 conceptual touchpoints** (entity; DbSet+config; migration+snapshot; Program.cs DI+Map; fixture TRUNCATE) — exactly TR-2's claim. Two are removable (Program.cs registration, fixture TRUNCATE); the entity/migration/snapshot trio is irreducible under EF migrations.

**Where the docs' recipe was wrong/incomplete (validates DOC-18 / TR-2):**
- `WAYS_OF_WORKING.md:48` still shows `MapGroup("/api/<feature>").RequireAuthorization(...)` as the endpoint shape — but the pinned convention is `MapTenantFeatureGroup(...)` (the raw form is exactly what R6/DEBT-6 forbids). Following the recipe literally ships the banned idiom.
- The recipe's contributor bullet (`WAYS_OF_WORKING.md:54`) mentions only dissolve; `ITenantDataContributor` now **requires `ExportKey` + `ExportAsync`** (GDPR-1). A slice built from the recipe text alone **would not compile** — I had to copy `NotesDataContributor` to discover the two extra members. Confirmed DOC-18(b).
- The recipe omits steps 2 (`AppDbContext` config), 3 (migration), 5 (TRUNCATE) and the snapshot entirely — I only knew to do them by cloning Notes + reading the harness. Confirmed TR-2.
- `CLAUDE.md` golden rule 1 + `DATA_MODEL.md` say cross-tenant lookups "opt out with `IgnoreQueryFilters()`" — doing that inside a feature slice **fails the arch test** (proven below). Confirmed DOC-17.

Everything DID compile once I cloned Notes rather than followed prose — the exemplar is load-bearing (see Generation-scenario notes).

---

## Attacks (tried / blocked-or-not / by CORE vs SLICE)

### Tenancy

| Attack | What I tried | Blocked? | By CORE or SLICE? |
|---|---|---|---|
| (a) Omit the tenant stamp | Careless handler adds a `Project` with `TenantId` left default (`Guid.Empty`), saves under tenant A | **Yes** — row landed under tenant A, not tenant-less | **CORE** — `TenantStampingInterceptor.Stamp` stamps `currentTenantId` onto `Added` rows with empty `TenantId`. Slice did nothing. (`Attack_OmitTenantStamp_InterceptorStampsCurrentTenant` ✅) |
| (b) Forge/bind a foreign `TenantId` | Acting as A, add a `Project { TenantId = victim }` | **Yes** — `SaveChangesAsync` threw `InvalidOperationException("Refusing to persist …")` | **CORE** — interceptor refuses a foreign `TenantId` on an `Added` row (fail closed). (`Attack_ForgeForeignTenantId_InterceptorRefusesWrite` ✅) |
| (b') Read a foreign row by primary key | Attacker tenant calls `repo.Query().FirstOrDefault(p => p.Id == victimRowId)` | **Yes** — `null` (global filter hides it) | **CORE** — global query filter. (`Attack_ReadForeignRowById_GlobalFilterHidesIt` ✅) |
| (c) Chain `IgnoreQueryFilters` in the slice | Added `projects.QueryAllTenants().IgnoreQueryFilters()` to `ProjectsHandler.ListAsync`, rebuilt, ran the arch test | **Yes — build FAILS** | **CORE (build gate)** — `ArchitectureTests.FeatureSlices_DoNotBypassTheTenantFilter` failed: *"Feature code must not call IgnoreQueryFilters … Offenders: ProjectsHandler.cs"*. Reverted. **Actually run and observed red.** |
| (d) Bypass via raw `QueryAllTenants()` | Attacker tenant calls `new EfRepository<Project>(db).QueryAllTenants()` in the request path | **NOT blocked** — sees every tenant's rows (by design for the hatch) | **NEITHER at build time** — the arch scan matches only the literal `IgnoreQueryFilters`, NOT `QueryAllTenants`. A careless request-path slice using `QueryAllTenants()` compiles, passes CI, and leaks. → **ADV-2 / R33** (sharpens R5). (`Attack_EscapeHatch_SeesEveryTenant_ByDesign` ✅ documents the by-design read) |
| Foreign **UPDATE** under wrong tenant | Attacker loads a victim row via `IgnoreQueryFilters()`, sets `Name="HIJACKED"`, saves | **NOT blocked** — the edit persisted | **NEITHER** — `TenantStampingInterceptor` inspects only `EntityState.Added` (`:53`); `Modified`/`Deleted` foreign entries are unchecked. → **ADV-1 / R32 (High)**. Reachable only through the hatch (banned in Features), so defence-in-depth, but the write guard is INSERT-only in fact. (`Attack_UpdateForeignRow_UnderWrongTenant` ✅ — asserts the row WAS hijacked, documenting the gap) |

**Verdict:** write-stamping + global filter **hold even when the slice is written carelessly** for the normal request path (INSERT + all reads). Isolation did **not** depend on the slice remembering anything. The two gaps (ADV-1 foreign UPDATE/DELETE, ADV-2 `QueryAllTenants` not arch-banned) both require the escape hatch, which the Features arch-ban blocks for `IgnoreQueryFilters` — but `QueryAllTenants` slips that ban, so the two gaps compose into a real (if narrow) request-path write-leak a careless slice could ship.

### Auth / authz

Mounted the **real `MapProjects()` routes** in a `TestServer` with the real JWT policy, real `PermissionService`, real `HttpCurrentTenant`, real Postgres-backed `AppDbContext`, and minted real signed JWTs. (`ProjectsAuthzIntegrationTests`, 5 tests, all ✅.)

| Attack | Result | By CORE or SLICE? |
|---|---|---|
| Unauthenticated `GET /api/projects/` | **401** | **CORE** — `AuthPolicies.TenantApi` inherited via `MapTenantFeatureGroup`. Slice declared no `[Authorize]`. |
| Authenticated Member `GET` | **200** (happy path through the full stack) | Inherited policy + tenant claim → `HttpCurrentTenant` → global filter. |
| Member `DELETE` (permission-gated with `.RequirePermission(ManageMembers)`) | **403** with `{error:"forbidden", permission:"ManageMembers", …}` | **CORE** — `PermissionEndpointExtensions` filter + real `RolePermissions` matrix. Member lacks `ManageMembers`. |
| Owner `DELETE` on missing id | **404**, not 403 — permission seam passed, handler returned not-found | Confirms the 403 path is the permission gate, not an artefact. |
| Wrong-tenant token: A creates, B lists | B sees **empty**, A sees its own | **CORE** — global filter driven by the JWT `tenant_id` claim through the real HTTP pipeline. |

**Verdict:** `MapTenantFeatureGroup`'s inherited policy enforces **without the slice re-implementing any guard**; `.RequirePermission` yields the 403 path correctly. Claim #2 holds.

### i18n / config / migrations

- **Migration:** `dotnet ef migrations add AddProjectsThrowaway` composed cleanly; `MigrationsTests.Migrations_ApplyCleanly_AndModelHasNoPendingChanges` (real `MigrateAsync` on a fresh DB) stayed green — no drift, no broken migration.
- **Config:** the slice needed no new config key (correct — a plain tenant slice shouldn't). Nothing to collide.
- **i18n:** the slice's only user-facing string is a hardcoded English `BadRequest` message (`"A project name is required"`), mirroring the Notes wart (TR-5) — API-emitted messages are not localized. No resx entry was needed or added, so no i18n collision was possible to test at the API layer; this itself re-confirms **TR-5** (no per-feature resx convention; API errors unlocalized).

---

## Composability findings (second slice: Widgets)

Scaffolded a minimal second slice **Widgets** (`Widget` entity + `/api/widgets` route + its own migration `AddWidgetsThrowaway`) in the same namespace/prefix space as Projects.

- **Migrations do NOT misorder.** Timestamp-prefixed: `20260701221509_AddProjectsThrowaway` then `20260701221817_AddWidgetsThrowaway`. Both apply in order on a fresh DB (`MigrateAsync` green). ✅
- **Snapshot is a shared mutation point.** Both `ef migrations add` calls rewrote `AppDbContextModelSnapshot.cs`. Two slices built on parallel branches would produce a **guaranteed merge conflict** in the snapshot (and in `Program.cs` / `OnModelCreating`). Not a correctness bug — a workflow hazard feeding DEBT-3/DEBT-4/TR-2. → ADV-3.
- **Routes don't conflict** (`/api/projects` vs `/api/widgets`) — but **nothing prevents** two slices choosing the same prefix; ASP.NET would map both and dispatch ambiguously. No guard exists. → **ADV-4 / R35**.
- **DI order doesn't bite.** Both handlers and both `ITenantDataContributor`s register independently; contributors are consumed as `IEnumerable<ITenantDataContributor>`, so order is irrelevant and no slice shadows another.
- **i18n keys:** neither slice added a resx key (API-only, unlocalized), so no clash was possible — again re-confirming TR-5/TR-6 (no UI exemplar, no per-feature resx).
- **Tenant filter auto-applied to both** new `ITenantScoped` entities via the reflection loop in `OnModelCreating` — `EveryTenantScopedEntity_HasAGlobalQueryFilter` stayed green **without** editing the arch test. Strong positive: the every-entity-filtered guarantee scales to new slices for free.

---

## Suite results + harness readiness

- **Core.Tests:** 42 passed / 42 (Testcontainers not needed; pure unit).
- **Api.Tests:** **349 passed / 349** = 335 baseline + 14 new Projects tests (`ProjectsSliceTests` 4 + `ProjectsAttackTests` 5 + `ProjectsAuthzIntegrationTests` 5). Widgets added no dedicated test file; it is exercised by the existing `MigrationsTests` + `ArchitectureTests`.
- **Flakiness:** none observed. Re-ran the Projects+Migrations subset twice (15/15, then as part of 349/349) — stable. The structural per-test TRUNCATE (`PostgresTestBase`) held.

**Harness readiness (validates Phase-3 assessment):**
- **Reusable out of the box (strong):** `PostgresFixture` + `PostgresTestBase` gave a new backend slice happy-path + read-isolation + write-isolation testing **immediately**; `TestCurrentTenant` made "act as tenant X" trivial; `FakeTimeProvider` covered the injected clock. The `FeatureAuthorizationTests` `TestServer` pattern extended cleanly to a full auth/authz/permission integration host — I did **not** have to build a `WebApplicationFactory` from scratch.
- **Forced to touch a central list (TR-3):** I had to hand-edit the `PostgresFixture` TRUNCATE list for **both** slices — the single most error-prone step; forgetting it yields silent cross-test leakage. This is the harness's one real readiness defect for slice authors, exactly as Phase 3 flagged.
- **TR-1 confirmed by inspection:** the tenancy/GDPR/outbox harness is still load-bearing on `Note`/`NotesDataContributor` (`RepositoryScopingTests`, `TenantStampingInterceptorTests`, `EnterTenantScopingTests`, `Outbox/*`, `Gdpr/*`, `PostgresFixture` TRUNCATE). A generated app that deletes the sample per WoW:64 breaks these on day one. I did **not** need to rebuild the harness for Projects, but only because Notes still exists to anchor it.
- **`ServiceHarness` gap:** it covers auth/tenant/billing services but not a generic feature slice — I wired Projects' repo/handler by hand (cheap, since `IRepository<T>` + `EfRepository<T>` are open-generic). Fine for this slice; a slice needing MFA/notify/webhook wiring would still hand-assemble (Phase-3 point 5).

---

## Generation-scenario notes (structural)

- **Slice code is cleanly separable from core.** All Projects/Widgets *logic* lives in two self-contained `Features/<X>/` folders; the only entanglement with core is the 5 forced touchpoints above (entity in Core, DbSet/config/snapshot/migration in Infrastructure, DI/Map in Program.cs, TRUNCATE in the fixture). Deleting a slice = delete the folder + reverse those 5 — no logic bleeds into platform code. Coupling direction stayed clean (only `Program.cs` references `Features.*`).
- **The Notes exemplar is present and exemplary — and load-bearing.** I could only produce a *compiling* slice by cloning Notes, because the prose recipe is incomplete (missing `ExportKey`/`ExportAsync`, DbSet config, migration, TRUNCATE) and in one spot wrong (raw `MapGroup`). A generated no-upstream clone that **deletes Notes** (per WoW) loses both the exemplar and the harness anchor (TR-1) — the single highest-value thing to fix before generation: replace Notes-as-fixture with a harness-owned `TestWidget`, and keep Notes purely as a copy-me exemplar (or bake the full mechanism into a template/generator).
- **Scaffolding a generated clone would inherit unwantedly:** the `🗑️ DELETE-ME` Notes slice (entity, DbSet, migration `AddNotesSample`, DI, TRUNCATE) ships live; the PUBAPI/HOOKS endpoints sitting loose in `Features/` (TR-7/DEBT-6) are platform chassis a clone would mistake for deletable app features; and the app-flavoured Core constants (`PlanCatalog`, `WebhookEvents.Ping`) marked EXAMPLE. None block generation but each is inherited noise.

---

## Assurance-claims table

| # | Guarantee | Supported / Contradicted / Insufficient | Evidence | Verdict |
|---|---|---|---|---|
| 1 | No slice can access another tenant's data, even written carelessly | **Supported (reads + INSERT); Contradicted (foreign UPDATE/DELETE via hatch)** | Attacks (a),(b),(b'),(c) all blocked by CORE; but `Attack_UpdateForeignRow` shows the interceptor is INSERT-only (ADV-1/R32), and `QueryAllTenants` isn't arch-banned (ADV-2/R33) — a careless request-path slice could combine them to mutate a foreign row | **Holds conditionally** — holds fully for the normal path; fails for foreign UPDATE/DELETE reached through the un-arch-banned `QueryAllTenants`. Fix R32+R33. |
| 2 | Every slice route inherits auth/authz (RBAC seam) without re-implementing it | **Supported** | 401 unauth, 200 member, 403 permission-gated, 404 (not 403) for authorized-but-missing, wrong-tenant sees nothing — all through the real stack with zero guards in the slice | **Holds** |
| 3 | A slice can be added touching only slice code — zero core edits | **Contradicted** | 5 forced central touchpoints measured (headline log); 2 removable (Program.cs, TRUNCATE), 3 irreducible under EF (entity, config, migration+snapshot) | **Fails** as stated ("zero"); **Holds conditionally** if reworded to "slice folder + a bounded, documented set of 5 central touchpoints" (TR-2 fix). |
| 4 | A slice's migrations/config/i18n cannot collide with core or another slice | **Insufficient → partially Contradicted** | Migrations timestamp-ordered, applied clean, no drift (✅); but the shared snapshot/`Program.cs`/`OnModelCreating` are guaranteed parallel-branch merge points, and nothing prevents duplicate route prefixes or table names (ADV-4) | **Holds conditionally** — no *silent correctness* collision found; workflow/merge collisions and unguarded prefix/table duplication remain. Fix R35 + DEBT-3/4. |
| 5 | BILLING/ADMIN/GDPR cross-tenant paths (EnterTenant, impersonation, export/erasure) preserve isolation | **Supported** | Reasoned from code + Phase-3 verified-correct list; **exercised directly**: my `ProjectsDataContributor` auto-participates in dissolve/export/erasure via `IEnumerable<ITenantDataContributor>` and its Has/Export/Wipe are tenant-scoped by explicit `TenantId` (`Contributor_ReportsExportsAndWipesTenantData` ✅). Note the standing **SOLID-1** asymmetry: there is NO `IUserDataContributor`, so a *user*-keyed slice would escape erasure | **Holds** for tenant-scoped paths (the ones a slice participates in); the SOLID-1 per-user gap is a separate, already-filed High. |

---

## Foundation-readiness verdict

**Ready WITH FIXES.** The vertical-slice mechanism is fundamentally trustworthy: a slice written by a careless (or hostile) author **cannot** breach tenant isolation on the normal request path, and it inherits authentication, authorization, the permission seam, dissolve, GDPR export, and GDPR erasure with no platform edits. The composability of two parallel slices is correct (ordered migrations, auto-applied filters, order-independent DI). That is a strong foundation.

But it is **not** ready to generate the first real app until the must-fix list clears, because a generated clone inherits the defects:

**Must-fix before generating the first real app:**
1. **GAP-1 (Critical, prior — not re-tested here, still open):** default fake billing provider + anonymous always-mapped webhook = unauthenticated cross-tenant subscription write. Ship-blocker. (R1.)
2. **ADV-1 (High, new):** extend `TenantStampingInterceptor` to refuse foreign-tenant `Modified`/`Deleted` entries, not just `Added` — the "write isolation in both directions" claim is INSERT-only today. (R32.)
3. **ADV-2 (Medium, new):** add `QueryAllTenants` to the `Features/**` tenant-bypass arch-ban (allowlisting `*DataContributor.cs`) — closes the only request-path route to ADV-1 and the general careless-`QueryAllTenants` read leak. (R33; sharpens R5.)
4. **TR-1 (High, prior):** give the platform tests a harness-owned `ITenantScoped` fixture entity so deleting the DELETE-ME Notes sample (which the docs instruct) doesn't break the tenancy/GDPR/outbox guards; keep Notes as a pure copy-me exemplar. (R9.)
5. **TR-2 / TR-3 / DOC-18 (Medium, prior, re-measured):** publish the full 5-touchpoint "add a slice" checklist, fix the WAYS_OF_WORKING recipe so it compiles (`MapTenantFeatureGroup`, `ExportKey`+`ExportAsync`), soften ADR-004's "zero central edits," and derive the fixture TRUNCATE list from the model (removing 1 of the 2 removable touchpoints). (R10, R11, R34.)

Recommended-not-blocking: R35 (route/table collision guard), DEBT-3/DEBT-4 (assembly-scan DI/config to shrink `Program.cs`/`OnModelCreating`, removing the other removable touchpoint and the snapshot-merge pain), TR-5/TR-6 (per-feature resx + a UI exemplar), SOLID-1 (`IUserDataContributor`, so user-keyed slices don't escape GDPR erasure).

---

## Files created / modified by this pass (for throwaway cleanup)

**Created (slice + test code — safe to delete wholesale):**
- `src/Core/Entities/Project.cs`
- `src/Core/Entities/Widget.cs`
- `src/Api/Features/Projects/` (ProjectsModels.cs, ProjectsHandler.cs, ProjectsEndpoints.cs, ProjectsDataContributor.cs)
- `src/Api/Features/Widgets/` (WidgetsEndpoints.cs)
- `src/Infrastructure/Persistence/Migrations/20260701221509_AddProjectsThrowaway.cs` (+ `.Designer.cs`)
- `src/Infrastructure/Persistence/Migrations/20260701221817_AddWidgetsThrowaway.cs` (+ `.Designer.cs`)
- `tests/Api.Tests/ProjectsSliceTests.cs`
- `tests/Api.Tests/ProjectsAttackTests.cs`
- `tests/Api.Tests/ProjectsAuthzIntegrationTests.cs`

**Modified (central — revert to restore baseline):**
- `src/Api/Program.cs` (2 blocks: DI + Map for both slices)
- `src/Infrastructure/Persistence/AppDbContext.cs` (2 DbSets + 2 `OnModelCreating` configs)
- `src/Infrastructure/Persistence/Migrations/AppDbContextModelSnapshot.cs` (snapshot churn from both migrations)
- `tests/Api.Tests/Infrastructure/PostgresFixture.cs` (TRUNCATE list: added `"Projects", "Widgets"`)

**Audit docs (KEEP — these are the Phase-4 deliverables):**
- `docs/audits/v2-2026-07/ADVERSARIAL_REPORT.md` (this file)
- `docs/audits/v2-2026-07/FOUNDATION_RULES.md` (appended R32–R35)
- `docs/audits/v2-2026-07/RULE_CONFLICTS.md` (appended the Phase-4 entry)

The simplest reset: `git checkout -- src/Api/Program.cs src/Infrastructure/Persistence/AppDbContext.cs src/Infrastructure/Persistence/Migrations/AppDbContextModelSnapshot.cs tests/Api.Tests/Infrastructure/PostgresFixture.cs` and delete the created files/folders + the two migration pairs. The whole branch `audit/v2-phase4-adversarial` is disposable except the three audit docs.
