# Stories — Deployment to staging/prod (`DEPLOY`)

> One file per epic. Takes the platform from "tested locally + in CI" to **running on a real
> staging environment** on an all-free-tier stack, with a repeatable path to prod. Design decision +
> constraints in **ADR-017** (hosting: Render free + Neon Postgres + Brevo, **single-origin**).
> Stories use Gherkin acceptance criteria. **Status: ✅ COMPLETE** — DEPLOY-1 (single-origin + proxy),
> DEPLOY-2 (container + live staging, all four sign-in paths verified), DEPLOY-3 (CI deploy pipeline +
> post-deploy smoke + QA staging §1.5). Ongoing operator config (deploy-hook secret, prod environment)
> per `docs/DEPLOYMENT.md`.

**Epic key:** `DEPLOY`

**Why now:** every epic (auth → HOOKS) is built and CI-green, but the app has never left localhost.
Deployment is the one untested dimension: reverse proxies, real TLS, real domains, a managed DB,
sleeping containers, real SMTP. This epic makes staging the QA playground (`docs/QA_TEST_PLAN.md`
runs against a URL, not just a dev box) and records the prod recipe downstream apps inherit.

**The chosen stack (ADR-017, free-tier-first):**

| Layer | Choice | Free-tier shape |
|---|---|---|
| API + Web | **Render free** — one container; the API **serves the Blazor WASM bundle** (single origin) | 512 MB, sleeps after ~15 min idle, no card required |
| Postgres | **Neon free** | 0.5 GB, autosuspends, **auto-wakes ~1 s**; Postgres 17 |
| Email | **Brevo** free | 300 mails/day; already the platform's assumed prod SMTP |
| Billing | Stripe **test mode** | free; webhooks point at the staging URL |

**Prerequisites (external, before DEPLOY-2):**
- A **Render** account and a **Neon** account (both free, no card).
- A **Brevo** account with an SMTP key (free tier).
- Google/Microsoft OAuth apps gain the staging redirect URIs (`https://<staging-host>/signin-google`,
  `…/signin-microsoft`).
- Stripe test-mode webhook endpoint registered for the staging URL (+ its signing secret).
- **Repo secrets** (flag for the owner to add, like the CI-infra items in the v2 remediation):
  `RENDER_DEPLOY_HOOK_STAGING`, `STAGING_BASE_URL` (DEPLOY-3).

**Accepted free-tier trade-offs (recorded in ADR-017 — decisions, not surprises):**
1. **Sleep:** Render free spins down after ~15 min idle → first request of the day cold-starts
   (~30–60 s) and **the in-process outbox/scheduler pause while asleep** (queued email/webhooks send
   on next wake). Acceptable for staging; prod wants an always-on plan (~$7/mo) — same image, no code change.
2. **Real SMTP on staging:** Mailpit is a local dev trap; staging uses Brevo, so email-based QA cases
   need real (or plus-addressed) inboxes there. The automated staging smoke therefore checks
   health/shell, not email journeys.
3. **512 MB / shared CPU:** fine for QA; not a load-test box.

---

### DEPLOY-1 — Production topology in code: single-origin hosting + proxy correctness

**Status: ✅ Implemented** (`feat/deploy-1-single-origin`). Config-gated single-origin hosting: when
`Hosting:ServeWebClient` is true the API serves the published Blazor WASM bundle
(`UseBlazorFrameworkFiles` + `UseStaticFiles` + `MapFallbackToFile("index.html")`) with a more-specific
`/api/{**rest}` fallback so an unmatched API route stays an API-shaped 404 (never the shell); default off
(local dev keeps the `src/Web` dev server). `src/Web` `ApiBaseUrl` now defaults to
`HostEnvironment.BaseAddress` when unset (explicit config still wins — local dev + e2e CI). Config-gated
`UseForwardedHeaders` (`Proxy:Enabled`, default off) via `ProxyForwardingExtensions` — honors
X-Forwarded-For/-Proto behind a proxy, ignored otherwise (anti-spoofing). `.env.example` documents both
keys. Tests: `tests/Api.Tests/Integration/SingleOriginHostingTests.cs` (serve-off default, SPA fallback,
framework assets, `/api` not shadowed, known API route still authenticates — real Program routing via
`WithWebHostBuilder`) + `tests/Api.Tests/Hosting/ProxyForwardingTests.cs` (forwarded headers honored
on / ignored off — minimal TestServer).

**As an** operator
**I want** the API container to serve the Blazor WASM client and behave correctly behind a TLS-terminating proxy
**So that** one free host runs the whole app with first-party cookies and honest client IPs — no CORS/SameSite/cookie drama per environment

