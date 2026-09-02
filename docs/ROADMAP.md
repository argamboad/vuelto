# Roadmap

> The **sequenced** view of platform work: what's done, and what's planned next in priority waves.
> This is the *ordering*; the per-item design sketches live in `docs/PLATFORM_BACKLOG.md`, the decisions
> in `docs/DECISIONS.md`, and per-epic Gherkin stories under `docs/stories/`. Each planned item becomes
> an epic when picked up (ADR + story file + branch-per-slice — see `docs/WAYS_OF_WORKING.md`).
>
> **Sizes:** S ≈ a slice or two · M ≈ a small epic · L ≈ a multi-slice epic.

## Status (2026-06-26) — the three foundation pillars are DONE

| Pillar | Shipped | ADR / stories |
|--------|---------|---------------|
| **JOBS** | transactional outbox + inbox + scheduled-jobs host | ADR-007 / `stories/async-jobs.md` |
| **BILLING** (core loop) | entitlement gate → Stripe Checkout → webhook → Customer Portal | ADR-006 / `stories/billing.md` |
| **OBS** | structured logging, OpenTelemetry, health/readiness, append-only audit log | ADR-008 / `stories/observability.md` |
| *(emergent)* | `EnterTenant` tenancy primitive (ADR-003 amend); billing → platform-controller refactor (ADR-004/006 amend) | — |

**Key consequence:** building the platform infra first deliberately satisfied the dependencies for most
of the backlog — GDPR needed audit (✅), notifications/webhooks needed the outbox (✅), admin-impersonation
needed audit + a tenant-context primitive (both ✅). So the remaining work is largely **unblocked** and
sequenced below by value, not by dependency.

---

## Wave 1 — Keystones (unlock the rest) — ✅ COMPLETE

| Item | Epic | Size | Why first | Deps |
|------|------|------|-----------|------|
| **RBAC** (admin role + permission seam + roster UI) — ✅ *COMPLETE* (RBAC-1/2/3; ADR-009, `stories/rbac.md`) | `RBAC` | M | B2B table stakes; unblocks Admin + Public API | ready |
| **File storage** (`IFileStorage`: local / S3-compatible) — ✅ *COMPLETE* (FILES-1/2/3; ADR-010, `stories/files.md`) | `FILES` | S–M | Avatars, attachments, and the GDPR export artifact all need it | ready |

## Wave 2 — Compliance & security (enterprise table stakes)

| Item | Epic | Size | Why | Deps |
|------|------|------|-----|------|
| **Account & data lifecycle (GDPR)** — export + erasure — ✅ *COMPLETE* (GDPR-1/2; ADR-011, `stories/gdpr.md`) | `GDPR` | M–L | Legal exposure; reuses dissolve (add `ExportAsync` beside `WipeAsync` on contributors) | audit ✅, File storage (W1) ✅ |
| **MFA / TOTP 2FA** — ✅ *COMPLETE* (MFA-1..4 — JSON + redirect + native step-up all shipped, enforced on every sign-in path) (ADR-012, `stories/mfa.md`) | `MFA` | M | Security baseline; closes the ADR-C15 "TOTP promised, never built" gap | ready |

## Wave 3 — Extensibility & ops (open the platform up)

| Item | Epic | Size | Why | Deps |
|------|------|------|-----|------|
| **Public API + API keys** — ✅ *COMPLETE* (PUBAPI-1/2, config-gated off; ADR-015, `stories/pubapi.md`) | `PUBAPI` | M | Programmatic access distinct from the user session | RBAC (W1) |
| **In-app notifications** — ✅ *COMPLETE* (NOTIFY-1/2; ADR-013, `stories/notify.md`) | `NOTIFY` | M | Follow-on to email; fan-out via the outbox | outbox ✅ |
| **Outbound webhooks** — ✅ *COMPLETE* (HOOKS-1/2 — subscriptions + delivery log/replay; config-gated off; ADR-016, `stories/hooks.md`) | `HOOKS` | M | Integration story for *your* customers | outbox ✅ |
| **Admin back-office + impersonation** — ✅ *COMPLETE* (ADMIN-1/2; ADR-014, `stories/admin.md`) | `ADMIN` | M–L | Support tooling — all deps ready (audit ✅ + `EnterTenant` ✅); highest blast radius, do deliberately | RBAC, audit ✅, EnterTenant ✅ |

