# vuelto's docs

¿Y el vuelto? is an app on the Perezosoft platform's packages (`Perezosoft.Platform.*`, ADR-034): the platform's
documents describe the platform, these describe vuelto.

| Document | What |
|---|---|
| [`PROJECT_BRIEF.md`](PROJECT_BRIEF.md) | What vuelto is, for whom, MVP in / out |
| [`FEATURES.md`](FEATURES.md) | User flows: the platform's constant flows and the app's |
| [`DATA_MODEL.md`](DATA_MODEL.md) | Entities, relationships, the money rules (two currencies, the day's rate) |
| [`DECISIONS.md`](DECISIONS.md) | vuelto's decisions, `ADR-V…` |
| [`QA_TEST_PLAN.md`](QA_TEST_PLAN.md) | Manual QA across web and the native shells; the PDFs are generated from it (`dotnet platform qa-guide`, `qa-runlog`, `check-qa`) |
| [`REPORTS_PDF_INCOME_PLAN.md`](REPORTS_PDF_INCOME_PLAN.md), [`UI_REDESIGN_PLAN.md`](UI_REDESIGN_PLAN.md) | Plans of record: the reports PDF + income lines, the UI redesign |
| [`stories/`](stories/) | The app's epics, as Gherkin |
| [`postman/`](postman/) | The API collection and its environments (ADR-023) |
| [`brand/`](brand/) | The brand's rasters |
| [`qa-runs/`](qa-runs/) | Filled QA run logs |
| [`../ops/`](../ops/README.md) | Operator scripts (hidden — no page, no route): the household snapshot, the income backfill and parity check, the two-role RLS posture |

**The platform's documents**, at its `develop`:
[NEW_APP_GUIDE](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/NEW_APP_GUIDE.md) (taking a platform release, the add-a-slice path) ·
[WAYS_OF_WORKING](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/WAYS_OF_WORKING.md) (slices, stories, commits, the add-a-slice checklist) ·
[ARCHITECTURE](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/ARCHITECTURE.md) · [FLOWS](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/FLOWS.md) ·
[DEPLOYMENT](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/DEPLOYMENT.md) (Render, Neon, Brevo; the image's `nuget` secret) ·
[MOBILE_TESTING](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/MOBILE_TESTING.md) · [LOCALIZATION](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/LOCALIZATION.md) ·
[REBRANDING](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/REBRANDING.md) · [TECH_STACK](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/TECH_STACK.md) ·
[DECISIONS](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/DECISIONS.md) ·
[FOUNDATION_RULES_v3](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/audits/v4-2026-09/FOUNDATION_RULES_v3.md) (the binding rules).

## vuelto's specifics the platform's documents do not know

- **Ports** (`local-ports.props`, `dotnet platform ports`): API https 7260 / http 5338, Web https 7108 / http 5269,
  Postgres 5434, Mailpit 1026 (SMTP) / 8026 (UI), the app container 8180. The Android emulator reaches the API at
  `http://localhost:5338` through `adb reverse tcp:5338 tcp:5338`. The compose project is `vuelto` (containers `vuelto-*`).
- **Staging**: `https://vuelto-staging.onrender.com` (the `vuelto-staging` service in `render.yaml`); its database is the
  `production` branch of the Neon project "Y el vuelto?". Its Docker build restores the platform's packages through the
  secret file `nuget` (the feed token, `read:packages`), set on the service. Staging migrates **on boot** against the
  owner's real household: see "Deploying a change that copies data" below.
- **The brand**: name "¿Y el vuelto?", app id `com.perezosoft.vuelto`, indigo + gold (ADR-V011, ADR-V015). The mails get
  it through `PlatformBrand` in `src/Host/AppComposition.cs` (name, palette, the logo `src/Host/Email/Assets/logo.png`,
  the tagline, and `src/Host/Email/EmailStrings*.resx` for the strings that name the app); the Android bars through
  `src/Maui/Platforms/Android/SystemBarColors.cs` (held to app.css by `NativeChromeGateTests`).
- **Native builds against a host**: `dotnet platform publish-native -ApiBaseUrl https://vuelto-staging.onrender.com`.

### OAuth redirect URIs (Google, Microsoft)

   **The full list each registration must carry** — one Google client and one Microsoft registration per
   app, and a missing URI is a silent OAuth failure on that host (the provider refuses the redirect, the
   app never learns why). Local first, then this app's staging. The `http://localhost:5338` rows are the
   Android emulator's (`adb reverse`, the platform's [MOBILE_TESTING](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/MOBILE_TESTING.md) §5); `https://localhost:7260` is the API's own
   HTTPS profile, which the web and desktop flows use.

   | Host | Google (Credentials → OAuth client → Authorized redirect URIs) | Microsoft (App registrations → Authentication → Web → Redirect URIs) |
   |---|---|---|
   | local (web/desktop) | `https://localhost:7260/signin-google` | `https://localhost:7260/signin-microsoft` |
   | local (Android emulator) | `http://localhost:5338/signin-google` | `http://localhost:5338/signin-microsoft` |
   | staging | `https://vuelto-staging.onrender.com/signin-google` | `https://vuelto-staging.onrender.com/signin-microsoft` |
   | local, mailbox connection | `https://localhost:7260/api/email/connections/callback` | same |
   | staging, mailbox connection | `https://vuelto-staging.onrender.com/api/email/connections/callback` | same |

   These are this app's own ports (above) and staging host; the mailbox-connection rows
   are the provider callback the email integration uses on each host. If staging uses a different client
   id than local, the staging row goes on that client instead. Confirm the list in the console whenever a host
   or a port changes; nothing in the repo can check it.
3. Copy the client id + secret into Render: `Authentication__Google__ClientId` / `__ClientSecret` (and/or

5. **Mail consent (EMAIL-2) reuses the same app registrations.** Connecting an inbox on `/email` sends the
   user to the provider with the read-only mail scopes and returns to the **API's** callback, so register a
   second redirect URI per provider: `https://<host>/api/email/connections/callback`. Google additionally
   needs the Gmail API enabled on the project and the `gmail.readonly` scope on the consent screen;
   Microsoft needs the `Mail.Read` + `offline_access` delegated permissions. Without these the "Connect"
   buttons answer `provider_not_configured` / the IdP refuses — nothing else breaks.

### Settings of its own (Render / `.env`)

| Variable | Required | What |
|---|---|---|
| `Authentication__Google/Microsoft__*` | optional | enable OAuth sign-in **and** the read-only mail consent for the same provider (EMAIL-2) |
| `ExchangeRate__Provider` | no | `bccr` (default): the Banco Central buy/sell pair via the Finance Ministry mirror — no key, no quota (ADR-V019); `exchangerate-api`: the one-rate world feed (needs `ExchangeRate__ApiKey`) |
| `ExchangeRate__BccrUrl` | no | the mirror URL (default `https://api.hacienda.go.cr/indicadores/tc`); outbound HTTPS from the host must be allowed |
| `ExchangeRate__ApiKey` | only with `exchangerate-api` | free key from app.exchangerate-api.com; when the chosen provider can't answer ⇒ the household's last transaction rate, else "unavailable" — never a fabricated rate (ADR-V006) |

### Staging and production RLS

- **Staging (Neon, single role)** — the Neon owner is *not* a superuser, and `FORCE` subjects
  owners to policies: **RLS is live on staging with no config change.** Migrations still work
  (owner does DDL).
- **Prod (two roles — activate with production, the platform's STATUS §5; rehearse on staging first).**
  One command does steps 1–2 and verifies them: `pwsh ops/staging-rls.ps1 -OwnerUrl <neon owner url>
  -RuntimePassword <new>` provisions the role through the local compose container's `psql`, proves
  it can read but not run DDL, and prints the three values for steps 2–4 (`ops/README.md`
  "two-role RLS posture" has the click-by-click version). By hand:
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

### Move your household to another server (snapshot — operator tool, hidden)

You have been running the app locally for a while and the household is real: categories, banks, budget
lines, months and weeks, transactions with their frozen rates, refunds, merchant rules, the review queue.
When staging or production comes up you do not start over — you carry the household across with
`ops/snapshot-household.sql`. No page, no menu, no API route: it is a psql script. **Full runbook —
what comes across, the dry run, troubleshooting: [`ops/README.md`](../ops/README.md).**

1. **Snapshot on the source** (the database that holds the household; `-Atq` keeps the output raw):

   ```bash
   docker exec -i vuelto-db-1 psql -U dev -d dev_db -Atq -v email=you@example.com -f - < ops/snapshot-household.sql > my-household.sql
   ```

   The file is plain SQL: one `INSERT … SELECT * FROM json_populate_recordset(…)` per table, in
   foreign-key order, **ids preserved**, every statement `ON CONFLICT DO NOTHING`, wrapped in one
   transaction. A comment above each statement says how many rows it carries.
2. **Deploy the target first** — the API creates the schema on its first start (the platform's [DEPLOYMENT](https://github.com/argamboad/perezosoft-platform/blob/develop/docs/DEPLOYMENT.md) §4 / §7).
3. **Restore as the owner / migrations role** (the runtime role is fenced by RLS — "Staging and production RLS" above), once:

   ```bash
   psql "<Neon owner connection string>" -f my-household.sql
   ```

4. **Sign in with the same email.** The OTP sign-in finds the pre-seeded user (same id), so the
   membership, the household and everything under it are already yours. A Google/Microsoft login link
   comes across too (same app registration ⇒ same subject).
5. **Reconnect the inbox(es).** OAuth tokens are encrypted with the *source* server's Data Protection
   key ring and are deliberately left out — **Settings → Email inboxes → Connect** again on the target.
   The dedup tombstones do come across, so a re-sync never re-stages what you already handled.

Not carried on purpose: billing/subscription state (belongs to the target's Stripe), API keys, audit
events, outbox, invitations, usage counters, webhooks, MFA enrolment and recovery codes, sessions,
notifications. Re-running the file is a no-op. `HouseholdSnapshotTests` proves the round-trip on a real
Postgres and fails the build if a tenant-scoped table is ever added without being listed as included
or excluded in the script.

### Deploying a change that copies data (INCOME-1)

Staging applies migrations **on boot**, against the owner's real household, with no human step. A migration that
copies data (the first is INCOME-1's `AddIncomeLines`: income defaults → income lines, month incomes → month rows)
therefore gets a rehearsal and a restore point **before the PR is merged**, and a check right after the deploy.

1. **Restore point.** In the Neon console, create a branch of the staging database (name it after the PR, e.g.
   `before-income-lines`). Keep it until the owner confirms the numbers. Also take the household snapshot (above) and
   keep the file outside the repo.
2. **Rehearse.** Create a second branch, point a local API at it (`ConnectionStrings__DefaultConnection`), and start
   it: the migration runs. Then:

   ```bash
   psql "<rehearsal branch owner connection>" -f ops/check-income-parity.sql
   ```

   **Expected:** `(0 rows)`. Open Settings → Manage income, three past months and their dashboards: the income is
   what production shows today.
3. **Merge and deploy.** Name the restore branch, the snapshot file and the rehearsal result in the PR.
4. **Check staging right after the deploy**, before anyone edits a month's income:

   ```bash
   psql "<staging owner connection>" -f ops/check-income-parity.sql
   ```

   Any row: roll the app back to the previous deploy on Render (the old income fields were never touched, so the
   previous build reads them as before), then investigate. The new tables can stay; the old build ignores them.
5. **Later, owner-gated (INCOME-3):** only after the numbers have been confirmed for a while does a separate PR drop
   the old income fields. That PR repeats steps 1–4.

Details on both scripts: [`ops/README.md`](../ops/README.md).

## Its own stack, beyond the platform's

- **`HtmlAgilityPack`** (MIT) — HTML parsing for the bank voucher extractors (BAC, BN), in `src/Host/Vouchers/`.
- **BCCR reference rates** (compra/venta) from the Finance Ministry's public mirror (`api.hacienda.go.cr/indicadores/tc`,
  no key) via a plain `HttpClient` + `IMemoryCache` (1 h freshness window counts as live) — the default
  `ExchangeRate:Provider` (ADR-V019); the **exchangerate-api.com** one-rate feed stays selectable (ADR-V006). Both hosts
  fixed, allowlisted for the outbound-URL guard.
- **Microsoft Graph + Gmail REST** via plain `HttpClient` (no SDKs) for read-only voucher ingestion: mail-scope consent
  reuses the `Authentication:{Microsoft,Google}` client credentials; mailbox tokens encrypted with **Data Protection**.
  Polling is an `IScheduledJob` on the platform's scheduler (ADR-V010).
- **`QuestPDF` 2026.9** — the Reports PDF (REPORTS-7 / ADR-V022), rendered server-side in `src/Host/Features/Reports/Pdf/`.
  **Community licence** (free under USD 1M annual revenue), set in code. The PDF embeds **Nunito** (SIL OFL 1.1, licence
  beside the files; it carries ₡). **`PdfPig` 0.1.16** (Apache-2.0) is test-only — it reads the generated PDF back.
- **No component library** — the UI is the platform's Bootstrap 5.3 RCL (ADR-V011).
