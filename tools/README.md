# Operator tools

Scripts an operator runs by hand against a database. **None of these has a page, a menu or an API
route** — they are deliberately hidden from the product. Everything here is safe to keep in the repo:
the scripts carry no data; the files they *produce* do, and stay on your machine.

| Tool | What it does | Proven by |
|---|---|---|
| [`snapshot-household.sql`](snapshot-household.sql) | Writes **one household** as a restorable SQL file, so a household built up on one server (typically your local machine) moves to another (staging, production) without starting over | `tests/Api.Tests/Tools/HouseholdSnapshotTests.cs` — a seed → snapshot → wipe → restore round-trip on real Postgres, plus a gate that every tenant-scoped table is listed |
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
| `BudgetSettings`, `Categories`, `Banks`, `Envelopes`, `FixedExpenses`, `VariableExpenses`, `MerchantCategoryMappings` | the catalog and the budget baseline |
| `Months`, `Weeks`, `Transactions`, `Refunds` | the ledger — frozen rates, refund links and received dates included |
| `PendingVouchers`, `IngestedVouchers` | the review queue and its dedup tombstones, so a re-sync on the target never re-stages what you already handled |

| Excluded on purpose | Why |
|---|---|
| `EmailConnections` | the OAuth tokens are encrypted with the **source** server's Data Protection key ring and cannot be read anywhere else — **reconnect each inbox on the target** (Settings → Email inboxes → Connect) |
| `Subscriptions` | billing state belongs to the target's own Stripe |
| `ApiKeys`, `AuditEvents`, `OutboxMessages`, `TenantInvitations`, `UsageCounters`, `WebhookSubscriptions`, `WebhookDeliveries` | operational state of the source server |
| `UserMfa`, `MfaRecoveryCodes`, `RefreshTokens`, `LoginTokens`, `Notifications`, `NotificationPreferences`, `InboxMessages` | per-server session/security state — re-enrol MFA on the target if you use it |

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