## Finish-the-epic (optional — when a real paid plan exists)

- **BILLING-5** (seat/usage quotas) — ✅ **DONE** (`IQuotaService`; seats on the invite path → 402,
  metered usage via monthly `UsageCounter`; limits in `PlanCatalog`, null = unlimited). · **BILLING-6**
  (trial/dunning) — ✅ **DONE** (`IBillingNotifier` dunning on past_due/canceled transitions +
  `SubscriptionLapseSweepJob` one-time lapse nudge, via NOTIFY). · **BILLING-7** (dissolve cleanup) —
  ✅ **DONE** (`BillingDataContributor` wipes the projection + cancels the provider sub via the outbox on
  tenant dissolve). **BILLING epic complete (1–7).** Optional follow-up: advance trial-ending nudge.

## Test & hardening debt (small, parallel cleanup)

- **E2E** (Playwright) for the platform features built unit/integration-first (billing flows, health, …).
- **stripe-mock** integration test (deferred in BILLING-2 over Testcontainers 4.12 friction).
- **Build-time ban on `IgnoreQueryFilters` in `src/Api/Features/**`** (audit task B9-1) — ✅ **DONE**
  (`tests/Api.Tests/ArchitectureTests.cs`): a one-test guardrail making the escape hatch unreachable
  from slice code.
- *(optional)* Declarative auto-audit SaveChanges interceptor (deferred from OBS-4; explicit
  `IAuditLog.Record` covers semantic events today).

## Deferred — don't build until forced

