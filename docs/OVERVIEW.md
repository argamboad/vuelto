# Platform Overview — what this platform is, and why it saves you months

> A friendly tour for product managers, power users, developers, and architects.
> No prior knowledge of the codebase assumed. For the operational status snapshot see
> `STATUS.md`; for the doc map see the root `CLAUDE.md`.

## In one paragraph

This is a **production-grade foundation for multi-tenant SaaS applications** — everything a
subscription web product needs *except your product features*. Sign-in (passwordless, OAuth, MFA),
teams and roles, Stripe billing with quotas and trials, background jobs, notifications, file
storage, GDPR export/erasure, an admin console, a public API, outbound webhooks, two languages,
observability, native desktop/mobile shells, and a CI/CD pipeline that deploys to a free-tier
cloud — all built, integrated with each other, covered by ~500 automated tests, and documented.
You clone it, name your product, and start building the features only your product has.

## The problem it solves

Every new SaaS product rebuilds the same invisible 80%. Before a customer sees a single screen of
*your* idea, someone has to build sign-in and password recovery, team invitations, subscription
billing and its dozen edge cases (trials, failed cards, seat limits, cancellations), transactional
email, data export for GDPR, an audit trail, health checks, a deploy pipeline… That work is:

- **Slow** — realistically months, even for experienced teams (see the estimates below).
- **Risky** — auth, tenancy, and billing are exactly where security and money bugs live.
- **Undifferentiated** — none of it makes your product better than a competitor's; it's the
  price of admission.

This platform ships that 80% **pre-built and pre-hardened**: the pieces are wired together (a
notification is delivered through the same reliable job system billing uses; erasing an account
automatically cancels the Stripe subscription and deletes the tenant's files), the security
invariants are enforced by automated architecture tests rather than good intentions, and the whole
thing has been audited, remediated, and re-audited.

## How you use it

1. **Clone** the repo and fill in the short conceptualization docs (what the product is, its
   screens, its data model) — the docs have `TODO` placeholders and templates waiting.
2. **Rebrand** by checklist (`REBRANDING.md`) — name, logo, colours, email templates, the
   tenant's app-facing label (the reference app calls a tenant a "Household"; yours might be a
   "Workspace" or "Team").
3. **Build your features as vertical slices** — a documented convention with a complete reference
   feature (`Notes`) to copy: API endpoint + UI page + tests, end-to-end, one PR each.
4. **Deploy** on the proven path (Render + Neon + Brevo, all free tiers) — staging auto-deploys
   from `develop`; production is one approval click.

Everything is **web-first**: build a feature once in the shared UI library and it appears in the
web app *and* the native Windows/Android/iOS/macOS shells, which are already wired for auth,
language, downloads, and lifecycle quirks.

## The feature domains

### 🔐 Identity & sign-in
Getting users in the door, safely, with no passwords to leak.

- **Passwordless sign-in**: magic links (email a link) and 6-digit email codes — single-use,
  hashed at rest, time-limited.
- **OAuth**: "Continue with Google" wired; adding Microsoft/Apple/GitHub is **one line of code**.
- **Custom JWT + rotating refresh tokens** — sessions survive restarts, tokens can't be replayed.
- **MFA**: authenticator-app TOTP with QR enrollment, encrypted secrets, one-time recovery codes —
  and the step-up is enforced on **every** sign-in path (web form, OAuth, magic link, native app).

### 👥 Multi-tenancy & collaboration
The unit of account is a **tenant** (team/household/org); users share its data.

- Automatic tenant creation on first sign-in — zero-friction onboarding.
- Invitations by **email** and by **join code**; member roster with roles.
- Full membership lifecycle: transfer ownership, leave, **dissolve** (with complete cleanup:
  subscription cancelled, files deleted, projections wiped).
- **Isolation by construction**: a global database query filter scopes every read to the caller's
  tenant automatically. Bypassing it is banned in feature code and *fails CI* — cross-tenant leaks
  are an architecture-test failure, not a code-review hope.

