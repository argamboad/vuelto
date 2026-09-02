# Tech Stack

> Constant across these SaaS projects. The stack/architecture below is pre-decided; only the
> version numbers need re-verification at project start (this file ages). App-specific notes go
> in the marked section at the end.

## Summary

| Layer | Choice | Status |
|-------|--------|--------|
| Backend API | ASP.NET Core Web API | Committed |
| Web frontend | Blazor WebAssembly (WASM) | Committed |
| UI components | Shared **Razor Class Library (RCL)** | Committed (discipline rule) |
| Database | PostgreSQL | Committed |
| ORM | Entity Framework Core (Npgsql) | Committed |
| Auth | Custom JWT + rotating refresh tokens (no ASP.NET Core Identity) | Committed (ADR-002) |
| Non-web clients (mobile + desktop) | .NET MAUI **Blazor Hybrid**, reusing the RCL | Committed (ADR-018); auth wired, full-parity build = epic `NATIVE` |
| Hosting | **Render free** (one container: API serving the WASM bundle, single-origin) + **Neon** Postgres + **Brevo** SMTP — free-tier-first | Decided (ADR-017); build = epic `DEPLOY` |

## Target versions

> ⚠️ **RE-VERIFY at project start.** Versions move; search for current stable before committing.
> Policy: **target the latest _stable_ release, never previews.**
>
> **Verified 2026-08-25 (SDK-bump slice; first verified 2026-06-17):** .NET SDK **pinned in `global.json` (10.0.400, rollForward disable)** — the single source of truth
> (CI `setup-dotnet` + both Dockerfile image tags follow it; v3 audit DEP-4) · ASP.NET Core / EF Core
> **10.0.11** · Npgsql.EntityFrameworkCore.PostgreSQL **10.0.3** · PostgreSQL server **17**.
> Note: `Guid.CreateVersion7()` (time-ordered UUIDv7) is supported in .NET 9+ — already used in
> `Tenant.cs`. PostgreSQL 18 adds a native `uuidv7()` SQL function but is not required for this.

## Architecture shape

**Clean API boundary.** The Blazor WASM web app is a client of the ASP.NET Core API. The frontend
never talks to the database directly. This boundary is the durable architectural asset: any future
client (MAUI mobile/desktop, etc.) consumes the same API.

```
            ┌─────────────────────────┐
            │  ASP.NET Core Web API    │
            │  + EF Core (Npgsql)      │──── PostgreSQL
            │  + custom JWT auth       │
            └────────────┬────────────┘
                         │ HTTP (API boundary)
        ┌────────────────┴───────────────────┐
        │                                     │
┌───────────────┐                  ┌─────────────────────────┐
│ Blazor WASM   │  (NOW)           │ MAUI Blazor Hybrid      │  (LATER)
│ web app       │                  │ mobile/desktop shell    │
└───────┬───────┘                  └───────────┬─────────────┘
        │                                       │
        └──────────────┬────────────────────────┘
                        │  both consume
              ┌─────────────────────┐
              │ Shared Razor Class  │
              │ Library (UI)        │
              └─────────────────────┘
```

## The RCL discipline (present-day rule)

**Blazor UI components live in a shared Razor Class Library, not inline in the web app project.**
A future MAUI Blazor Hybrid app (mobile **and** Windows/macOS desktop) reuses the same components
from the RCL rather than rewriting the frontend. Reuse isn't 100% (navigation/platform bits
differ) but captures the majority of the UI. Cheap now, expensive to retrofit — so pay it up front.

## Multi-tenancy (constant)

- **Tenant ≠ User.** A Tenant (org/household/team — label is app-specific) owns the data; Users
  belong to a Tenant; multiple Users per Tenant.
- **Tenant-scoped data, per-user preferences only.** Enforce tenant scoping on every query; never
  leak across tenants. Users are custom entities authenticated by app-issued JWTs; tenant
  association sits on top and is enforced by a global EF query filter.

