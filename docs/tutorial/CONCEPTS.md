# Concept ledger

> The idea-level twin of `COVERAGE.md`: every first-class concept the course teaches,
> mapped to the lesson that **first teaches it** (later lessons may deepen it). Rule:
> no lesson may *use* a concept before its teaching lesson — if writing a lesson forces
> a concept earlier, move the row and re-sequence. Maintained by hand; reviewed at each
> part boundary.

## Architecture & design

| Concept | Taught | One-line definition |
|---|---|---|
| Tenant ≠ user (multi-tenancy) | 0.1 | App data belongs to the paying unit (tenant); users are members that come and go |
| Clean architecture / dependency rule | 0.1 | Dependencies point inward; the domain core depends on nothing |
| Vertical slices vs horizontal layers | 0.1 | Ship thin end-to-end increments; organize features as self-contained folders |
| Architecture Decision Records | 0.1 | Context → decision → consequences, written at decision time, superseded not deleted |
| Structural vs conventional enforcement | 2.6 | Invariants held by types/filters/gates, not by developer discipline |
| Seam (swap-point interface) | 2.3 | A small Core interface letting implementations swap by environment |
| Decorator pattern | 4.2 | Wrap an implementation of the same interface to add behavior (crash-safe email) |
| Repository + Unit of Work | 3.1 | Generic data access with tenant scoping built in; one transaction boundary |
| Marker interface | 2.6 | An empty-ish interface whose *presence* drives platform behavior (`ITenantScoped`) |
| EF interceptors | 2.7 | Hook SaveChanges/commands to enforce cross-cutting write-side rules |
| Feature folder anatomy | 3.2 | Endpoints + handler + models + data contributor, self-contained per feature |
| Contributor seam (decentralized lifecycle) | 2.9 | Features register their own wipe/export logic; no central method to forget |
| Error envelope | 1.5 | One response shape for all errors; internals never leak across the boundary |
| API status semantics (401/402/403) | 5.1, 6.1 | Unauthenticated vs unentitled vs unpermitted are different answers |
| Config-gated features (default OFF) | 7.3 | Optional surfaces ship disabled; absence of config = off, fail closed |

## Distributed systems & reliability

| Concept | Taught | One-line definition |
|---|---|---|
| Dual-write problem | 4.1 | DB commit + side effect cannot be made atomic naively; one will be lost |
| Transactional outbox | 4.1 | Persist intent in the same transaction; a dispatcher delivers later |
| Work claiming (`SKIP LOCKED`) | 4.1 | Multiple workers pull jobs without double-claiming or blocking |
| Idempotency / inbox dedup | 4.3 | Same message twice = same result once; dedup on (source, key) |
| Event-ordering guard (strictly newer) | 5.2 | Projections apply only newer events; redelivered/stale events are no-ops |
| Scheduled background jobs | 4.4 | Recurring work hosted in-process with per-job cadence |
| Injected clock (`TimeProvider`) | 3.3 | Time is a dependency; ambient now = untestable, drifting logic |
| Atomic quota consumption | 5.3 | Check-and-consume must be one operation or concurrency oversells it |
| Health / readiness probes | 4.5 | "Process up" ≠ "dependencies ready"; expose both, machine-readable |

## Security

| Concept | Taught | One-line definition |
|---|---|---|
| JWT access tokens & claims | 2.1 | Short-lived signed tokens carrying identity + tenant context |
| Rotating refresh tokens + reuse detection | 2.2 | Each refresh burns the token; a replayed old one reveals theft |
| Hash-only credential storage | 2.1 | Store hashes of tokens/keys; a DB leak yields nothing replayable |
| Passwordless (magic link / OTP) | 2.4 | Single-use, hashed, time-limited login tokens delivered out-of-band |
| OAuth / account linking takeover guard | 2.5 | Provider email trust decides when linking is safe vs an account takeover |
| Fail-closed defaults | 2.6 | Missing context yields *no* access/data, never *all* |
| Defense in depth | 0.2, 8.4 | Independent layers so one bypassed control isn't game over |
| MFA / TOTP step-up | 6.5 | Second factor enforced at every sign-in convergence point; anti-replay |
| RBAC permission matrix | 6.1 | Ordered roles mapped to coarse capabilities, checked at the endpoint |
| Entitlements (plan gating) | 5.1 | What the tenant's *plan* allows, distinct from what the *user* may do |
| SSRF and outbound URL guarding | 6.4 | User-supplied URLs can aim your server at your own network; validate first |
| HMAC payload signing | 7.4 | Shared-secret signature proves origin and integrity of a webhook |
| Signed, expiring URLs | 6.3 | Capability tokens for downloads; possession = access, bounded in time |
| Encryption at rest (DataProtection) | 6.3 | Secrets encrypted with a persisted keyring, not stored plaintext |
| Secret hygiene (.env contract + scanning) | 0.2 | Secrets never in the repo: gitignored env file + scanner + CI gate |