### 🎭 Roles & permissions (RBAC)
- Owner / admin / member roles with a clean permission seam (`[RequireTenantPermission]`).
- Owner-only role management; the UI adapts to what the signed-in user may do.

### 💳 Billing (Stripe)
The domain teams underestimate most — here it's done, including the ugly parts.

- Plan catalog + **entitlements** (what each plan unlocks), Stripe **Checkout** and the
  **Customer Portal** (cards, invoices, cancellation — Stripe hosts it).
- Webhook-driven state: the app never guesses; Stripe events drive an idempotent projection.
- **Seat and usage quotas** enforced atomically, with a friendly "you've hit your plan limit" UX.
- **Trials and dunning**: expiry warnings, failed-payment handling, automatic lapse downgrade.
- A billing page summarizing plan, seats, usage, and renewal — plus a **fake provider** so
  development and automated tests never need real Stripe calls.

### ⚙️ Background jobs — the reliability backbone
- Transactional **outbox**: side-effects (emails, webhooks, notifications) are committed with your
  data and delivered after — a crash never loses work and never double-sends.
- **Inbox** for idempotent processing of external events, plus a **scheduler** for recurring work.
- Every other domain rides on this: it's why notifications, billing emails, and webhooks are
  reliable by default.

### 🔔 Notifications
- In-app notification center (the bell), unread counts, mark-as-read.
- Per-user delivery preferences (in-app vs email, per category).

### 📁 File storage
- One abstraction, two backends: local disk (dev) and **S3-compatible** (prod).
- Tenant-scoped keys (files are isolated like every other row), signed URLs, safe downloads —
  same-tab on web, the native **share sheet** on mobile/desktop.

### 🛡️ GDPR & data lifecycle
- **Tenant data export**: one click, a complete JSON bundle.
- **Account erasure**: per-user, cascading correctly through billing, files, and projections.
- Built on a **contributor pattern** — when you add a feature, you plug it into export/erasure
  once and compliance stays complete.

### 🔭 Observability & audit
- Structured logging + **OpenTelemetry** traces/metrics; `/health` and `/health/ready` endpoints.
- **Append-only audit log** of security-relevant actions — including admin impersonation.

### 🛠️ Platform admin console
- Config-gated staff-only surface: cross-tenant inspection, **short-lived audited impersonation**
  ("see what the customer sees" without their password), and broadcast announcements delivered
  through the notification system.

### 🔌 Public API & API keys *(config-gated, off by default)*
- Tenants can mint API keys (stored hash-only), scoped to their data automatically.
- Per-key **rate limiting** and a public **OpenAPI document** for integrators.

### 🪝 Outbound webhooks *(config-gated, off by default)*
- Tenants subscribe URLs to events; deliveries are **HMAC-signed**, retried with backoff, and
  fully logged with **one-click replay** and a send-test button.

### 🌍 Localization
- English + Spanish shipped end-to-end (UI, emails, validation); the resource-file plumbing makes
  a new language a translation task, not an engineering task. Language choice persists per user —
  including in the native apps.

### 📱 Native apps (Windows, Android, iOS, macOS)
- MAUI Blazor Hybrid shells reusing the **same UI code** as the web app — a feature built for web
  appears in the apps for free.
- The hard native glue is done: native-safe auth for all sign-in paths, MFA step-up, culture
  bootstrap, download→share-sheet bridge, refresh-on-resume, Android back button.
- CI compiles all four platforms on every push and **boots the real app** through a full sign-in
  on Windows and an Android emulator per merge.

### 🚀 Delivery, quality & guardrails
- **CI pipeline**: ~473 unit/integration tests, 29 browser end-to-end journeys, 2 native smoke
  tests, secret scanning, license and supply-chain checks (locked dependencies), documentation
  drift gates, and **architecture tests** (35 machine-enforced rules covering tenancy, fail-closed
  auth, SSRF, atomic quotas, and more).