## Why these choices (rationale, constant)

- **ASP.NET Core Web API** — strong, well-supported, the durable client-agnostic asset.
- **Blazor WASM (over Server)** — preserves the "frontend is just another API client" boundary;
  modern Blazor reduced WASM bundle size and improved AOT. Server couples UI to server + holds a
  per-user live connection — rejected for that reason.
- **PostgreSQL** — free, portable, cheap to host; capable. Chosen over SQL Server for
  economy/portability.
- **EF Core (Npgsql)** — default .NET ORM; first-class Postgres; maps the data model to migrations.
- **Custom JWT + rotating refresh tokens** (not ASP.NET Core Identity) — a hardened auth stack the
  platform ships: passwordless (magic link + email OTP) and OAuth account-linking on custom
  `User`/`UserLogin`/`LoginToken`/`RefreshToken` entities. Tenant scoping layers on top. See ADR-002.
- **MAUI Blazor Hybrid (committed — ADR-018)** — reuses the C# Blazor UI (via RCL) across mobile +
  Win/macOS desktop, not just the API. Auth is wired; **full feature parity** (verify + CI + signed
  distribution across Android/Windows/iOS/macOS) is epic `NATIVE` (`docs/stories/native.md`).
  Alternatives if MAUI disappoints: Uno Platform, Avalonia, or a JS frontend against the same API.

## Deferred sub-decisions (revisit when relevant)

- ~~Hosting specifics (pick near deploy; undemanding profile).~~ **Decided 2026-07-02 (ADR-017):**
  Render free (single-origin container) + Neon Postgres + Brevo; built by epic `DEPLOY`
  (`docs/stories/deploy.md`).
- SMS OTP provider (Twilio etc.) — deferred until phone-based OTP is needed.

## Local dev environment (constant)

Spun up via `docker compose up -d`. Copy `.env.example` → `.env` and adjust before first run.

| Service | Image | Default port(s) | Purpose |
|---------|-------|-----------------|---------|
| `db` | `postgres:17` | `${DB_PORT:-5432}` (committed `.env.example` sets **5433**) | PostgreSQL — matches production DB engine |
| `mail` | `axllent/mailpit:latest` | SMTP `${MAIL_SMTP_PORT:-1025}`, UI `${MAIL_UI_PORT:-8025}` | Local SMTP trap for passwordless + invitation emails |

Both services have healthchecks. When the API container is added to compose (per-project), it should declare `depends_on: db: condition: service_healthy`.

Port variables allow multiple projects to run simultaneously without conflicts.

## Auth packages (constant)

| Package | Version | Purpose |
|---------|---------|---------|
| `Microsoft.AspNetCore.Authentication.Google` | 10.0.11 | Google OAuth provider |
| `Microsoft.AspNetCore.Authentication.MicrosoftAccount` | 10.0.11 | Microsoft OAuth provider |
| `MailKit` | 4.17.0 | SMTP email sending (magic links, invitations) |

**Adding a new OAuth provider:** install the provider package, add `.AddXxx(options => ...)` in
`ServiceCollectionExtensions.AddInfrastructure()`. No structural changes needed.

**Secrets in dev:** all local secrets/config live in the gitignored repo-root `.env` (loaded by
the API via DotNetEnv — see ADR-001); never put them in `appsettings*.json`. Copy `.env.example`
to `.env` and fill in. Keys use the .NET env-var form (`__` = section nesting):
```sh
# .env  (repo root)
Jwt__Secret=...
Authentication__Google__ClientId=...
Authentication__Google__ClientSecret=...
Authentication__Microsoft__ClientId=...
Authentication__Microsoft__ClientSecret=...
# Email__Smtp__* — optional; unset = Mailpit trap in dev
```
Production reads the same keys from real environment variables, never a committed file.

## App-specific notes
<!-- Fill per project: anything this app needs beyond the constant stack — extra libraries,
     storage (blob/file), background jobs, real-time (SignalR), search, etc. -->