**Context / notes:**
- **Single origin.** The API hosts the WASM bundle (`Microsoft.AspNetCore.Components.WebAssembly.Server`:
  `UseBlazorFrameworkFiles` + `UseStaticFiles` + `MapFallbackToFile("index.html")`, fallback excluded for
  `/api/**`). This is **additive**: local dev keeps the separate `src/Web` dev server (hot reload) and the
  `BlazorClient` CORS policy stays for that + native clients. **Does not violate the clean-API-boundary
  rule** (golden rule 2 / ADR-004): the UI still talks to the API over HTTP only — the API just also
  serves its static files (recorded in ADR-017).
- **`ApiBaseUrl` becomes optional** in `src/Web`: default to `builder.HostEnvironment.BaseAddress`
  (same origin) when unset, instead of throwing. Local dev config keeps the explicit
  `https://localhost:7160`; the deployed bundle ships without it. (The e2e CI job's appsettings
  overwrite keeps working — explicit config still wins.)
- **Forwarded headers.** `UseForwardedHeaders` (`XForwardedFor | XForwardedProto`) — currently missing;
  behind Render every request would look like the proxy's IP, collapsing the per-IP passwordless rate
  limiter into one shared bucket (the exact failure the E2E suite hit in CI) and breaking scheme-aware
  redirect URI generation (OAuth callbacks). **Gate it by config** (e.g. `Proxy:Enabled` or the standard
  `ASPNETCORE_FORWARDEDHEADERS_ENABLED`), default **off**: honoring `X-Forwarded-For` when *not* behind
  a proxy is an IP-spoofing vector against the rate limiter.
- HTTPS redirection/HSTS must respect the forwarded proto (Render terminates TLS; the app sees http).
- Refresh-cookie flags (`Secure`, `SameSite`) verified for the same-origin deployment shape.

**Acceptance criteria**

```gherkin
Scenario: The API serves the web client
  Given the published API container
  When I request "/" or any non-API route like "/settings"
  Then I receive the Blazor index.html (SPA fallback)
  And requesting "/_framework/*" serves the WASM assets

Scenario: API routes are never swallowed by the SPA fallback
  When I request an unknown "/api/..." route
  Then I get an API-shaped 404/401, not index.html

Scenario: The web client defaults to same-origin
  Given the WASM bundle is served without an ApiBaseUrl setting
  Then it calls the API on its own origin
  And an explicit ApiBaseUrl (local dev, e2e CI) still takes precedence

Scenario: Forwarded headers are honored only when enabled
  Given proxy support is enabled (staging/prod config)
  When a request arrives with X-Forwarded-For and X-Forwarded-Proto
  Then rate limiting partitions on the forwarded client IP and generated URLs use https
  And with proxy support disabled (default) the headers are ignored
```

**Out of scope:** Dockerfile (DEPLOY-2); any hosting provisioning; CDN fronting.
**Definition of done:** tests first (integration harness: fallback vs `/api` routing, same-origin
default, forwarded-headers on/off incl. the rate-limiter partition); local dev flow unchanged
(`src/Web` dev server still works); `.env.example` documents the new keys; merged, app working;
ADR-017 referenced.

---

### DEPLOY-2 — Containerize + bring up the staging environment (Render + Neon + Brevo)

**Status: 🚧 Local half done** (`feat/deploy-2-container`) — cloud bring-up pending operator accounts.
Multi-stage `Dockerfile` (repo root) + `.dockerignore`: publishes Web + Api, folds the WASM bundle into
the API's `wwwroot`, runs non-root, binds `$PORT` (default 8080), `HEALTHCHECK` → `/health`. A compose
`app` service (behind the `app` **profile**, so `docker compose up -d` still starts only db+mail) gives
local parity. `render.yaml` blueprint (free plan, health `/health/ready`, secrets `sync:false`) +
`docs/DEPLOYMENT.md` runbook (Neon session pooler, Brevo, **required Stripe test key** — the Production
fail-closed billing guard, GAP-1). **Verified locally in Production mode against the compose Postgres:**
boots + migrates, `/health` + `/health/ready` 200, SPA shell + deep-link fallback, fingerprinted WASM
assets served (`application/wasm`), `/api/*` unknown → 404 (not shadowed), protected `/api` → 401.
**Remaining (operator):** create Neon + Brevo + Stripe-test accounts, apply the blueprint, set secrets,
first deploy, register OAuth/Stripe-webhook URIs — then confirm an OTP sign-in on the live URL.

**As an** operator
**I want** a production Docker image and a documented, reproducible staging environment on the free tier
**So that** the app runs on a real URL with a managed DB and real SMTP, and QA can test where deployment bugs actually live

