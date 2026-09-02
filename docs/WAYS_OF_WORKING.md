# Ways of Working

> The process layer: how work is sliced, how user stories and PRs are written, and naming
> conventions. This is constant across projects from this platform; project-specific examples are
> marked. Referenced by `CLAUDE.md` so Claude Code follows it.

## Slices (the unit of build work)

A **slice** is a thin, vertical, end-to-end increment that delivers one coherent piece of user
value and leaves the app working. Vertical means it cuts through all layers as needed (API →
Core/derived rules → Infrastructure/EF → Shared.Ui components → Web), rather than building one
horizontal layer in isolation.

**Principles**
- **Vertical, not horizontal.** Prefer "user can toggle inventory availability" (touches DB, API,
  UI) over "build all the database tables."
- **Small enough to finish.** A slice should be completable and mergeable on its own — roughly a
  few focused sessions, not weeks. If it can't be described in one or two sentences, split it.
- **Working state after each slice.** Every merged slice keeps the app runnable. No half-wired
  features on the main branch.
- **Foundational slices first.** Some early slices are enabling (auth + tenant scaffolding, the
  data model + first migration). These are still vertical where possible.
- **One epic = a group of related slices.** User stories are written per-epic at the start of that
  epic (not all upfront).

**Slice lifecycle**
1. Pick the next slice (from the project roadmap / backlog, if one exists).
2. Write/refine the user story/stories for it (see template below) under `docs/stories/`.
3. **Write the tests first (TDD).** Unit tests for Core logic; E2E tests for the user-facing flow.
4. Branch, implement until all tests are green; refactor.
5. Open a PR using the PR template; self-review against acceptance criteria.
6. Merge; app remains in a working state. Add ADRs to `DECISIONS.md` for any decisions made.

## Code organization — clean platform + vertical feature slices

Two senses of "vertical" apply, and they're complementary:

- **Delivery-vertical slices (above):** the *unit of build work* — each increment cuts through the
  layers it needs and leaves the app working.
- **Organization-vertical feature folders:** *where the code lives*. The reusable **platform** stays
  horizontal / clean-layered (Core, Infrastructure, and the auth/tenancy controllers), while each
  **app feature** lives in one self-contained folder: `src/Api/Features/<Feature>/`.

This hybrid is pinned in **ADR-004**. The platform is the durable chassis (JWT auth, membership
tenancy, the global tenant query filter, email, persistence); features bolt on and *reuse* it.

