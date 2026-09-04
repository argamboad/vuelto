# Deployment runbook

> How to run this platform on a real environment. The target stack is **free-tier-first** (ADR-017):
> **Render** (one container serving the API **and** the Blazor WASM client, single-origin) + **Neon**
> Postgres + **Brevo** SMTP. The same Docker image runs anywhere — Render is the reference, not a lock-in.
>
> Epic: `DEPLOY` (`docs/stories/deploy.md`). This runbook is DEPLOY-2's deliverable; the CI-driven
> deploy pipeline is DEPLOY-3.

## TL;DR

1. Build once, locally, to prove the container: `docker compose --profile app up --build` → open the app.
2. Create a **Neon** DB, a **Brevo** SMTP key, and (for staging) a **Stripe test** key.
3. Apply `render.yaml` as a Render **Blueprint**, paste the secrets, deploy.
4. Register the OAuth redirect URIs + Stripe webhook against the live URL.

---

## 0. The image (what ships)

`Dockerfile` (repo root) is a multi-stage build that publishes the **Web** (Blazor WASM) and **Api**
projects, folds the WASM bundle into the API's `wwwroot`, and runs the API as a **non-root** user. The
API then serves everything from **one origin** (`Hosting__ServeWebClient=true`, baked in) — so the
refresh cookie is first-party and **no CORS configuration is needed** (`Auth__AllowedOrigins` can stay
empty). It listens on `$PORT` (Render provides it; defaults to 8080), migrates the database on boot, and
exposes `/health` (liveness) + `/health/ready` (DB reachable).

> **SDK pin:** the SDK version lives in **`global.json`** (the single source of truth) — CI's
> `setup-dotnet` reads it, and the Dockerfile build image (`sdk:10.0.400`) + runtime image
> (`aspnet:10.0.11`) are pinned to match. The Blazor SDK injects patch-specific implicit packages, so a
> float breaks `--locked-mode`. To bump the SDK, follow the bump-together playbook in `CLAUDE.md` (update
> global.json → regenerate lockfiles → the two Dockerfile tags → the docs, in one PR).

### Verify the image locally (no cloud accounts needed)

The `app` service in `docker-compose.yml` is behind the `app` **profile**, so the normal dev flow
(`docker compose up -d` → just Postgres + Mailpit) is unchanged. To run the production container against
the compose Postgres + Mailpit:

```bash
cp .env.example .env         # if you haven't — the app service loads it for secrets
# .env must set a Jwt__Secret (≥32 chars) and, because the container runs in Production, a
# Billing__Stripe__SecretKey (a Stripe TEST key is fine — see §3).
docker compose --profile app up --build      # add: APP_PORT=8099 to change the host port
```

Then check (host port 8080 unless you set `APP_PORT`):

| Request | Expect |
|---|---|
| `GET /health` | 200 |
| `GET /health/ready` | 200 (migrations applied, DB reachable) |
| `GET /` and `GET /settings` | 200, the SPA shell (deep links fall back to `index.html`) |
| `GET /_framework/dotnet.<hash>.js` | 200 `text/javascript` (WASM runtime served) |
| `GET /api/does-not-exist` | **404** (an unmatched API route is never the SPA shell) |
| `GET /api/notifications` (no token) | **401** (the API still guards its routes) |

Tear down with `docker compose --profile app down`.

---

## 1. Neon (Postgres, free)

1. Create a project at <https://neon.tech> (Postgres 17). **Do not enable Neon Auth** — this platform
   ships its own auth (ADR-002); a second identity source would only conflict. Use Neon as plain Postgres.