**Context / notes:**
- **Multi-stage Dockerfile** (repo root + `.dockerignore`): `sdk:10.0` restore/publish the API (which
  now embeds the Web bundle via DEPLOY-1) → `aspnet:10.0` runtime, non-root user, listens on `PORT`
  (Render convention), container `HEALTHCHECK` → `/health`.
- **Compose parity:** an optional `app` service (profile) in `docker-compose.yml` runs the image against
  the compose Postgres + Mailpit — "the container works" is verifiable locally before any cloud step.
- **Neon:** connection string from env (`ConnectionStrings__DefaultConnection`), **session-mode pooler**
  + `sslmode=require` (transaction pooling breaks Npgsql prepared statements — record in the runbook).
  Verify on-boot `Migrate()` against Neon, and that the outbox dispatcher/scheduler behave through
  autosuspend cycles (they poll; a dropped connection must recover, not crash the host).
- **Render:** `render.yaml` blueprint (free plan, health check path `/health/ready`, env vars declared,
  secrets set in the dashboard — **never in the repo**, per the auth rules). Data Protection keys already
  persist to the DB, so MFA/webhook secrets survive restarts/redeploys ✅.
- **Config story:** a "staging/prod" section in `.env.example` documenting every deployed key (Brevo
  SMTP, OAuth secrets, Stripe test keys + webhook secret, `Proxy` gate, connection string) — the
  doc-sync CI gate keeps it honest.
- **Runbook:** new `docs/DEPLOYMENT.md` — provision Neon → Render → Brevo, register OAuth redirect
  URIs + the Stripe webhook, set env vars, first deploy, and the free-tier caveats (sleep/wake, Neon
  autosuspend, 300 mails/day).

**Acceptance criteria**

```gherkin
Scenario: The image runs the full app locally
  Given the built Docker image and the compose Postgres
  When I start the container with dev-shaped env vars
  Then /health/ready returns 200 and the web client loads and signs in (OTP via Mailpit)

Scenario: Staging is live
  Given the Render service, Neon DB, and Brevo SMTP are configured per the runbook
  When the container boots
  Then migrations apply, /health/ready returns 200 on the public URL
  And an OTP sign-in round-trips end to end (email delivered via Brevo)

Scenario: Secrets never enter the repo
  Then the image, render.yaml, and compose contain no secret values
  And the gitleaks CI gate stays green
```

**Out of scope:** the deploy pipeline (DEPLOY-3); prod provisioning (recorded as a recipe, executed
per-app); custom domain / DKIM (documented as the upgrade path in the runbook).
**Definition of done:** image builds in CI (build-only gate); compose-parity run verified; staging URL
live with a manually verified OTP sign-in; `docs/DEPLOYMENT.md` complete; `.env.example` updated;
merged; ADR-017 referenced.

---

### DEPLOY-3 — Deploy pipeline + post-deploy smoke + QA integration