**A feature slice** (`src/Api/Features/<Feature>/`) typically contains:
- `<Feature>Endpoints.cs` — a minimal-API group registered via
  **`app.MapTenantFeatureGroup("/api/<feature>")`** (NOT a raw
  `MapGroup(...).RequireAuthorization(...)` — the helper applies the shared `AuthPolicies.TenantApi`
  policy so a slice can't forget auth), called from `app.Map<Feature>()` in `Program.cs`. Gate
  individual endpoints with the **`.RequirePermission(Permission.X)`** (→ 403; ADR-009) and
  **`.RequireEntitlement(...)`** (→ 402; ADR-006) endpoint filters as needed. Features are minimal-API
  groups; the platform stays controllers.
- `<Feature>Handler.cs` — the orchestration/logic; injects `IRepository<T>` (whose `Query()` is
  already tenant-filtered), `ICurrentTenant`, `IUnitOfWork`, and platform services as needed.
- `<Feature>Models.cs` — request/response DTOs + validation, co-located.
- `<Feature>DataContributor.cs` — an `ITenantDataContributor` so the feature's data participates in
  tenant export **and** dissolve (registered in DI; no central wipe method to edit). It **requires
  four members**: `HasDataAsync`, `WipeAsync`, **`ExportKey`** (the section name in a tenant export),
  and **`ExportAsync`** (GDPR-1, ADR-011). A slice built from the old two-method recipe won't compile.
- The **entity** lives in `src/Core/Entities/` (it's the EF model + migration source) and implements
  **`ITenantScoped`** so the global query filter scopes it automatically.

**A feature must NOT** reach into another feature's folder, edit a central "has data / wipe data"
method, author a bespoke per-entity repository (use `IRepository<T>`), use `IgnoreQueryFilters()`
(banned in `src/Api/Features/**` — use `IRepository<T>.QueryAllTenants()`), or inline UI in the Web
app (UI components go in the Shared.Ui RCL).

**Add-a-slice mechanical checklist:**
1. **Entity** → `src/Core/Entities/<Entity>.cs`, implementing `ITenantScoped`.
2. **DbSet + config** → add the `DbSet<>` to `AppDbContext` and any `IEntityTypeConfiguration`.
3. **Migration** → `dotnet ef migrations add Add<Entity>` (in `src/Infrastructure/Persistence/Migrations/`).
4. **RLS policy — same migration** (ADR-020, v3 audit TR-4): `dotnet ef migrations add` scaffolds
   **no RLS DDL**, so append the new table's policy to the migration you just created —
   `migrationBuilder.Sql(...)` with the statements from `RlsDdl.StatementsFor` (copy the shape from
   the platform's RLS migration; `Down` drops the policy + disables RLS). The
   `RlsMigrationGateTests` parity gate fails CI on any `ITenantScoped` table whose policy didn't
   arrive by migration — this step is why it stays green.
5. **DI wiring** → register the handler/services (`Add*`) and map the group (`app.Map<Feature>()`) in
   `Program.cs`.
6. **Contributor** → register the `ITenantDataContributor` (all four members) in DI.
7. **Fixture reset** → add the new table(s) to the test fixture's reset/truncate list.
8. **UI** → nav entry + component in the Shared.Ui RCL, and add the resx (`.resx`) strings (EN/ES).
   **Namespace your keys per feature** (`Notes_Title`, `Notes_Empty`, …) — `AppStrings.resx` is one
   shared file, and unprefixed keys (`Title`, `Empty`) collide across slices (v3 audit / Phase-4 obs).

**Reference:** `src/Api/Features/Notes` is a complete, working example (marked "🗑️ DELETE-ME").
Copy its shape; delete it when you ship your first real feature.

## User stories

Stories live in `docs/stories/`, **one file per epic** (e.g. `docs/stories/inventory.md`),
multiple stories per file. They translate `FEATURES.md` behavior into intent + testable
acceptance criteria. **Acceptance criteria use Gherkin (Given/When/Then).**

### Story template

```markdown
### <STORY-ID> — <short title>

**As a** <role / tenant member>
**I want** <capability>
**So that** <benefit>

**Context / notes:** <optional — links to FEATURES.md flow, DATA_MODEL rule, constraints>

**Acceptance criteria**

Scenario: <name of the scenario>
  Given <initial context>
  And <additional context>
  When <action>
  Then <expected outcome>
  And <additional outcome>

Scenario: <another scenario — include edge cases and the unhappy path>
  Given ...
  When ...
  Then ...

**Out of scope:** <what this story explicitly does NOT cover>
**Definition of done:** tests written first (TDD); all unit + E2E scenarios green; Core logic
unit-tested; E2E covers happy + key unhappy paths; tenant-scoping verified; merged, app working.
```

### Story ID & naming
- **Story ID format:** `<EPIC>-<n>` where `<EPIC>` is a short uppercase epic key and `<n>` is a
  sequential number. Example epic keys: `AUTH`, `TENANT`, plus project-specific ones.
  e.g. `AUTH-1`, `TENANT-3`.
- **Story file naming:** `docs/stories/<epic-lower>.md` (e.g. `docs/stories/auth.md`).
- Keep epic keys registered at the top of each story file so they don't drift.

## Branches, commits, PRs

### Conventional Commits
All commits and PR titles follow **Conventional Commits**:

```
<type>(<scope>): <description>
```

- **Types:** `feat`, `fix`, `docs`, `refactor`, `test`, `chore`, `perf`, `build`, `ci`.
- **Scope (optional but encouraged):** the area/epic, e.g. `feat(inventory): ...`,
  `fix(auth): ...`.
- **Description:** imperative, lowercase, no trailing period. e.g.
  `feat(inventory): add availability toggle endpoint`.
- Breaking changes: append `!` (`feat(api)!: ...`) and explain in the body.

### Branch naming
```
<type>/<epic-or-scope>-<short-desc>
```
e.g. `feat/inventory-availability-toggle`, `fix/auth-token-refresh`. Optionally include the story
ID: `feat/INV-3-availability-toggle`.

### PR naming
PR title = a Conventional Commit line, ideally referencing the story:
`feat(inventory): availability toggle (INV-3)`.

### Merge discipline (CI before merge)
- **Never merge before the branch CI finishes green.** Two real incidents drove this rule:
  PR #137/#138 were merged while their branch runs were still executing and a flaky theme E2E
  slipped onto develop (fixed in #139), and the 2026-07-14 toolchain drift (#142/#143) was
  diagnosed slower because merges had outpaced their runs.
- **Branch-green is necessary, not sufficient — develop can still fail after a green branch run:**
  1. **Toolchain drift** — CI floats on `dotnet-version: 10.0.x` and the GitHub runner images
     rotate weekly; an SDK/Xcode rollout can land *between* the branch run and the merge
     (2026-07-14: NU1004 locked-mode restore + an Xcode/workload mismatch, from one SDK patch).
     The fix playbook lives in `CLAUDE.md` → Tech stack.
  2. **Develop-only jobs** — the Apple builds/smokes run only on develop pushes (the
     `native-paths` gate; macOS bills 10×), so an Apple-affecting change is first *proven* by the
     post-merge run. Watch that run to completion; don't stack the next merge onto an unverified
     one.
- **Recommended repo setting:** GitHub branch protection on `develop` requiring the `build-test`
  and `e2e` status checks (Settings → Branches → Add rule, or
  `gh api repos/{owner}/{repo}/branches/develop/protection`). This makes "merge before CI
  finishes" impossible at the platform level instead of relying on habit. (Not enabled by
  default in this template — it needs repo admin and blocks solo-maintainer hotfix pushes to
  develop, so opt in per deployment.)
- After merging, `deploy-staging` only runs off a fully green develop run — a red develop
  silently **freezes staging** at the last good commit, so a broken develop is not a
  "fix it later" state.

### PR template
Stored at `.github/pull_request_template.md` (auto-loaded by GitHub). Contents:

```markdown
## Summary
<what this PR does, in 1–3 sentences>

## Related
- Story: <STORY-ID> (link)
- ADRs added/affected: <ADR numbers or "none">

## Type
- [ ] feat  [ ] fix  [ ] docs  [ ] refactor  [ ] test  [ ] chore  [ ] perf

## Acceptance criteria
- [ ] All Gherkin scenarios for the story pass
- [ ] Edge / unhappy-path scenarios covered

## Checklist
- [ ] Tests written first (TDD) — no production code without a failing test
- [ ] Unit tests green (Core.Tests, Api.Tests)
- [ ] E2E tests green (E2E.Tests) — happy + key unhappy paths covered
- [ ] Vertical slice — app is in a working state
- [ ] Tenant-scoping enforced (no cross-tenant leakage)
- [ ] Core derived-rule logic unit-tested (if touched)
- [ ] UI components added to Shared.Ui (not inline in Web)
- [ ] No direct UI→DB access (goes through the API)
- [ ] Latest stable deps; no preview packages
- [ ] Docs updated (FEATURES / DATA_MODEL / DECISIONS) if behavior or decisions changed

## Notes
<anything reviewers/future-you should know>
```

## Testing strategy (TDD — constant)

**Test-Driven Development is the default on every slice.** Write the failing test first; only
then write the production code that makes it pass; then refactor. No production code is written
without a test that drove it.

### Red-Green-Refactor
1. **Red** — write a failing test derived from the Gherkin scenario.
2. **Green** — write the minimum production code to pass it.
3. **Refactor** — clean up without breaking the tests.

### Test layers

| Layer | Project | Framework | What it covers |
|-------|---------|-----------|----------------|
| Unit | `tests/Core.Tests` | xUnit | Domain logic, derived rules, entity invariants |
| Unit / Integration | `tests/Api.Tests` | xUnit (+ Postgres Testcontainer) | Services, repositories, feature slices, tenancy invariants |
| E2E | `tests/E2E.Tests` | Playwright (NUnit) | Critical user flows through a real browser |

### Unit tests (`Core.Tests`, `Api.Tests` — xUnit)
- One test class per production class; file mirrors the source tree.
- Cover every derived rule, happy path, unhappy path, and tenant-scoping boundary.
- Tests that exercise relational behavior (EF global query filters, `ExecuteUpdate`/`ExecuteDelete`,
  transactions/savepoints) run against a real **PostgreSQL Testcontainer** — see
  `tests/Api.Tests/Infrastructure/PostgresFixture.cs` and `ServiceHarness.cs`. The EF in-memory
  provider can't model these, so don't use it. Pure logic with no DB needs no container.

### E2E tests (`E2E.Tests` — Playwright/NUnit)
- One test file per epic, mirroring `docs/stories/`.
- Tests inherit from Playwright's `PageTest`; use Page Object Model (`tests/E2E.Tests/Pages/`).
- Cover the Gherkin happy path + key unhappy paths through the real running UI.
- Run against the full stack: `docker compose up -d`, then start the API and Web. OTP-based
  tests read codes from **Mailpit**, so the API must send to Mailpit (the dev default) — see
  **`tests/E2E.Tests/README.md`** for the exact commands (incl. overriding a real-SMTP `.env`).
- Base URL defaults to `https://localhost:7008`; override with `PLAYWRIGHT_BASE_URL`.

### First-time Playwright setup
```sh
dotnet build tests/E2E.Tests
pwsh tests/E2E.Tests/bin/Debug/net10.0/playwright.ps1 install
```

### Running tests
```sh
dotnet test tests/Core.Tests
dotnet test tests/Api.Tests   # spins up a Postgres Testcontainer; Docker must be running
# E2E — requires docker compose + the API & Web running (see tests/E2E.Tests/README.md)
dotnet test tests/E2E.Tests
```

## How Claude Code should use this
- Default to vertical slices; refuse to build sprawling multi-epic chunks in one go — propose a
  split instead.
- **Write tests first.** For every slice: unit tests before Core/API code; E2E tests before UI
  code. Gherkin scenarios map directly to test cases.
- Write the per-epic story file before starting an epic; use the story + Gherkin as the spec.
- Name branches, commits, and PRs per the conventions above.
- Fill the PR template; check every box honestly or note why N/A.
