# Operator tools

Scripts an operator runs by hand against a database. **None of these has a page, a menu or an API
route** — they are deliberately hidden from the product. Everything here is safe to keep in the repo:
the scripts carry no data; the files they *produce* do, and stay on your machine.

| Tool | What it does | Proven by |
|---|---|---|
| [`snapshot-household.sql`](snapshot-household.sql) | Writes **one household** as a restorable SQL file, so a household built up on one server (typically your local machine) moves to another (staging, production) without starting over | `tests/Api.Tests/Tools/HouseholdSnapshotTests.cs` — a seed → snapshot → wipe → restore round-trip on real Postgres, plus a gate that every tenant-scoped table is listed |
| [`check-income-parity.sql`](check-income-parity.sql) | After the INCOME-1 migration, compares every month's new income rows with the two old income fields they were copied from, per currency. **Must print no rows** | `tests/Api.Tests/IncomeMigrationTests.cs` — seeds old-shape households, migrates, and asserts the same parity on real Postgres |
| [`backfill-income-lines.sql`](backfill-income-lines.sql) | The INCOME-1 copy on its own: old income defaults → income lines, old month incomes → month rows. Only needed after restoring a household snapshot **taken before** INCOME-1. Safe to re-run: it skips what already exists | `IncomeMigrationTests` — the script must equal the migration's SQL, and a second run adds nothing |
| [`staging-rls.ps1`](staging-rls.ps1) | Puts a hosted database (Neon) into the **two-role RLS posture** in one command: provisions the fenced `app_runtime` role, sets its password, proves it can read but not create tables, and prints the exact values to paste into the host | The script runs its own checks (a read that must succeed, a `CREATE TABLE` that must fail) and stops on the first one that does not hold; the app's `Rls__EnforceRuntimeRole` guard re-checks at boot |

---

## Snapshot a household and restore it elsewhere

### When to use it

You have been using the app locally for a while — categories, banks, budget lines, months and weeks,
transactions with their frozen exchange rates, refunds, merchant rules, the review queue — and now
staging or production exists. You want that household there, as is, once.

