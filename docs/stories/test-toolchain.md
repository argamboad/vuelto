# Stories — Test toolchain (`xunit.v3` migration)

> One file per epic. Keeps the test projects on maintained packages. `xunit` 2.9.3 — what `Api.Tests`,
> `Core.Tests` and `Ui.Tests` run on — is flagged **Legacy** by NuGet; `xunit.v3` is the maintained line.
> Nothing is broken and nothing here reaches runtime: this is planned so the move happens on a quiet day,
> not on the day an SDK major forces it. Filed by the platform's v4 audit (T65, TOOL-5/6) and ported here.
> **Status: 📋 PLANNED** — TOOLS-1 not started; TOOLS-2 ✅ done 2026-10-04.

**Epic key:** `TOOLS`

**Prerequisites (external, before any code):** none.

**Where things stand (2026-10-04)** — `dotnet list package --deprecated --include-transitive`:

| Project | Deprecated | Why it is still there |
|---|---|---|
| `Api.Tests`, `Core.Tests`, `Ui.Tests` | `xunit` 2.9.3 (Legacy → `xunit.v3`) | TOOLS-1 below |
| `E2E.Tests` | none | TOOLS-2: `NUnit3TestAdapter` 5.0.0 → 6.3.0 dropped the transitive `Microsoft.ApplicationInsights` 2.22.0 |

The E2E suite is NUnit (Playwright's own base classes are NUnit) and stays NUnit; this epic does not touch it
beyond the adapter.

---

### TOOLS-1 — Move the three xUnit projects to `xunit.v3`

**Status: 📋 Planned.**

**As a** maintainer
**I want** the unit, integration and component tests on `xunit.v3`
**So that** the test framework is a maintained package, and the move is not forced on me by a future SDK

**Context / notes — what actually changes (sized against this repo's tree on 2026-10-04):**

- **Packages.** `xunit` → `xunit.v3` in `Directory.Packages.props`; `xunit.runner.visualstudio` (3.1.4 today)
  already runs both. A v3 test project is an **executable**: each test `.csproj` gets `<OutputType>Exe</OutputType>`.
  Regenerate the three lockfiles in the same PR (`dotnet restore --force-evaluate`), as for any package move.
- **`IAsyncLifetime` returns `ValueTask`.** v3's `InitializeAsync`/`DisposeAsync` return `ValueTask`, and the
  interface extends `IAsyncDisposable`. **10 files** in `Api.Tests` implement it (the Postgres fixtures, the
  integration factory, the RLS and migration test classes); none in `Core.Tests` or `Ui.Tests`. Mechanical, but
  `IntegrationTestFactory` also derives from `WebApplicationFactory<Program>`, which has its own `DisposeAsync` —
  that one needs care, not a find-and-replace.
- **`Xunit.Abstractions` is gone** (its types moved into `Xunit`). No file in the tree uses it today.
- **Analyzers.** v3 ships newer analyzers, and the repo builds with `TreatWarningsAsErrors`. Expect `xUnit1051`
  ("pass `TestContext.Current.CancellationToken`") across the async tests. Decide once, in the PR: fix them (the
  better answer — a cancelled run stops promptly) or disable that one rule in `Directory.Build.props` with the
  reason written beside it. Not a per-file suppression.
- **Collections and fixtures** (`[CollectionDefinition]`, `ICollectionFixture<T>` — 2 definitions in `Api.Tests`)
  are unchanged in v3.
- **bUnit** 2.x is framework-agnostic (`BunitContext`); `Ui.Tests` needs only the package and `OutputType` change.
- **The gates that read test source** (`EveryMachineRule_NamesAStandingCheck`, `RuleIds_CitedInTests_AreFinalRules`,
  the test-id contract) match method and class names, not attributes: unaffected.
- **CI** runs `dotnet test`, which runs v3 projects as it runs v2 ones. No workflow change is expected; confirm
  the coverage collector still attaches (`coverlet.collector` is a VSTest data collector).

**Order.** `Core.Tests` first (32 files, no fixtures) as the proof; then `Ui.Tests`; `Api.Tests` last (201 files,
the fixtures). One PR per project keeps each diff reviewable, or one PR for all three if the first two are trivial
— decide after `Core.Tests`.

```gherkin
Scenario: The test projects run on the maintained framework
  Given the three xUnit test projects reference xunit.v3
  When I run dotnet test on each
  Then the same tests are discovered and pass as before the move
  And dotnet list package --deprecated reports no deprecated package in any test project

Scenario: A database fixture still starts once and is disposed
  Given the Postgres fixture implements the v3 async lifetime
  When the Api.Tests suite runs
  Then one container is started for the collection and disposed at the end

Scenario: The build stays warning-free
  Given TreatWarningsAsErrors is on
  When the solution builds with the v3 analyzers
  Then it builds, with any disabled analyzer rule named and explained in Directory.Build.props
```

**Done when:** `dotnet list tests/<each> package --deprecated --include-transitive` is clean for all four test
projects, the test counts match the last run before the move, and CI is green.

---

### TOOLS-2 — Drop the deprecated transitive package from the E2E project

**Status: ✅ Done 2026-10-04** (v4 T65, ported from the platform). `NUnit3TestAdapter` 5.0.0 pulled
`Microsoft.ApplicationInsights` 2.22.0 (deprecated) through `Microsoft.Testing.Extensions.VSTestBridge` 1.5.3.
Adapter 6.3.0 no longer does; `tests/E2E.Tests/packages.lock.json` regenerated (`dotnet restore --locked-mode`
passes). The platform's CI ran its journeys green on 6.3.0 before the port; this repo's own run is the pull
request that brought it.

```gherkin
Scenario: The E2E project has no deprecated package
  When I run dotnet list tests/E2E.Tests package --deprecated --include-transitive
  Then it reports no deprecated packages
```

**Not done here, on purpose.** The same report lists newer majors of `NUnit` (5.0.0), `Microsoft.NET.Test.Sdk`
(18.x) and `coverlet.collector` (10.x), and a newer `Microsoft.Playwright.NUnit`. None is deprecated; a major
bump of the test stack is its own change with its own run of the suite, and Playwright moves with its browser
downloads in CI. Take them when there is a reason, one at a time.