2. Copy the **Direct connection** string (the host **without** `-pooler`). That's the right default here:
   a single Render instance keeps its own Npgsql connection pool, and the app **polls** (no
   `LISTEN/NOTIFY`) and uses no server-side prepared statements, so it doesn't need PgBouncer. Keep
   `SSL Mode=Require`. Shape:
   `Host=<ep>.<region>.aws.neon.tech;Port=5432;Database=<db>;Username=<user>;Password=<pw>;SSL Mode=Require;Trust Server Certificate=true`.
   *(Only switch to the pooled `-pooler` host if you later run many instances — Neon's pooler is
   transaction-mode PgBouncer, which this app is compatible with but doesn't require.)*
3. This becomes `ConnectionStrings__DefaultConnection`. Migrations apply automatically on first boot.

> Free Neon autosuspends when idle and **auto-wakes in ~1 s** on the next query — no manual unpause.

## 2. Brevo (SMTP, free — 300/day)

1. Sign up at <https://brevo.com>, create an **SMTP key** (Senders & API → SMTP).
2. **Verify a sender** (Senders, Domains & Dedicated IPs → **Senders** → add + verify your email).
   Brevo refuses to relay from an unverified sender, so this is required before any mail flows.
3. Set: `Email__Smtp__Host=smtp-relay.brevo.com`, **`Email__Smtp__Port=2525`**,
   `Email__Smtp__Username=<your Brevo login>`, `Email__Smtp__Password=<the SMTP key>`, and
   **`Email__Smtp__FromAddress=<the verified sender>`** (optionally `Email__Smtp__FromName`). Without a
   valid, verified `FromAddress` the send is **rejected by Brevo** — and because mail is async via the
   outbox, the request still returns success while the email never arrives (it retries/dead-letters in
   `OutboxMessages`). If a code doesn't turn up, check that first.
4. For real deliverability later, verify a sender **domain** (SPF/DKIM) — optional for staging QA.

> **Render blocks outbound SMTP on ports 25/465/587 on free instances** (a `TimeoutException` on
> `ConnectAsync` is the symptom). Brevo also listens on **2525** (STARTTLS, not blocked), which is why
> the blueprint uses it. If you move to a host without that block, 587 is equally fine (the sender uses
> `SecureSocketOptions.Auto`). A paid Render instance lifts the block too.

## 3. Stripe (test mode) — REQUIRED

The billing provider is **fail-closed**: in any non-Development environment the app **refuses to boot**
without `Billing__Stripe__SecretKey` (the in-memory fake provider trusts an unsigned webhook and must
never run in Production — GAP-1). For staging, use a **test-mode** secret key (`sk_test_…`) from the
Stripe dashboard. You don't need working billing to sign in — this just satisfies the guard. (When you
later wire real billing, add `Billing__Stripe__WebhookSecret` and point a Stripe webhook at
`/api/billing/webhook`.)

Optionally set **`Billing__Stripe__ExpectLiveKey`** to fail closed on a key/mode mismatch (v3 DEP-10):
`false` on staging (refuses to boot with an `sk_live_…` key that could make real charges), `true` on
production (refuses to boot in test mode with an `sk_test_…` key). Unset skips the check.

> **Security + cache headers (v3 DEP-2/DEP-3).** When `Hosting__ServeWebClient=true` (the deployed
> single-origin container), the API adds `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY` +
> `Content-Security-Policy: frame-ancestors 'none'`, and `Referrer-Policy: strict-origin-when-cross-origin`
> to every response, plus **HSTS** outside Development. The SPA shell is served `no-cache` (so a post-deploy
> Blazor integrity mismatch can't pin a stale shell) and `/_framework` assets `immutable`. A fuller
> resource CSP (script/style/connect) is deferred — it needs validation against the running Blazor WASM app.

## 4. Render (host, free)

1. Push your branch; in Render choose **New + → Blueprint** and point it at the repo. It reads
   `render.yaml` (service `template-staging`, Docker runtime, free plan, health check `/health/ready`).
2. Fill the dashboard secrets (everything marked `sync: false`): `ConnectionStrings__DefaultConnection`
   (§1), `Billing__Stripe__SecretKey` (§3), the four `Email__Smtp__*` (§2). `Jwt__Secret` is
   auto-generated by Render (persisted, so sessions survive redeploys). Leave `Auth__AppBaseUrl` blank
   for the first deploy.
3. Deploy. When it's live, copy the public URL (e.g. `https://template-staging.onrender.com`) and set
   **`Auth__AppBaseUrl`** to it (magic-link/invite emails link there), then redeploy.

> Free instances **sleep after ~15 min idle** — the first request cold-starts (~30–60 s) and the
> background outbox/scheduler pause while asleep (queued email/webhooks flush on wake). Fine for staging;
> a paid always-on plan (~$7/mo) is the floor for real users. The same image; no code change.

## 5. OAuth — Google / Microsoft (optional)

OAuth is **config-gated**: a provider is only registered when its `ClientId` is set, and the login +
settings pages read `GET /api/auth/providers` so an unconfigured provider simply **shows no button**
(no dead button that 500s). Magic link + OTP work without any of this — set up OAuth only if you want it.

Per provider:

1. Register an app — **Google**: Cloud Console → APIs & Services → Credentials → OAuth client ID (Web).
   **Microsoft**: Azure Portal → App registrations → New registration.
2. Set the **redirect URI** to the app's default OAuth callback path (no `CallbackPath` override in this
   platform):
   - Google: `https://<host>/signin-google`
   - Microsoft: `https://<host>/signin-microsoft`