**Status: ✅ Implemented** (`feat/deploy-3-pipeline`). Deploy jobs live in `.github/workflows/ci.yml`
(not a separate `deploy.yml` — same-workflow `needs` is the reliable way to gate deploy on the test/build
jobs). **`deploy-staging`**: on a push to `develop`, after every test gate is green, POSTs the Render
deploy hook (`RENDER_DEPLOY_HOOK_STAGING`), then waits for the new build to be live (`GET /api/version` reports the pushed commit — the old instance
serves during Render's build) and smoke-tests
(liveness, readiness, SPA shell + deep-link, `/api/*` → 404 not the shell, `/api/auth/providers`); a red
smoke fails the run. **`deploy-prod`**: on a push to `main`, behind the `production` GitHub Environment
(add a required reviewer → manual approval; `main` stays deploy-only). Both **skip cleanly** (log a notice,
pass) when their hook/URL aren't set, so the platform is green out of the box and a downstream app opts in.
QA plan gained **§1.5 "Environment B — deployed staging"** (real-Brevo inboxes, cold-start, config-gated
OAuth, Stripe test triggers), with the PDFs regenerated (B11-8 gate). Verified live end-to-end during
DEPLOY-2 bring-up (all four sign-in paths on the real staging URL).

**Operator setup (one-time, flagged):** repo secret `RENDER_DEPLOY_HOOK_STAGING` (Render → service →
Settings → Deploy Hook) + repo variable `STAGING_BASE_URL`; for prod, a `production` environment with a
required reviewer + `RENDER_DEPLOY_HOOK_PROD`. Turn **off** Render's native auto-deploy on the service so
CI is the only trigger. Steps in `docs/DEPLOYMENT.md`.

**As a** maintainer
**I want** merges to `develop` to auto-deploy staging (with a smoke gate) and a protected manual path for prod
**So that** staging always reflects `develop` for QA, and prod stays a deliberate act from `main`

**Context / notes:**
- **`deploy.yml`:** on push to `develop` (after CI is green) → trigger the Render deploy hook
  (`RENDER_DEPLOY_HOOK_STAGING` secret) → poll until live → **post-deploy smoke** against
  `STAGING_BASE_URL`: `/health` + `/health/ready` 200, `/` serves the app shell, an unknown `/api/*`
  route answers API-shaped (not index.html), `/login` renders (Playwright, headless). Email-based
  journeys stay **manual** on staging (real SMTP — no Mailpit to read; per ADR-017).
- **Prod path:** a `main`-triggered job behind a **GitHub environment with required approval** —
  honoring the standing rule that `main` is deploy-only and never touched without explicit say-so.
  The platform ships the workflow; actual prod provisioning is the downstream app's step 1 (runbook §prod).
- **QA plan integration:** `docs/QA_TEST_PLAN.md` §1 gains an **"Environment B: staging"** setup
  (staging URL, Brevo inbox strategy — plus-addressed real inboxes, Stripe test-mode triggers, the
  wake-from-sleep first-request note) so every existing case can be executed against staging as
  written. **Requires regenerating both QA PDFs** (the `qa-artifacts` co-change gate).
- Flag for the owner (CI-infra, can't self-serve): add the two repo secrets; create the `production`
  GitHub environment with a required reviewer.

**Acceptance criteria**

```gherkin
Scenario: Merging to develop updates staging
  Given CI is green on a merge to develop
  When the deploy workflow runs
  Then staging serves the new build and the post-deploy smoke passes

Scenario: A failed smoke is loud
  Given a deploy whose smoke checks fail
  Then the workflow run fails (red on the merge commit) with the failing check named

Scenario: Prod is gated
  Given the prod deploy job
  Then it only runs from main and requires a manual environment approval

Scenario: QA can run the manual plan against staging
  Given QA_TEST_PLAN.md §1 "Environment B: staging"
  Then a tester can execute the smoke suite (§4) against the staging URL as written
```

**Out of scope:** blue/green or preview environments (Neon branching makes these cheap later — noted
in the runbook as a follow-up); rollback automation (Render's "rollback to previous deploy" is the
manual lever, documented).
**Definition of done:** workflow merged + one observed green staging deploy with smoke; QA plan §1
updated **with regenerated PDFs**; required secrets/environment flagged to the owner; merged;
ADR-017 referenced.

---

## Slice plan (implementation map)

Ordered, each a mergeable vertical slice that leaves the app working. TDD throughout.

1. ✅ **Single-origin + proxy correctness (DEPLOY-1).** — DONE. API serves the WASM bundle (SPA
   fallback, `/api` excluded), `ApiBaseUrl` defaults to same-origin, config-gated `UseForwardedHeaders`.
   Both config-gated **off** by default (additive — local dev unchanged). Proven by integration tests
   in the existing harness (`SingleOriginHostingTests`) + a focused middleware test (`ProxyForwardingTests`).
2. 🚧 **Container + staging bring-up (DEPLOY-2).** — Local half DONE: multi-stage Dockerfile +
   `.dockerignore` + compose `app`-profile parity + `render.yaml` + `docs/DEPLOYMENT.md`, verified in
   Production mode against compose Postgres. Cloud half (Neon + Render + Brevo + Stripe-test accounts →
   live URL + manual OTP sign-in) is the operator's step per the runbook.
3. ✅ **Pipeline + smoke + QA (DEPLOY-3).** — DONE. `ci.yml` `deploy-staging` (develop → Render hook +
   post-deploy smoke) and `deploy-prod` (main → behind the `production` environment approval); both skip
   until their secrets exist. QA plan §1.5 "Environment B — deployed staging" (+ PDF regen). Operator
   adds the deploy-hook secret + `STAGING_BASE_URL` var to activate.

**Known sharp edges (from ADR-017):**
- **Forwarded headers are a spoofing vector when not behind a proxy** — config-gated, default off.
- **The SPA fallback must never shadow `/api/**`** (or API 404s become silent index.html 200s —
  breaks clients and the E2E suite's failure modes).
- **Transaction-mode pooling breaks Npgsql** — Neon/Supabase-class poolers must run session mode.
- **Sleep pauses the outbox** — free tier only; never ship a paid prod on a sleeping instance.
- **Secrets stay out of the repo** (golden auth rule) — Render dashboard env vars; `render.yaml`
  declares keys, never values; gitleaks enforces.
- **`main` stays deploy-only** — the prod job is the *only* thing that touches it, behind approval.
