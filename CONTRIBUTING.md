# Contributing

Working conventions (slices, Gherkin stories, Conventional Commits, the PR template) live in
[`docs/WAYS_OF_WORKING.md`](docs/WAYS_OF_WORKING.md). Test-Driven Development is mandatory — write the
failing test before the production code on every slice (see `CLAUDE.md`, golden rule 7).
For orientation before touching platform code, the diagram layer is
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) (structure) and [`docs/FLOWS.md`](docs/FLOWS.md)
(call stacks) — both drawn from the code and cross-linked to the ADRs.

## Definition of "Solid" — the frozen quality bar

This is the finish line from the 2026-06-21 deep audit (`docs/audits/v1-2026-06/AUDIT_REPORT.md` §6). It is the spec
that ends the audit treadmill: when all five hold, "solid" is **provable by the test suite + CI**,
not asserted by a reviewer. **Do not run another discovery audit — keep these green instead.**

- [x] **1. No open High.** The three High findings are fixed and test-backed: write-side tenant
  stamping (CONF-1), passwordless brute-force/rate-limiting (CONF-5), and the fail-closed
  `email_verified` takeover guard (MITI-3).
- [x] **2. Tenant safety is structural in both directions.** Reads are scoped by the global query
  filter and writes by `TenantStampingInterceptor`; an architecture test fails the build if
  `IgnoreQueryFilters` appears in `src/Api/Features/**` (use `IRepository<T>.QueryAllTenants()`), and
  live two-tenant isolation tests prove the filter.
- [x] **3. Passwordless auth is rate-limited and the takeover guard fails closed**, each with a test
  (`RateLimitingTests`, `PasswordlessServiceTests`, `ClaimsExtractorTests`).
- [x] **4. The reference slice (Notes) is exemplary** — injected clock + shared
  `MapTenantFeatureGroup` scaffolding — because every feature copies it.
- [x] **5. Docs don't contradict code, and the invariants are enforced in CI** — warnings-as-errors
  (`Directory.Build.props`), the architecture tests (B9-1), and the migration-drift guard
  (`MigrationsTests`) all run in [`.github/workflows/ci.yml`](.github/workflows/ci.yml).

## v2 extension (2026-07) — post-platform-epic invariants

The v2 re-audit (`docs/audits/v2-2026-07/`) extended the bar to the platform epics (ADRs 006–016). The
binding ruleset is **[`docs/audits/v2-2026-07/FOUNDATION_RULES.md`](docs/audits/v2-2026-07/FOUNDATION_RULES.md)
v1.0 (R1–R35)** — read it before writing/modifying code. The new machine-enforced invariants:

- [x] **6. No default-config tenancy breach.** `FakeBillingProvider` runs only in Development; a keyless
  production build fails fast (GAP-1, R1).
- [x] **7. Write isolation covers UPDATE/DELETE**, not just INSERT — the interceptor refuses a
  foreign-tenant mutation loaded via the escape hatch (ADV-1, R32).
- [x] **8. Second-factor + provider-event replay are closed** — MFA step-up is single-use and TOTP
  timesteps are anti-replayed (LOGIC-S1, R28); the billing webhook applies only strictly-newer events
  (LOGIC-B1, R29).
- [x] **9. Outbound tenant-supplied URLs pass an SSRF guard; scope/event normalization fails closed;
  quota consumption is atomic** (GAP-2/SOLID-3/LOGIC-B7 — R3/R17/R30).
- [x] **10. Per-user PII can't escape erasure** — a new `UserId`-keyed entity fails the build until an
  `IUserDataContributor` is wired (SOLID-1, R12); and the **platform tests don't depend on the DELETE-ME
  Notes sample** (TR-1, R9).

## How the bar is enforced (so it can't rot)

| Guard | Where | Catches |
|-------|-------|---------|
| Architecture tests | `tests/Api.Tests/ArchitectureTests.cs` | `IgnoreQueryFilters`/`QueryAllTenants` in features; an unfiltered `ITenantScoped` entity; a `TenantId` entity that isn't scoped/allowlisted (R2); a controller skipping the tenant/admin base (R4); ambient `UtcNow` in server code (R15); a `UserId` entity not wired into erasure (R12); a platform test depending on the Notes sample (R9); Blazor components in the web app |
| Migration-drift + rollback | `tests/Api.Tests/MigrationsTests.cs` | a model change with no migration; a broken migration up **or** `Down` |
| Per-test isolation | `tests/Api.Tests/Infrastructure/PostgresTestBase.cs` | order-dependent / leaky relational tests |
| Warnings-as-errors | `Directory.Build.props` | nullable violations + every compiler/analyzer warning |
| CI gate | `.github/workflows/ci.yml` | all of the above, on every PR to `main`/`develop` |

When you add a feature, copy `src/Api/Features/Notes` (the DELETE-ME reference slice), comply with
`FOUNDATION_RULES.md`, and keep these guards green. That is the whole contract.

## v3 extension (2026-07) — the Definition of Solid is now FOUNDATION_RULES v2.0

The v3 delta audit (post-DEPLOY/NATIVE/RLS/THEME/PREFS epics) consolidated the bar into
**[`docs/audits/v3-2026-07/FOUNDATION_RULES_v2.md`](docs/audits/v3-2026-07/FOUNDATION_RULES_v2.md)**:
**R1–R35 carried unchanged from v1.0 + R36–R76** (each tagged `[machine]`/`[review]` with its
enforcement mechanism). **That file is the Definition of Solid** — read it before writing or
modifying code; the v2 section above remains as the historical layer it extends. Headline v3
additions, all machine-enforced on develop: the honest RLS migration-parity gate (R37) + the RLS
policy ships in the entity's own migration (R42), tenant-axis dissolution/export completeness +
DI-registered contributors (R43), impersonation never reaches the staff gate and audit writes
carry the acting principal (R45/R52), atomic single-use credentials + per-user second-factor caps
(R47–R49), the outbound-URL SSRF scan (R76), the client component-test chassis (R70), the Postman
parity gate (R74, ADR-023), one SDK pin source (R61), host `index.html` parity (R68), and
doc-map/QA-count sync (R75) — see `tests/Api.Tests/EnforcementGateTests.cs` for the last three.

> **Remaining v2 enforcement backlog** (tracked in `docs/audits/v2-2026-07/AUDIT_TASKS.md` B11, not yet
> wired): CI doc-sync + config-key + secret scan, Central Package Management + lockfile + license scan,
> the `MailKit`-outside-`Infrastructure/Email` ban, and the `MA0048` file-name analyzer.
