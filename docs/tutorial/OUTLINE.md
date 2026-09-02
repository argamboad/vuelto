# Build a Production SaaS Platform From Scratch — Course Outline (v2)

> Rebuild-from-zero: the learner types **every hand-written line of this repo**, in an
> order where each step depends only on what came before, understanding the purpose of
> each file as it's created. Coverage is not asserted — it is **machine-checked**:
> `gen_coverage.py` maps every tracked file to the lesson that builds it and fails on
> any unmapped file. See `COVERAGE.md` for the full file→lesson manifest
> (currently: 715 tracked files · 505 built in lessons · 210 explicitly bucketed as
> generated/vendored/meta · **0 unmapped** — last reconciled 2026-07-09, includes the
> QA-pass findings/features: rate-limit split + 429 copy in 2.4, typeable recovery codes
> in 6.5, notification delete/clear in 7.2, targeted/broadcast announce + plan comp in
> 7.5; plus the DevOps thread: CI born in lesson 1.6 and growing a job per part,
> deploy pipeline + release readiness in 8.3, the RLS backstop in 8.4).

## Pedagogical spine

1. **Every lesson ends green.** Builds, tests pass, git checkpoint tag.
2. **Pain before abstraction.** Build the concrete thing, feel the problem, then extract
   the seam that solves it.
3. **TDD is the rhythm.** Red → Green → Refactor on every lesson with logic in it.
4. **Guardrails ship with features.** Each Foundation Rule (R1–R35) arrives as the arch
   test added alongside the code that motivates it.
5. **Decisions are first-class.** Every lesson has an **Architecture Decision** box: the
   fork faced, the option chosen, the options rejected, and where it's recorded (ADR).
   Lesson 0.1 teaches the ADR practice itself before any code exists.

## Per-lesson template

```
Goal · Concepts · Maps-to (ADR / Rule / story / repo files — see COVERAGE.md)
1 Motivate (the problem, ideally as a failing/leaking test)
2 Red   — failing test from the Gherkin scenario
3 Green — minimum code, explained line-by-line where novel
4 Refactor & harden — extract the seam; add the arch-test guardrail
5 Run it — commands + expected output
6 Architecture Decision — the fork, the choice, the road not taken
7 Checkpoint — git tag; "you should now be able to…"
```

---

## Part 0 — Orientation
- **0.1 Mental model & the decision record.** Tenant ≠ user; clean platform + vertical
  slices; and the practice this course treats as a skill: ADRs, PROJECT_BRIEF, DATA_MODEL,
  FEATURES — writing the *why* down before the *what*. The learner writes ADR-001 of their
  own repo in this lesson.
- **0.2 A reproducible machine.** Pinned toolchain, `docker-compose.yml` (Postgres 17 +
  Mailpit), `.env`/`.env.example` contract (ADR-001), `.gitignore`/`.gitattributes`,
  `.gitleaks.toml` secret-scanning gate.

## Part 1 — The walking skeleton
- **1.1 Solution, projects & supply chain.** `Perezosoft.slnx`, all csproj + references
  pointing inward, `Directory.Build.props` (warnings-as-errors), Central Package
  Management + locked restore (R25–R27).
- **1.2 First endpoint, first test.** `Program.cs` is born; `WebApplicationFactory`
  harness; the Red→Green loop lived once, tiny; `Perezosoft.Api.http` scratchpad.
- **1.3 Database & the test container.** `AppDbContext` (v0), design-time factory,
  Testcontainers fixture with **model-derived TRUNCATE** (R11), migrations smoke test.
- **1.4 Configuration & the options pattern.** `SettingsProvider`, the one blessed
  `BindConfiguration().ValidateOnStart()` shape (R22), DotNetEnv loading, and the
  doc-sync test forcing every key into `.env.example` + `appsettings.json` (R20).
- **1.5 The error envelope.** `ErrorResponse`: one shape for every error; why
  `ex.Message` never crosses the API boundary (R16/R18).