- **Deploys**: Docker everywhere; staging auto-deploys on merge with a version-gated smoke check;
  production deploys behind a manual approval. Total infra cost to start: **$0** (free tiers).
- **Ways of working**: TDD, vertical slices, conventional commits, an ADR log explaining every
  significant decision (18 so far), and a 118-case manual QA plan with printable guides.

## The tech stack

One language end-to-end (**C# / .NET**), the latest stable versions only, and deliberately boring
choices — every one is mainstream, documented, and replaceable. Full rationale in
`TECH_STACK.md` and the ADR log.

| Layer | Choice | Notes |
|---|---|---|
| Runtime | **.NET 10** (SDK 10.0.400) | Latest stable line; policy: never previews |
| Backend API | **ASP.NET Core Web API** | The durable, client-agnostic asset |
| Web frontend | **Blazor WebAssembly** | A pure client of the API — never touches the DB |
| UI components | Shared **Razor Class Library** | One UI codebase for web *and* native shells |
| Native shells | **.NET MAUI Blazor Hybrid** | Windows, Android, iOS, macOS from the same RCL |
| Database | **PostgreSQL 17** | Free, portable, cheap to host |
| ORM | **EF Core (Npgsql)** | Migrations generated from the data model; the tenant filter lives here |
| Auth | **Custom JWT + rotating refresh tokens** | Not ASP.NET Identity (ADR-002) — the hardened passwordless/OAuth/MFA stack is the platform's own |
| Background jobs | **Postgres-backed outbox/inbox/scheduler** | No message broker to operate; Hangfire/Quartz/MassTransit are the documented swap-ins at scale |
| Billing | **Stripe.net** | Stripe is the source of truth; local `Subscription` is a projection; fake provider for dev/tests |
| Email | **MailKit behind an `IEmailSender` seam** | Only `Infrastructure/Email` may touch SMTP (CI-enforced) |
| Files | **AWSSDK.S3 behind `IFileStorage`** | Works with AWS S3, MinIO, Cloudflare R2, DO Spaces; local disk in dev |
| MFA | **Otp.NET** | Standard RFC-6238 TOTP |
| Telemetry | **OpenTelemetry 1.16** | OTLP exporter, config-gated |
| Tests | **xUnit** (unit/integration), **Playwright + NUnit** (E2E) | ~473 + 29 tests in CI |
| Packaging | **Docker** | Same container locally (compose) and in production |

**Architecture in one line:** Blazor WASM and the MAUI shells are both thin clients of one
ASP.NET Core API over a clean HTTP boundary; features are thin vertical slices on top of a
hardened platform layer; 35 architecture rules keep it that way mechanically.

## Service providers

Everything external is free-tier to start (**$0/month** until you have real traffic) and sits
behind an abstraction or a config key, so swapping any provider is contained:

| Role | Provider | Tier / cost | Swappable? |
|---|---|---|---|
| Source control + CI/CD | **GitHub + GitHub Actions** | Free (public repo = unlimited standard minutes) | Any git host; workflows are plain YAML |
| App hosting | **Render** (staging + prod services) | Free tier; deploy hooks from CI | Any Docker host — Fly.io, Railway, a VPS (ADR-017) |
| Database | **Neon** (serverless PostgreSQL 17) | Free tier | Any Postgres — RDS, Supabase, self-hosted |
| Transactional email | **Brevo** (SMTP) | Free tier (~300 emails/day) | Any SMTP behind `IEmailSender`; **Mailpit** traps all mail in dev |
| Payments | **Stripe** | Pay-per-transaction | Provider seam + fake implementation for dev/E2E |
| Sign-in (OAuth) | **Google** and **Microsoft** wired | Free | New provider = one line + its client credentials |
| File storage | Any **S3-compatible** (AWS S3, R2, MinIO, DO Spaces) | Config-selected; local disk default | Behind `IFileStorage` |
| Observability backend | Any **OTLP endpoint** (Grafana Cloud, Honeycomb, …) | Config-gated, off by default | Standard OpenTelemetry protocol |

Secrets never live in the repo: dev uses a gitignored `.env`, production uses real environment
variables, and CI runs a secret scanner to keep it that way.

## What it deliberately does NOT include

- **Your product.** There are no domain features — just `Notes`, a small complete sample slice
  you copy as a pattern and then delete.
- **Opinions you'd fight.** No CSS framework lock-in beyond plain components, no CQRS/event-
  sourcing ceremony, no Kubernetes — boring, replaceable choices on the latest stable .NET.
- **Multi-node scale-out** (caching layer, distributed locks) — consciously deferred until an app
  actually needs it; the seams are noted in the backlog.

## What it saves you

Honest, industry-typical estimates for a small experienced team building each domain **to the same
standard** (integrated, tested, edge cases handled — not a weekend prototype):

| Domain | Typical effort |
|---|---|
| Passwordless + OAuth auth, JWT/refresh sessions | 3–6 weeks |
| MFA enforced on every sign-in path | 2–3 weeks |
| Multi-tenancy with enforced isolation + lifecycle | 3–5 weeks |
| Stripe billing incl. quotas, trials, dunning | 4–8 weeks |
| Reliable background jobs (outbox/inbox/scheduler) | 2–4 weeks |
| Notification center + preferences + email fan-out | 2–3 weeks |
| File storage with signed URLs, S3 + native share | 1–2 weeks |
| GDPR export + erasure, done cascade-correctly | 2–4 weeks |
| Observability + append-only audit | 2–3 weeks |
| Admin console with audited impersonation | 2–3 weeks |
| Public API keys + rate limiting + OpenAPI | 2–3 weeks |
| Outbound webhooks with signing, retry, replay | 2–3 weeks |
| Localization plumbing (2 languages, incl. emails) | 1–2 weeks |
| Native shells for 4 platforms with real parity | 4–8 weeks |
| CI/CD, E2E suite, QA plan, security/supply-chain gates | 3–6 weeks |
| **Total** | **≈ 35–55 engineer-weeks (8–13 engineer-months)** |

On this platform, that becomes: **clone → rebrand → first product feature shipping within days.**

Two savings the table can't show:

- **Risk you don't take.** Tenancy isolation, webhook replay-safety, fail-closed auth, and quota
  atomicity are the bugs that cost customers and reputations. Here they're guarded by
  architecture tests and a two-round adversarial audit, not by hoping the intern read the wiki.
- **Compounding speed.** The conventions (vertical slices, TDD, the reference feature, the docs
  system) mean the *tenth* feature ships as cleanly as the first — including for AI-assisted
  development, which the repo's operating manual (`CLAUDE.md`) is explicitly designed for.

## Changing stacks: two credible migration paths

First, the honest framing. A stack migration means **re-spending a large share of the 8–13
engineer-months above** — perhaps 40–60% of it, because the language-neutral assets carry over:
the PostgreSQL schema, the Stripe account and webhook contracts, the architecture patterns
(tenant query filter, outbox, GDPR contributor, vertical slices), the docs
(`FEATURES.md`/`DATA_MODEL.md`/`DECISIONS.md` describe *the product*, not C#), and even most of
the Playwright E2E suite (it drives the browser by test IDs, not the framework). What does **not**
carry over is every line of C#. So a migration needs a *strategic* reason — team composition or
product direction — never fashion. "The stack is old" doesn't apply here (latest stable .NET), and
"I dislike Blazor" has a cheaper fix: thanks to the clean API boundary, you can **replace only the
frontend** with any JS SPA against the unchanged API — a frontend project, not a migration
(`TECH_STACK.md` names this escape hatch explicitly).

If a full migration *is* justified, these two targets fit this platform's shape best:

### Path 1 — Full-stack TypeScript: Next.js (React) + NestJS or tRPC + Drizzle/Prisma, same Postgres

**What justifies it:**

- **Frontend reach.** Blazor WASM's genuine weaknesses are first-load payload, SEO/SSR for
  products where the marketing site and the app blend, and the sheer volume of polished React
  UI components. If your product is consumer-facing and discovery-driven, this matters.
- **Hiring.** TypeScript/React is the largest engineering talent pool in the world; if you're
  scaling a team fast, availability wins arguments.
- **One language everywhere** — including your CI scripts, infra tooling, and (via React
  Native/Expo) a native-app story with over-the-air updates that replaces the MAUI shells.

**How the concepts map:** EF Core → Drizzle or Prisma on the *same* Neon database (consider
Postgres **Row-Level Security** as the new home of the tenant filter — it moves the isolation
guarantee into the DB itself); outbox/jobs → the same Postgres tables or a managed runner
(Inngest, Trigger.dev, BullMQ); custom JWT auth → Auth.js or a rebuild of the same token model;
Render → Vercel or stay on Render.

### Path 2 — Python: Django (+ DRF; keep or replace the frontend) same Postgres

**What justifies it:**

- **An AI-first product direction.** If the differentiating roadmap is LLM features, retrieval,
  evals, or data science, Python is where that entire ecosystem lives. Keeping the product
  backend and the AI code in one language and one process avoids the awkward "polyglot sidecar
  service" split that .NET+Python shops end up maintaining.
- **Batteries that mirror these epics almost 1:1.** Django's admin (≈ the ADMIN console),
  `django-allauth` (≈ OAuth/passwordless), Celery + beat (≈ JOBS), `dj-stripe` (≈ the billing
  projection), `django-anymail` (≈ the email seam), built-in i18n (≈ LOCALIZATION) — the
  rebuild is more assembly than invention.
- **Talent + data-domain fit** for teams already living in Python.

**How the concepts map:** the tenant filter → a scoped model manager (or `django-tenants`);
the same Postgres schema migrates over; DRF serves the same clean API so a JS or even the
existing Blazor frontend could keep talking to it during a phased cutover.

**What you give up on either path:** the single-language C# story, the shared-RCL trick that
gives four native apps from one UI codebase, and the 35 machine-enforced architecture rules —
you'd re-encode those invariants in the new stack's idioms (RLS, lint rules, CI checks), and
budget real time for it: that enforcement layer is a big part of what makes this platform safe.

## If you are a…

- **Product manager** — treat the domain list above as "already shipped." Scope your MVP purely
  in product features; sign-in, billing, and compliance are not on your roadmap.
- **Power user / founder** — you can have a branded, deployable, multi-user product skeleton on a
  $0 stack in a day, and validate an idea with real sign-ins and real Stripe test payments.
- **Developer** — read `WAYS_OF_WORKING.md`, copy the `Notes` slice, write the failing test first.
  The platform stays out of your way; the guardrails fail your build *before* they fail your users.
- **Architect** — start with `DECISIONS.md` (18 ADRs) and `audits/v2-2026-07/FOUNDATION_RULES.md`
  (the 35 enforced invariants). Clean API boundary, shared UI library, thin feature slices over a
  hardened platform layer; every "why" is written down.

## Where to go next

| You want to… | Read |
|---|---|
| See current status + what's left | `docs/STATUS.md` |
| Start a new app on the platform | root `CLAUDE.md`, then `docs/PROJECT_BRIEF.md` placeholders |
| Rebrand it | `docs/REBRANDING.md` |
| Build a feature | `docs/WAYS_OF_WORKING.md` + the `Notes` sample |
| Understand the stack in depth | `docs/TECH_STACK.md` |
| Understand a decision | `docs/DECISIONS.md` |
| See the architecture in diagrams | `docs/ARCHITECTURE.md` |
| Trace a call stack (sign-in, webhook, dissolve…) | `docs/FLOWS.md` |
| Deploy it | `docs/DEPLOYMENT.md` |
