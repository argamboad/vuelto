# Postman collection — Vuelto API

Complete, chained collection covering **every HTTP surface** of the platform: health/meta,
passwordless sign-in (+ MFA step-up), account/sessions, MFA management, household (members,
invitations, roles, transfer, leave, export + signed download), notifications (incl.
delete/clear), billing (+ the fake-provider webhook that simulates payment completion), the
Notes sample slice, the staff admin surface (announce/broadcast/comp — ADR-021), and the
config-gated Public API + outbound webhooks.

**"Every HTTP surface" is machine-enforced** (v3 audit TR-6): the CI parity gate
(`tests/Api.Tests/Integration/PostmanParityTests.cs`) reads the app's real route table and fails
when a mapped `/api` endpoint has no matching request here. Browser-flow endpoints (OAuth
redirects/callbacks, the emailed magic-link verify, provider linking, signed file downloads) are
included as **`(doc-only)`** requests — they document the contract but aren't runnable from
Postman; everything else is executable and chained.

## Files

| File | Becomes in Postman |
|------|-----------|
| `Vuelto.postman_collection.json` | Collection (v2.1) — one collection for **all** environments |
| `Vuelto.local.postman_environment.json` | Environment "Vuelto — local dev" |
| `Vuelto.staging.postman_environment.json` | Environment "Vuelto — staging (Render)" |

## Workspace sync — git → Postman, automatic (no export/import)

These files are the **canonical copy** (CLAUDE.md "API documentation"); the Postman workspace is
a mirror kept fresh by CI. The `postman-sync` workflow pushes the collection + every
`*postman_environment.json` to the workspace (matched **by name**: update if present, create if
not) on any change to `docs/postman/**` on `develop` — so the team never imports JSON by hand.

**One-time setup** (until then the job skips with a notice):
1. Postman → avatar → **Settings → API keys** → generate a key → GitHub repo →
   **Settings → Secrets → Actions** → secret `POSTMAN_API_KEY`.
2. Postman → workspace **Overview** → copy the workspace **ID** → GitHub →
   **Settings → Variables → Actions** → variable `POSTMAN_WORKSPACE_ID`.
3. Delete any duplicate same-name collections/environments in the workspace once (with
   duplicates, the first name-match wins). Trigger the first run via **Actions →
   postman-sync → Run workflow** (or merge any `docs/postman/` change).

**Direction is one-way.** Edits made in the Postman UI are overwritten on the next sync — change
the JSON here (PR-reviewed, versioned) instead. Postman's built-in "connect repository" (API
Builder) was considered and rejected: it doesn't sync environments and needs manual UI pulls.

Manual fallback: importing the three files by hand still works anywhere.

## Environments — switching & adding

Every request is variable-driven (`{{baseUrl}}`, `{{mailpitUrl}}`, emails, tokens), so the
**environment selector in Postman's top-right corner is the only switch** — the collection never
changes. Tokens/ids live in the environment too, so each env keeps its **own session**: you can
stay signed in to local and staging simultaneously and flip between them.

| Environment | baseUrl | Email / OTP |
|-------------|---------|-------------|
| local dev | `https://localhost:7160` | Mailpit — the fetch request auto-extracts the code |
| staging (Render) | `https://template-staging.onrender.com` | **Real inboxes** (Brevo) — no Mailpit; read the OTP in your mail and set `{{otpCode}}` manually. Use a real address you own as `userEmail`. |

**Adding an environment** (e.g. production, once activated): duplicate a `*.postman_environment.json`,
change `name` + `baseUrl`, set `mailpitUrl` to `""` for hosted envs (the Mailpit fetch then skips
itself with a console hint), point `userEmail` at an inbox you own, and import. Commit the file
here so the team shares it.

Hosted-env caveats: the fake-provider **billing webhook** request only works where the fake
provider is configured (local dev) — against real Stripe it 400s by design (`stripe trigger`
instead). **Admin** requires your email in that environment's `Admin__StaffEmails__*` config;
**PUBAPI/HOOKS** gates are per-environment config too.

## Quick start (local)

1. `docker compose up -d` (Postgres + Mailpit) and run the API with the **https** profile
   (`dotnet run --project src/Api --launch-profile https` → `https://localhost:7160`).
2. Import the collection + both environments, select **Vuelto — local dev**.
3. **SMTP must point at Mailpit** (`localhost:1025`). If your local `.env` overrides SMTP to a
   real provider (e.g. Brevo), switch it back — the OTP auto-fetch reads Mailpit's API.
4. For `https://localhost` allow self-signed certs: Postman → Settings → General → **SSL
   certificate verification OFF** (or add the dev cert). Not needed for hosted envs.
5. Folder **1 · Sign in**: run `OTP — send` → wait ~2–5 s (outbox is async) → `OTP — fetch code
   from Mailpit` → `OTP — verify`. Tokens land in the environment; everything else just works.

## Quick start (staging / any hosted env)

1. Select the env, set `userEmail` to a real inbox you own.
2. `OTP — send` → read the 6-digit code in your inbox → paste it into `{{otpCode}}` (or directly
   into the `OTP — verify` body) → `OTP — verify`. Everything else is identical to local.

## How auth is wired

- The collection authenticates every request with **Bearer `{{accessToken}}`** (collection-level).
- Sign-in requests send **`X-Native-Client: true`**, selecting the API's body token transport —
  the refresh token arrives in JSON instead of an HttpOnly cookie, so Postman can chain
  `Refresh` (which **rotates**: the stored `refreshToken` is updated on every call; replaying an
  old one revokes all sessions by design).
- MFA-enabled account? `OTP — verify` stores `mfaChallenge`; complete with `MFA — verify
  (step-up)` using a TOTP or recovery code.
- `Impersonate user` stores a separate `{{impersonationToken}}` — it never clobbers your session.

## Feature gates & prerequisites

| Folder | Needs |
|--------|-------|
| 8 · Admin | your email in `Admin__StaffEmails__0` (repo `.env`) + API restart |
| 9 · Public API | `PublicApi__Enabled=true` + restart (`404` when off) |
| 10 · Webhooks | `Webhooks__Enabled=true` + restart; target URL must be public-routable (SSRF guard) |
| 6 · Billing webhook | fake provider (dev default); header `Stripe-Signature: valid` |

## Notes

- Passwordless send endpoints are rate-limited **5/min per IP** (verify: 10/min) → `429` with a
  "too many requests" message; that's the abuse guard (QA-AUTH-11), not a bug.
- Requests marked ⚠ are destructive (account erasure). The tests on each request assert the
  *expected* status set, including documented guard responses (402 quota, 409 provider-managed…).
- Rebranding: rename the collection/env (`Perezosoft` → your app) — see `docs/REBRANDING.md`.