## Testing

| Concept | Taught | One-line definition |
|---|---|---|
| Red → Green → Refactor | 1.2 | The failing test comes first and drives the design |
| Test pyramid (unit / integration / E2E) | 3.6 | Fast checks in bulk, browser journeys for the critical paths |
| Testcontainers (real DB in tests) | 1.3 | Integration tests against real Postgres; in-memory providers lie |
| Model-derived fixtures | 1.3 | Reset lists generated from the EF model, not hand-maintained |
| Architecture tests | 3.3 | Invariants (banned APIs, inheritance, naming) asserted by the test suite |
| Page Object Model | 3.6 | E2E screens wrapped in classes so journeys read as intent |
| Test doubles at the seam | 5.1 | Fake implementations swapped in DI (fake billing provider, mail trap) |
| Invariant tests vs behavior tests | 2.6 | Pinning a platform guarantee, not one method's output |

## Data

| Concept | Taught | One-line definition |
|---|---|---|
| Global query filters | 2.6 | Model-level predicates applied to every query of an entity |
| Write-side stamping | 2.7 | Interceptor assigns/verifies tenant on inserts; rejects foreign writes |
| EF migrations discipline | 1.3 | Schema changes as generated, reviewed, replayable scripts |
| Projection (not source of truth) | 5.2 | A local read model of an external system, rebuilt from its events |
| Postgres row-level security | 8.4 | The database itself enforces tenant isolation as a second wall |
| Derived values computed, not stored | 3.2 | Compute from source data; stored flags go stale |

## DevOps & delivery

| Concept | Taught | One-line definition |
|---|---|---|
| Pinned toolchains & infra-as-code | 0.2 | Versions live in the repo (global.json, compose); machines converge |
| Central Package Management + lockfiles | 1.1 | One version table, locked restore; builds are reproducible |
| CI as a growing gate | 1.6 | The pipeline is born with the code and gains a job per capability |
| Supply-chain gates (license/secret scan) | 1.6 | The build refuses copyleft licenses and committed secrets |
| Dev/prod parity | 8.2 | The container you test locally is the artifact you ship |
| Single-origin hosting | 8.1 | API serves the SPA; kills cross-site cookie failure classes |
| Staged environments & gated promotion | 8.3 | develop→staging automatic + smoke; main→prod behind approval |
| Post-deploy smoke (version-gated) | 8.3 | Deployment verifies the *new* build is the one answering |
| Runbooks as deliverables | 8.2 | Operations knowledge written down, executable by someone else |
| Release readiness (auto + manual QA) | 8.3 | Automation gates regressions; a QA pass gates releases |

## Observability & operations

| Concept | Taught | One-line definition |
|---|---|---|
| Structured logging + enrichment | 4.5 | Logs as queryable events, tenant/request context attached |
| Distributed tracing (OpenTelemetry) | 4.5 | Correlated spans across requests, jobs, and dependencies |
| Append-only audit log | 4.6 | Semantic events recorded immutably; edits structurally refused |
| Audited impersonation | 7.5 | Support access that is scoped, time-boxed, and leaves a trail |

## Product & compliance

| Concept | Taught | One-line definition |
|---|---|---|
| Billing lifecycle (checkout→webhook→portal) | 5.2 | Provider owns payment state; you own the projection and gates |
| Seats & metered quotas | 5.3 | Plan limits enforced at the action, atomically |
| Dunning & lapse handling | 5.3 | Failed payment → notify → downgrade path, as recorded policy |
| GDPR export / erasure | 7.1 | Per-tenant export and per-user erasure via the contributor seam |
| Internationalization (resx pipeline) | 3.5 | UI *and* emails localized; language is user preference |
| Rebrand surface | 9.1 | Every brand touchpoint enumerated — including the ones inside emails |

*74 concepts · 50 lessons. If a concept you expected is missing, that's a review finding —
add the row, then find it a teaching lesson.*