Not for: routine backups (use the database's own backups), moving a *whole server* (dump/restore the
database), or giving a household to a *different* person (the members' identity rows come along).

### What comes across

Ids are preserved, so every reference survives untouched.

| Included (in foreign-key order) | Why |
|---|---|
| `Users` (the household's members), `Tenants`, `TenantMemberships`, `UserLogins` | so your next sign-in on the target lands in *this* household — same user id, same membership; a Google/Microsoft login link matches when the target uses the same app registration |
| `BudgetSettings`, `Categories`, `Banks`, `Envelopes`, `IncomeLines`, `FixedExpenses`, `VariableExpenses`, `MerchantCategoryMappings` | the catalog and the budget baseline |
| `Months`, `MonthIncomes`, `Weeks`, `Transactions`, `Refunds` | the ledger — each month's income rows, frozen rates, refund links and received dates included |
| `PendingVouchers`, `IngestedVouchers` | the review queue and its dedup tombstones, so a re-sync on the target never re-stages what you already handled |

| Excluded on purpose | Why |
|---|---|
| `EmailConnections` | the OAuth tokens are encrypted with the **source** server's Data Protection key ring and cannot be read anywhere else — **reconnect each inbox on the target** (Settings → Email inboxes → Connect) |
| `Subscriptions` | billing state belongs to the target's own Stripe |
| `ApiKeys`, `AuditEvents`, `OutboxMessages`, `TenantInvitations`, `UsageCounters`, `WebhookSubscriptions`, `WebhookDeliveries` | operational state of the source server |
| `UserMfa`, `MfaRecoveryCodes`, `RefreshTokens`, `LoginTokens`, `Notifications`, `NotificationPreferences`, `InboxMessages` | per-server session/security state — re-enrol MFA on the target if you use it |

A table the source database doesn't have yet (an older schema, e.g. staging before INCOME-1) is skipped with a
`not in this database` comment, so the script also takes the pre-deploy backup in `docs/DEPLOYMENT.md` §9a.

The include/exclude list lives in the script header. `HouseholdSnapshotTests` fails the build if a new
tenant-scoped table is ever added without being named there.

### Prerequisites

- `psql` that can reach the **source** database. Locally that is the compose container:
  `docker exec -i vuelto-db-1 psql -U dev -d dev_db …`.
- The **target** already deployed once, so its schema is current (the API runs migrations on start).
- The target's **owner / migrations** connection string (on Neon: the role that owns the schema). The
  app's runtime role is fenced by row-level security (ADR-020) and cannot insert into another tenant.
- The email of the household's owner. If that person belongs to several households, the one where they
  are **owner** is chosen (then the oldest membership).

### Step by step

1. **Snapshot on the source.** `-Atq` keeps the output raw (no headers, no alignment):

   ```bash
   docker exec -i vuelto-db-1 psql -U dev -d dev_db -Atq -v email=you@example.com -f - < tools/snapshot-household.sql > my-household.sql
   ```

   Open the file: the header names the household id and the time; above every statement a comment
   says how many rows it carries (`-- Transactions: 28 row(s)`). Zero-row tables are still listed.
2. **Dry-run the restore** (recommended — it is what the tests do, on your real data):

   ```bash
   docker exec vuelto-db-1 psql -U dev -d dev_db -c "CREATE DATABASE snapshot_check;"
   docker exec vuelto-db-1 sh -c "pg_dump -U dev -s dev_db | psql -U dev -d snapshot_check -q"   # schema only
   docker exec -i vuelto-db-1 psql -U dev -d snapshot_check -v ON_ERROR_STOP=1 -q < my-household.sql
   docker exec vuelto-db-1 psql -U dev -d snapshot_check -c 'SELECT count(*) FROM "Transactions";'
   docker exec vuelto-db-1 psql -U dev -d dev_db -c "DROP DATABASE snapshot_check;"
   ```

3. **Restore on the target, once**, as the owner / migrations role:

   ```bash
   psql "<target owner connection string>" -v ON_ERROR_STOP=1 -f my-household.sql
   ```

   Every statement is `ON CONFLICT DO NOTHING` inside one transaction: a second run changes nothing,
   and a failure rolls the whole file back.
4. **Sign in on the target with the same email** (OTP or the linked Google/Microsoft account). The
   pre-seeded user is found by id, so you land in your household with everything in place.
5. **Reconnect your inbox(es)** under Settings → Email inboxes, then **Sync inboxes** on the Review
   queue: the tombstones came across, so nothing already handled is staged again.
6. **Delete `my-household.sql`** when done — it holds your data in clear text.

### Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `No household membership found for …` | the email has no membership on the source — check the spelling, or that you pointed at the right database |
| `permission denied for table …` on restore | you used the runtime role — restore with the owner / migrations role |
| `column "…" of relation "…" does not exist` | the target's schema is older than the source's — deploy the target (it migrates on start) and restore again |
| `duplicate key value violates unique constraint "IX_Users_Email"` | the target already has a user with that email under a *different* id (someone signed in before restoring) — delete that fresh user on the target, or restore before anyone signs in |
| Everything restored, but the app shows an empty household after sign-in | the sign-in created a *new* user/household because the email differs (case is ignored, dots are not) — sign in with exactly the exported email |
| Inboxes say "Needs reconnect" | expected — tokens never travel; reconnect once |

### Where else this is described

- `docs/DEPLOYMENT.md` §9 — the same steps inside the staging/production bring-up.
- `CLAUDE.md` — the status block's one-line pointer, next to the local seed routine.

---

## Check and repeat the income copy (INCOME-1)

### What this is, in plain words

INCOME-1 replaced the two "4-week / 5-week" incomes with income lines, and each month's two income fields with a list
of rows. The migration that ships it **only adds and copies**: it creates the two new tables, turns each household's
old defaults into lines, and copies every month's two incomes into rows with the same amounts and currencies. The old
fields stay where they were, untouched. These two scripts let you prove the copy and repeat it.

### Check the copy

Run it against the database the migration just touched, as the owner role, **before anyone edits a month's income**
in the app (an edit legitimately makes old and new differ):

```bash
psql "<owner connection string>" -f tools/check-income-parity.sql
```

Locally, against the compose database:

```bash
docker exec -i vuelto-db-1 psql -U dev -d dev_db -f - < tools/check-income-parity.sql
```

**Expected:** `DO`, then an empty result, `(0 rows)`. Each row printed is a month and currency whose totals differ:
the old total, the new total. Don't edit anything; roll the app back (the old fields are intact) and investigate.

### Repeat the copy after restoring an old snapshot

A household snapshot taken **before** INCOME-1 carries the old income fields but no lines and no rows. Restore it as
usual, then run the copy once:

```bash
psql "<owner connection string>" -f tools/backfill-income-lines.sql
```

It skips any household that already has income lines and any month that already has income rows, so running it twice
changes nothing. The same applies after "reset my data" with a seed file that only fills the old fields. Then run the
check above.

## Switch a hosted database to the two-role RLS posture

### What this is, in plain words

Row-level security (RLS) is already **on** in every environment: the schema forces a policy on every
tenant table, so a query only ever sees the household it was told about. On staging the app connects
as the database *owner*. Neon's owner is not a superuser, so the policies do apply to it, but an owner
can still run DDL (create or drop tables) and is one `ALTER` away from bypassing RLS.

The **two-role posture** (ADR-020, `docs/DEPLOYMENT.md` §7) splits that into:

| Role | Used for | Can it bypass RLS? |
|---|---|---|
| `app_runtime` | every request the app serves (`ConnectionStrings__DefaultConnection`) | no, and it cannot create tables either |
| the owner | the migrations the app runs at start-up (`ConnectionStrings__Migrations`), and operator work such as the household restore above | it owns the tables, so it must never serve requests |

`Rls__EnforceRuntimeRole=true` makes the app **refuse to boot** if its runtime connection turns out
to be privileged, so a mistake in the connection string fails loudly instead of silently weakening
the fence. Production is a copy of this; staging is where you rehearse it.

### Prerequisites

- Docker Desktop running with the compose stack's `vuelto-db-1` container up. The script borrows that
  container's `psql`, so nothing needs to be installed. (Any container with `psql` works: pass
  `-Container <name>`.)
- The **owner connection URL** from Neon: project → **Connect** → connection pooling **off** →
  copy the `postgresql://…` string. You paste it into the command yourself; it never leaves your
  machine except to reach the database.
- A **new password** for `app_runtime`: 24+ random characters, without `'`, `;`, `=` or `"`.

### Step by step

1. **Run the script** from the repo root, in PowerShell:

   ```powershell
   .\tools\staging-rls.ps1 -OwnerUrl "postgresql://neondb_owner:npg_...@ep-....aws.neon.tech/neondb?sslmode=require" -RuntimePassword "<your new password>"
   ```

   It prints four numbered steps. Each one must end in green: role provisioned, password set,
   `app_runtime` can read `Months`, `app_runtime` was **denied** `CREATE TABLE`. Any red line stops it
   and nothing on the host has changed yet, so it is safe to fix the cause and run it again.
2. **Copy the three values it prints** into the host (Render → your service → **Environment**):
   `ConnectionStrings__DefaultConnection` (now `Username=app_runtime`),
   `ConnectionStrings__Migrations` (the owner), `Rls__EnforceRuntimeRole` = `true`. Save.
3. **Deploy** (Render → Manual Deploy → *Deploy latest commit*, or the next CI deploy). Watch the
   boot log: it must reach the ready state without the message that starts
   `Rls:EnforceRuntimeRole is enabled, but the app's database role…`.
4. **Use the app normally** once: sign in, open the dashboard, add and delete a transaction. Everything
   that worked before still works; the only difference is the role underneath.

### Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `Provisioning script not found` | run from the repo root (or any folder inside the checkout) |
| `Error response from daemon` / `No such container` | Docker Desktop is off or the stack is down: `docker compose up -d db` first |
| `password authentication failed for user "neondb_owner"` | the URL is not the owner's, or was copied with pooling on (`-pooler` in the host); copy it again with pooling off |
| step 3 fails with `permission denied for table "Months"` | the grants did not apply: run the script again (it is idempotent) and read the step-1 output |
| step 4 says the role **could** create a table | the role is privileged on Neon (someone made it an owner); drop it in Neon → Roles and run again |
| the app does not boot; log says the database role is a superuser, has BYPASSRLS, or owns the tables | `DefaultConnection` still points at the owner: check `Username=app_runtime` on the host |
| the app boots but every page is empty | you pasted the runtime string into `Migrations` and the owner into `DefaultConnection` (swapped): the owner serves requests fine, but the guard is not looking at it. Swap them back |
| household restore fails with `permission denied` | expected: restore with the **owner** connection (see above), never with `app_runtime` |

## Publish the Android and Windows apps against a host

### What this is, in plain words

The phone and desktop apps are the same app as the website, built for a device. A **Release** build has
the API address baked in, so "the app against staging" is one build and "the app against production" is
another. This script makes both installables in one go. Full explanation: `docs/DEPLOYMENT.md` §9.

### Prerequisites

- The .NET MAUI workloads (`dotnet workload list` shows `android` and `maui-windows`) — already there if
  VS Code runs the app.
- For the signature check only: the Android SDK build-tools and a JDK on the PATH. Skipped if absent.

### Step by step

1. From the repo root:

   ```powershell
   pwsh tools/publish-native.ps1 -ApiBaseUrl https://vuelto-staging.onrender.com
   ```

   `-ApiBaseUrl` is **required** — a Release build compiles the host in, and there is no safe default.
   Output goes to `out\` at the repo root (gitignored; the same folder DEPLOYMENT §9 publishes to).
   Options: `-Out C:\somewhere`, `-Android` or `-Windows` alone.
2. **Phone:** send `out\Vuelto.apk` — that exact file, the one the script copied out of the publish
   folder for you (the folder also holds an unsigned twin Android drops without a word). Open it on the
   phone, allow installs from that source. It upgrades over a VS Code debug install (same key), but
   **close the app first**: Android will not replace one that is running.
3. **Desktop:** run `out\windows\Vuelto.Maui.exe`; pin a shortcut. SmartScreen warns once
   (unsigned) — *More info → Run anyway*.
4. The script ends with `Verified using v2 scheme … true` for the APK. If it says `false`, the phone will
   refuse the file silently — do not ship it.

### Troubleshooting

- **Tap the APK and nothing happens** → the APK carries only a v1 signature. Rebuild with this script
  (the project forces apksigner in Release); check with `apksigner verify --verbose`.
- **Google / Microsoft sign-in never comes back to the phone app** → the host lacks
  `Auth__Native__CallbackScheme=vuelto` (Render → Environment). Email-code sign-in works regardless.
- **`JAVA_HOME is set to an invalid directory`** (from `apksigner`) → the variable points at an uninstalled
  JDK; the script ignores it, but for manual `apksigner` runs point `JAVA_HOME` at the JDK you have
  (`C:\Program Files\Eclipse Adoptium\jdk-21…`).
- **NU1102 … Mono.win-x64** → a `-p:RuntimeIdentifier` was passed; don't.
- **"Release builds require -p:ApiBaseUrl"** → by design: never ship the localhost base.