3. Copy the client id + secret into Render: `Authentication__Google__ClientId` / `__ClientSecret` (and/or
   `Authentication__Microsoft__ClientId` / `__ClientSecret`).
4. Microsoft only: the tenant authority defaults to **`consumers`** (personal accounts). For work/school
   or both, set `Authentication__Microsoft__Tenant` to `organizations`, `common`, or a tenant GUID — and
   register the app for the matching account types.

Because `Proxy__Enabled=true`, the app sees the real `https` scheme behind Render's proxy, so the
generated redirect URIs match what you register. Render redeploys on the env change; the buttons then work.

5. **Mail consent (EMAIL-2) reuses the same app registrations.** Connecting an inbox on `/email` sends the
   user to the provider with the read-only mail scopes and returns to the **API's** callback, so register a
   second redirect URI per provider: `https://<host>/api/email/connections/callback`. Google additionally
   needs the Gmail API enabled on the project and the `gmail.readonly` scope on the consent screen;
   Microsoft needs the `Mail.Read` + `offline_access` delegated permissions. Without these the "Connect"
   buttons answer `provider_not_configured` / the IdP refuses — nothing else breaks.

## 6. Continuous deployment (DEPLOY-3, optional)

By default you deploy by pushing to the branch Render tracks. To instead gate deploys on **green CI** and
run an automated post-deploy smoke, wire the pipeline in `.github/workflows/ci.yml`:

1. In Render → your staging service → **Settings → Deploy Hook**, copy the hook URL, and **turn off
   "Auto-Deploy"** (so CI is the only trigger — no double deploys).
2. Repo → **Settings → Secrets and variables → Actions**:
   - Secret **`RENDER_DEPLOY_HOOK_STAGING`** = the deploy hook URL.
   - Variable **`STAGING_BASE_URL`** = `https://<app>-staging.onrender.com`.

   > **Set them together — the hook without the base URL now FAILS the run** (v3 audit DEP-6). The hook is
   > what fires the deploy; the base URL is what verifies it. Previously a hook-without-URL deployed for
   > real and reported green having asserted nothing. Configure both, or neither.
3. Now a push to `develop` that passes every CI gate triggers the deploy, **waits for the new build to
   actually be live** (polls `/api/version` until it reports the pushed commit — the old instance keeps
   serving during Render's build), then smoke-tests the live URL (liveness/readiness, SPA shell +
   deep-link, `/api/*` → 404, `/api/auth/providers`). A red smoke fails the run. Until the secret +
   variable exist, the `deploy-staging` job logs a notice and passes.
4. **Prod** (when you have a prod service) — all three, and the reviewer is the actual gate:
   - Create a **`production`** GitHub Environment (repo Settings → Environments) and add a **required
     reviewer**. ⚠️ **Do not skip this.** The `deploy-prod` job names the environment, but the *approval*
     lives only in repo settings — it cannot be committed. A clone that adds the hook without the reviewer
     gets **un-gated auto-deploy to prod on every `main` push** (v3 audit DEP-7).
   - Secret **`RENDER_DEPLOY_HOOK_PROD`** = the prod deploy hook URL.
   - Variable **`PROD_BASE_URL`** = the prod service URL. Same pairing rule as staging: hook without base
     URL fails the run rather than shipping unverified.

   Prod then runs the **same** version-gated smoke as staging (they share
   `.github/scripts/deploy-smoke.sh`, so the two cannot drift), behind your approval.