- **1.6 CI from commit one.** `ci.yml` is born: build + test with `--locked-mode`,
  gitleaks secret scan (0.2's backstop becomes policy), the copyleft license gate (R26),
  and the PR-template checklist. Like `ArchitectureTests`, **the pipeline grows a job per
  part from here** — e2e (3.6), docker build (8.2), deploy stages (8.3), native legs (A.1).
  A gate you add at the end is a gate you never designed for; this course gates from day one.

## Part 2 — Identity & tenancy (the chassis)
- **2.1 Users & JWT access tokens.** Custom JWT vs ASP.NET Identity — the fork and why
  (ADR-002); token generator/hasher seams (store hashes, never tokens).
- **2.2 Rotating refresh tokens & sessions.** Rotation + reuse detection; the refresh
  cookie's HttpOnly/SameSite decisions — planting the seed for single-origin (8.1).
- **2.3 Email I — the `IEmailSender` seam.** MailKit quarantined in one folder; Mailpit as
  the dev transport; `BrandedEmail` templates (the rebrand trap, 9.1 pays it off).
- **2.4 Passwordless: magic link + OTP.** `LoginToken` single-use/hashed/time-limited;
  tests read the code from Mailpit's API.
- **2.5 OAuth & account linking.** "New provider = one line"; fail-closed claims
  normalization (R17); the email-trust takeover guard.
- **2.6 ★ Tenancy I — the global query filter.** Build the leak, prove it with a test,
  close it structurally; fail-closed `Guid.Empty`; R2's invariant test.
- **2.7 Tenancy II — write-side.** `TenantStampingInterceptor`; `EnterTenant` for
  system-authenticated writes; the sanctioned escape hatch + its CI ban (R5).
- **2.8 Tenants & membership.** `Tenant`/`TenantMembership`; the app-facing
  `HouseholdController`; one-tenant-at-a-time (ADR-003).
- **2.9 Invitations, dissolve & the contributor seam.** Invitation lifecycle;
  `ITenantDataContributor` — decentralized wipe that GDPR later extends (7.1).

## Part 3 — The slice pattern & the UI
- **3.1 The repository seam.** Generic `IRepository<T>` with tenant-scoped `Query()` +
  `QueryAllTenants()`; why not bespoke per-entity repositories.
- **3.2 Anatomy of a vertical slice (Notes).** Endpoints/handler/models/contributor;
  `MapTenantFeatureGroup` so a slice can't forget auth (R6); slice isolation rules (R7–R10).
- **3.3 Injected clocks & the architecture tests.** `TimeProvider` (R15); the
  `ArchitectureTests.cs` project is born and grows every part hereafter.
- **3.4 The web client & auth UI.** Blazor WASM + the Shared.Ui RCL (why: non-web clients
  cheap); `ISessionStore`/`IOAuthInitiator` per-platform seams; login/callback pages.
- **3.5 Localization.** `IStringLocalizer` + resx (EN/ES) — UI *and* emails; the E2E i18n test.
- **3.6 The E2E harness.** Playwright + Page Object Model; Mailpit-driven OTP journeys.
- **3.7 ★ Build your own slice (capstone).** The learner writes the story + the
  `DATA_MODEL.md` entry first, then runs the eight-step checklist + full TDD loop
  unaided, UI and E2E included. No repo files — theirs.

## Part 4 — Reliability & operations
- **4.1 The transactional outbox.** Why dual-write is a bug; `SKIP LOCKED` claiming (ADR-007).
- **4.2 Email II — the outbox decorator.** Same `IEmailSender` seam, now crash-safe —
  the decorator pattern earning its keep.
- **4.3 The inbox.** Idempotency via `(Source, IdempotencyKey)` dedup.
- **4.4 The scheduler.** `IScheduledJob` host; token-cleanup job.
- **4.5 Observability.** Tenant-enriched logging, OpenTelemetry, health/readiness (ADR-008).
- **4.6 The append-only audit log.** Explicit `IAuditLog.Record`; append-only enforced by
  interceptor, not policy.

## Part 5 — Monetization
- **5.1 Billing abstraction & entitlements.** `IBillingProvider`; plan catalog;
  `RequireEntitlement` → 402; fake provider that **throws outside Development** (R1).
- **5.2 Stripe.** Checkout/webhook/portal; signature-auth + `EnterTenant`; inbox dedup;
  strictly-newer projection vs webhook redelivery (R29).
- **5.3 Quotas, dunning & dissolve.** Atomic quota consumption under concurrency (R30);
  lapse sweep; the billing contributor cancelling the provider sub on dissolve.

## Part 6 — B2B essentials & security hardening
- **6.1 RBAC.** Ordered roles + permission matrix (capabilities, not ACLs — ADR-009);
  `RequirePermission` → 403; admin-aware roster UI.
- **6.2 File storage.** `IFileStorage` local/S3-compatible; tenant-scoped keys (ADR-010).
- **6.3 Data protection.** ASP.NET DataProtection with DB-persisted keyring; signed
  download URLs; the pattern MFA/webhook secrets reuse.
- **6.4 The SSRF seam.** `IOutboundUrlGuard`: block loopback/link-local/RFC-1918/metadata;
  HTTPS outside dev (R3). Placed here because Part 7 needs it.
- **6.5 MFA / TOTP.** Encrypted secret; hashed, **human-typeable** recovery codes
  (`xxxxx-xxxxx`, canonicalized entry); anti-replay timesteps (R32); step-up enforced on
  **every** sign-in path (ADR-012); QR enroll UI.

## Part 7 — Compliance & extensibility
- **7.1 GDPR.** Contributor seam grows `ExportKey`/`ExportAsync`; per-user erasure via
  `IUserDataContributor` — the build fails without it (R12).
- **7.2 In-app notifications.** Outbox fan-out; per-user prefs — the sanctioned
  "per-user, not per-tenant" exception, examined (ADR-013); caller-scoped delete/clear
  (the API exposes the verbs clients need — no DB side-doors).
- **7.3 Public API & API keys.** Config-gated default-OFF (R21); hash-only keys; key →
  tenant-scoped principal; per-key rate limits (ADR-015).
- **7.4 Outbound webhooks.** Encrypted secrets (6.3), HMAC signatures, outbox delivery
  (4.1), SSRF guard (6.4) — four seams composing (ADR-016).
- **7.5 Admin back-office.** Staff allowlist; sanctioned cross-tenant reads; short-lived
  **audited** impersonation via `EnterTenant` (ADR-014); targeted + platform-wide
  announcements (the latter a 202 → outbox fan-out); plan comp/revert (409 when
  Stripe-backed — the provider stays the source of truth).

## Part 8 — Ship it
- **8.1 Single-origin hosting.** API serves the WASM bundle; why this kills the
  cross-site refresh-cookie failure class; config-gated forwarded headers (ADR-017).
- **8.2 Container & staging.** Dockerfile (+ the compose `app` profile as local parity
  check: "does the image that ships actually boot, migrate, serve?"), `render.yaml`,
  Render/Neon/Brevo bring-up; free-tier trade-offs as recorded decisions; the learner
  writes their own `DEPLOYMENT.md` runbook — an ops artifact, taught like one.
- **8.3 The deploy pipeline & release readiness.** The pipeline born in 1.6 gets its
  deploy stages: develop→staging auto with a version-gated post-deploy smoke; main→prod
  behind environment approval; deploy concurrency. Plus the *manual* half of release
  readiness: a QA test plan as a maintained artifact (the reference repo's has 117 cases
  across web + four native platforms) — automation gates regressions, the QA pass gates
  releases.
- **8.4 The RLS tenancy backstop.** Postgres row-level security as the DB-level *second
  wall* under the EF filter (ADR-020) — defense in depth for the platform's core invariant.
  Policy DDL derived from the EF model (`RlsDdl`) + the migration-parity gate (a new
  `ITenantScoped` entity without its policy fails CI); the session interceptor carrying
  tenant/bypass GUCs per command; the sanctioned escape hatches declaring themselves via
  query tags — and why tags don't render for `ExecuteUpdate/Delete`; the two-role topology
  + fail-closed posture guard that gates prod activation.

## Part 9 — Make it yours
- **9.1 Rebrand & de-sample.** The full REBRANDING checklist (including the inline email
  logo everyone forgets), delete the Notes sample, rename the tenant term — turning the
  rebuilt platform into *their* product.

## Appendix (optional)
- **A.1 MAUI shells & parity.** Desktop + Android hosts of the same RCL; the parity-audit
  method (WebView-vs-browser deltas → gap register → seams: resume-refresh, culture
  bootstrap, download launcher); the WebView2/emulator smoke harness (ADR-018).
- **A.2 Native auth bridge.** Loopback/deep-link OAuth, secure storage sessions, native
  MFA step-up.

> Note: the native *seams* themselves (`ICulturePersistence`, `IFileDownloadLauncher`)
> are taught in the main track (3.5, 6.3) where their web implementations land — a
> deliberate beat: designing the RCL seam web-first is what makes the native shells
> cheap later (Golden Rule 5).

---

## Coverage discipline

`python docs/tutorial/gen_coverage.py` regenerates `COVERAGE.md` and **exits 1 on any
unmapped file**. Run it whenever the repo or the outline changes; a new file that belongs
to no lesson is a course bug. Files bucketed as GEN (migrations, lock files) are
*generated by the learner* with tooling in the noted lesson; VENDORED and META are the
only things never typed.