- **Redis distributed cache** (`CACHE`) — only when you outgrow a single node (breaks the "Postgres-only
  run cost" on purpose-deferred terms).
- **Not planned** (integrations, not core): full-text/vector search, marketing email/CRM, product-analytics
  pipeline. i18n expansion is already shipped (EN/ES; FR/DE/PT scaffolded — `docs/LOCALIZATION.md`).

---

## Recommended next

**All 11 planned epics + their UI are COMPLETE.** Foundation (JOBS/BILLING/OBS) + Wave 1 (RBAC, FILES) +
Wave 2 (GDPR, MFA) + Wave 3 (NOTIFY, ADMIN, PUBAPI, HOOKS) — ADRs 006–016, all merged. PUBAPI + HOOKS
were un-parked and shipped on 2026-07-01 (config-gated **default-off**). The API-first surfaces (MFA
enroll/step-up, GDPR export/erasure, notification bell menu, admin console) all have their Blazor UI now.
**Open items:** HOOKS-3 (a Blazor webhook/API-key management UI) and API-key rotation. **Deferred:**
CACHE (Redis — until multi-node).

**`THEME` — per-user dark mode → ✅ COMPLETE (2026-07-10).** Light/Dark/System on Bootstrap 5.3's
`data-bs-theme`, following the two preference playbooks: device-local pre-paint bootstrap
(`theme.js` + `localStorage["app_theme"]`, the NATIVE-5 culture seam minus the Preferences half)
and server sync (`User.Theme`, `PUT /api/auth/theme`, `theme` JWT claim, cold-start layout
reconcile — the `locale` playbook). See `docs/stories/theme.md`.

**`PREFS` — per-user preference sync → ✅ COMPLETE (2026-07-14).** Fixed the QA-I18N-02 failure
(locale was never persisted server-side — the only switcher lived on the anonymous login page) and
the theme-only-after-reload instability: Settings → Preferences card (signed-in home for both
switchers), reconcile on every sign-in via the `AuthService.SignedIn` event, device-choice adoption
when the server value was never set, one-reload locale apply (WASM satellite assemblies), and
"system" stored verbatim so Auto propagates. ADR-022; see `docs/stories/prefs.md`.

**`BILLING-9` — seat re-check at invitation accept → ✅ COMPLETE (2026-07-14).** Closed the quota gap
where a downgrade (dunning lapse, cancel, ADR-021 comp revert) left pending invitations that could
each still join and grow the tenant past its new cap: `AcceptAsync` now refuses over-cap tenants
(402 `seat_limit_reached`, "household full" state on `/join`; accepts at exactly the cap stay
allowed and a refused token self-heals on re-upgrade). ADR-006 addendum; see `docs/stories/billing.md`.

**`RLS` — Postgres row-level-security tenancy backstop → ✅ COMPLETE (decided + built 2026-07-06,
ADR-020 + addendum).** DB-level second wall under the ADR-003 query filter: FORCEd fail-closed
policies on every `ITenantScoped` table, `RlsSessionInterceptor` GUC propagation, tags/EnterTenant
for the sanctioned cross-tenant paths, the integration harness running RLS-ENFORCED as a
non-privileged role, the migration-parity CI gate, and the two-role prod topology (+ posture
guard) documented in `DEPLOYMENT.md` §7. Staging is live-enforced with no config change; prod
activation (`STATUS.md` §5) enables the guard.

## Next up: DEPLOY (planned 2026-07-02)

The one untested dimension left: the app has only ever run on localhost + CI. Epic **`DEPLOY`**
(ADR-017, `stories/deploy.md`) takes it to a real **staging** environment on an all-free-tier stack —
**Render free** (one container, the API serving the WASM bundle **single-origin**, which kills the
cross-site refresh-cookie failure class outright) + **Neon** Postgres (session pooler) + **Brevo**
SMTP — with a repeatable prod recipe. Three slices: **DEPLOY-1** single-origin hosting + config-gated
forwarded headers (pure code, harness-tested); **DEPLOY-2** Dockerfile + compose parity + staging
bring-up + a `docs/DEPLOYMENT.md` runbook; **DEPLOY-3** the deploy pipeline (develop → staging auto
with a post-deploy smoke gate; main → prod behind environment approval) + a staging section in the QA
plan. Free-tier trade-offs are recorded decisions, not surprises (instance sleep pauses the outbox —
staging-acceptable, never prod; real SMTP means email QA cases stay manual on staging).
**DEPLOY is now ✅ COMPLETE** (all three slices; staging live + auto-deploy with a version-gated smoke).

## NATIVE — full MAUI parity (planned 2026-07-02) → ✅ COMPLETE (2026-07-14)

The MAUI Blazor-Hybrid shells reuse the shared RCL, so they already render every web screen and have
native auth wired — but native isn't built in CI and the full feature surface is
inherited-but-unverified. Epic **`NATIVE`** (ADR-018, `stories/native.md`) commits to **full
parity across Android/Windows/iOS/macOS**, in three platform waves: **guardrails** (CI build gate + a
`docs/NATIVE_PARITY.md` audit), **gap-fixes** (WebView deltas — downloads, external links, back button,
culture/theming), and **verification** (a per-feature native QA pass + automated emulator/simulator
smoke). **Distribution (signed AAB / MSIX / IPA / pkg + store submission) is downstream-app work per
ADR-024 (decided 2026-07-14)** — signing identity is per-app; the platform ships the
first-native-release checklist (`NEW_APP_GUIDE.md` Phase 9) instead of artifacts, and the epic
completes at NATIVE-6. Recorded platform cost: the **macOS CI runner** (the Apple Developer account +
signing material moved to the downstream list). Parity means "what web does" — OS push/biometrics are
beyond scope; web-first still holds (this keeps native *caught up*).

**Epic closed 2026-07-14:** the NATIVE-6 Android + Windows device pass came back green (the Apple
§13b smoke had passed 2026-07-06), completing the last platform slice. NATIVE-12 (OAuth
process-death resilience, PR #172) was added post-close as a QA-finding fix slice.

## Terminal state (reached 2026-07-14) — the platform roadmap is done

Every planned epic is complete and verified on web + all four native platforms; staging deploys
continuously from develop. Two scope decisions closed the tail: **native distribution** (signing,
installers, stores — ADR-024) and **production activation** (ADR-017 amendment: staging is the
platform's terminal environment) are **downstream-app work**, executed per app via
`NEW_APP_GUIDE.md` Phases 8–9. The **v3 delta audit** (2026-07-15 → 07-27, 62 tasks, PRs
#147–#191) then hardened the finished platform rather than extending it — FOUNDATION_RULES v2.0
is the resulting quality bar. What remains here is by-choice backlog (HOOKS-3 UI, API-key
rotation, CACHE, FR/DE/PT — see above) and maintenance: toolchain drift, QA findings (§14a re-runs
+ QA-AND-15 are the open device items), and keeping docs/CI honest as downstream apps report back.