5. **Postman workspace mirror** (optional, same graceful-skip pattern): secret **`POSTMAN_API_KEY`**
   (Postman → Settings → API keys) + variable **`POSTMAN_WORKSPACE_ID`** let the `postman-sync`
   workflow push `docs/postman/**` to the Postman workspace on every `develop` change — see
   `docs/postman/README.md`.

---

## Environment variables (reference)

| Key | Required | Notes |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | yes | `Production` (set by `render.yaml`) |
| `ConnectionStrings__DefaultConnection` | yes | Neon **direct** endpoint, `SSL Mode=Require` |
| `Jwt__Secret` | yes | ≥32 chars; Render auto-generates |
| `Billing__Stripe__SecretKey` | **yes** | fail-closed guard; a `sk_test_…` key for staging |
| `Email__Smtp__Host/Port/Username/Password` | yes | Brevo |
| `Email__Smtp__FromAddress` | yes | a Brevo-**verified** sender; sends fail without it |
| `Email__Smtp__FromName` | no | display name on outgoing mail |
| `Auth__AppBaseUrl` | yes | the public URL — used to build email links |
| `Hosting__ServeWebClient` | yes | `true` (baked into the image; keep set) |
| `Proxy__Enabled` | behind a proxy | `true` on Render — honor `X-Forwarded-*`. **Assumes the proxy is the sole ingress** — see §8 |
| `Proxy__KnownNetworks__0…` | if directly reachable | CIDRs; honor `X-Forwarded-*` only from a peer inside them (§8) |
| `Proxy__ForwardLimit` | rarely | proxy hops to trust, from the right (default `1`) (§8) |
| `PORT` | platform-set | Render provides it; image defaults to 8080 |
| `Auth__AllowedOrigins` | no | leave empty — single-origin needs no CORS |
| `Authentication__Google/Microsoft__*` | optional | enable OAuth sign-in **and** the read-only mail consent for the same provider (EMAIL-2) |
| `ExchangeRate__ApiKey` | recommended (app) | free key from app.exchangerate-api.com; unset ⇒ the household's last transaction rate, else "unavailable" — never a fabricated rate (ADR-V006) |
| `PublicApi__Enabled`, `Webhooks__Enabled` | optional | default off |
| `Admin__StaffEmails__0…` | optional | platform-staff allowlist |
| `ConnectionStrings__Migrations` | prod (two-role RLS) | owner/migrator connection — startup migrations do DDL (§7) |
| `Rls__EnforceRuntimeRole` | prod (two-role RLS) | `true` — fail-closed startup check that RLS actually applies (§7) |

This table is the deploy-oriented subset (what to set to go live). For the **complete list of every
configurable key and its default** — plus what's compiled-in and *not* configurable — see the
CONFIGURATION REFERENCE block in `.env.example`, the CI-enforced source of truth (the
`ConfigKeys_ReadInCode_AreDocumented` gate checks every key read in code against it).

Secrets live only in the Render dashboard / your local `.env` (gitignored) — **never** in the repo
(`render.yaml` declares keys, not values; gitleaks enforces this in CI).

---

## 7. Row-level security — the two-role topology (ADR-020)

The RLS tenancy backstop ships in the schema (the `RlsTenancyBackstop` migration `FORCE`s RLS +
fail-closed policies on every tenant table) and is driven per command by the app
(`RlsSessionInterceptor` sets the tenant/bypass GUCs). **What varies per environment is only the
role the app connects as** — Postgres exempts superusers/`BYPASSRLS` roles entirely:

- **Local dev** — the compose `dev` user is a superuser: RLS is present but bypassed, on purpose
  (zero-friction inner loop). The integration suite ALWAYS runs RLS-enforced via its own runtime
  role; to run the app enforced locally, see the optional block in `.env.example`.
- **Staging (Neon, single role)** — the Neon owner is *not* a superuser, and `FORCE` subjects
  owners to policies: **RLS is live on staging with no config change.** Migrations still work
  (owner does DDL).
- **Prod (two roles — activate with production, `STATUS.md` §5):**
  1. `psql` into the database **as the owner** and run
     `docker/db/provision-rls-runtime-role.sql` — **change the password literal first**.
  2. Set `ConnectionStrings__DefaultConnection` to the `app_runtime` connection (same host/db,
     `Username=app_runtime;Password=<yours>;SSL Mode=Require`).
  3. Set `ConnectionStrings__Migrations` to the owner connection (startup migrations bootstrap
     DDL the runtime role must not be allowed to run).
  4. Set `Rls__EnforceRuntimeRole=true` — the app then refuses to boot if its runtime connection
     would silently bypass RLS (superuser / `BYPASSRLS` / table owner), mirroring the Stripe-key
     guard.

Adding a new `ITenantScoped` entity? Ship its policy in the same migration
(`RlsDdl.StatementsFor`) — the `RlsMigrationGateTests` parity gate fails CI if you forget.

---

## 8. The proxy trust model — sole ingress (v3 audit DEP-1/ADM-10)

`Proxy__Enabled=true` tells the app to believe `X-Forwarded-For` / `X-Forwarded-Proto`. That matters
because the **real client IP drives the per-IP passwordless rate limiter** (and, through it, the MFA
attempt cap) and the `https` scheme drives OAuth redirect URIs.

**The assumption:** with no `Proxy__KnownNetworks` set, the app trusts **any** peer's `X-Forwarded-For`.
This is deliberate. A managed proxy fronts the app from an unknown, rotating IP, so the framework default
(trust loopback only) would ignore its headers entirely. It is safe **only because the proxy is the app's
only route in** — on Render the container's port isn't publicly reachable, so the only way to reach the
app is through the proxy, which *overwrites* the header with the true client IP.

**When that assumption breaks:** if the app is *also* reachable directly (a VM with an open port, a
cluster without an ingress-only policy, a port-forward), any client can send `X-Forwarded-For: 1.2.3.4`
and become whoever it likes — defeating the per-IP rate limiter and the brute-force protections built on
it. **Enabling this on a directly-reachable deployment is the failure mode to avoid.**

**Narrow the trust when you can't guarantee sole ingress:**

```bash
Proxy__Enabled=true
Proxy__KnownNetworks__0=10.0.0.0/8        # only honor X-Forwarded-* from peers in these ranges
Proxy__KnownNetworks__1=192.168.0.0/16    # repeat __2, __3… as needed
Proxy__ForwardLimit=1                     # hops to walk back (default 1); raise only for real chains
```

With `KnownNetworks` set, a request arriving from outside those ranges keeps its **real** peer IP and its
forged header is ignored. `ForwardLimit=1` means only the entry the nearest proxy appended is trusted, so
a client pre-seeding its own `X-Forwarded-For` can't reach past it — raise it only to the actual number of
proxies in front of the app.

Both fail closed: an unparseable CIDR or a `ForwardLimit` below 1 **stops startup** rather than quietly
widening trust. Leave `Proxy__Enabled=false` (the default) whenever there's no proxy at all.

---

## Prod, later

When a downstream app has real users, repeat §1–§5 as a second Render service fed from `main` (an
always-on plan), with **live** Stripe keys, the **two-role RLS setup (§7 — required by the
prod-activation checklist)**, a verified email sender domain (DKIM), and — optionally — a
custom domain (~$10/yr, the first worthwhile paid upgrade: nicer URLs + deliverability). Rehearse risky
migrations against a **Neon branch** (a free copy-of-prod DB) before promoting. The `main`→prod deploy is
gated behind a manual approval **only once you add the required reviewer to the `production` Environment**
— that gate is repo settings, not code, so it does not come with the clone. See §6.4.