- **Background jobs** — in-process outbox/inbox + scheduled-jobs host on Postgres (no broker); see
  ADR-007 / `docs/stories/async-jobs.md`. Hangfire/Quartz/MassTransit are the documented swap-ins at scale.
- **`Stripe.net` 52.x** — billing/subscriptions (ADR-006 / `docs/stories/billing.md`). Registered only
  when `Billing:Stripe:SecretKey` is set; otherwise an in-memory `FakeBillingProvider` keeps the app
  bootable and tests offline **in Development only**. The fake trusts a literal webhook signature, so
  outside Development a missing key **fails fast at startup** (v2 audit GAP-1) — production/staging **must**
  set `Billing__Stripe__SecretKey`. Stripe is the source of truth for money; our `Subscription` is a projection.
- **OpenTelemetry 1.16** (`OpenTelemetry.Extensions.Hosting` + AspNetCore/Http instrumentation + OTLP/
  Console exporters) — traces + metrics (OBS-2 / ADR-008). Npgsql DB spans via its built-in `"Npgsql"`
  `ActivitySource` (not the beta EF Core instrumentation). Exporter config-gated: OTLP when
  `OpenTelemetry:Otlp:Endpoint` is set, else nothing (or console via `OpenTelemetry:ConsoleExporter`).
- **`Otp.NET` 1.4.1** — authenticator-app TOTP (MFA-1 / ADR-012); the standard RFC-6238 math behind the
  encrypted per-user secret and step-up verification. **Supply-chain note (v3 audit TOOL-4/R66):** the
  package is effectively single-maintainer, and it sits on the MFA path — accepted because the surface
  is tiny (RFC-6238 HMAC math, no I/O, no network) and it is **confined to `MfaService`** (arch-tested:
  `OtpNet_IsConfinedToMfaService`), so swapping it for another RFC-6238 implementation — or ~30 lines of
  inline HMAC — is a one-file change behind `IMfaService`. Review releases before bumping the pin.
- **`AWSSDK.S3` 4.0.100** — the S3-compatible `IFileStorage` implementation (FILES-3 / ADR-010; works
  with AWS S3, MinIO, R2, DO Spaces). Selected only when `Storage:S3:*` is configured; else local disk.
- **`Swashbuckle.AspNetCore` 7.2.0** — the leak-free public OpenAPI document at
  `GET /api/public/openapi.json` (PUBAPI-2 / ADR-015), emitted only when `PublicApi:Enabled`.
- **`HtmlAgilityPack`** (MIT) — HTML parsing for the bank voucher extractors (BAC, BN); confined to
  `src/Infrastructure/Email/Vouchers/`. Core keeps only the pure text/date/money helpers (ADR-V010).
- **exchangerate-api.com** (free tier, 1,500 req/mo) via a plain `HttpClient` + `IMemoryCache`
  (1 h freshness window counts as live) — `ExchangeRate:ApiKey` (ADR-V006). Fixed host, allowlisted
  for the outbound-URL guard (R76).
- **Microsoft Graph + Gmail REST** via plain `HttpClient` (no SDKs) for read-only voucher ingestion:
  mail-scope consent reuses the platform's `Authentication:{Microsoft,Google}` client credentials;
  mailbox tokens encrypted with **Data Protection** (no extra key). Polling is an `IScheduledJob`
  on the platform's scheduler (ADR-V010).
- **No component library** — the UI is the platform's Bootstrap 5.3 RCL; the donor's MudBlazor is
  not brought over (ADR-V011).
- **Retired from the donor:** `System.Net.Mail`/Brevo HTTP senders (platform outbox + MailKit),
  `AesEmailTokenEncryptor` (Data Protection), Serilog + Sentry (platform OpenTelemetry), Swashbuckle
  for the private API (Postman is canonical), `railway.json`/`vercel.json` (Render).
