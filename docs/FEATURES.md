# Features & User Flows

> How the product behaves, flow by flow. The "why/what" lives in `PROJECT_BRIEF.md`; structures
> live in `DATA_MODEL.md`. Fill one section per major flow. Pattern shown below.

## Flow template (copy per flow)

### N. <Flow name>
**Goal:** <what the user is trying to accomplish>

Flow:
1. <step>
2. <step>

Notes: <edge cases, derived-rule references, tenant-scoping considerations>

---

## Constant flows (auth + tenant onboarding — always present)

> These reflect the platform's **custom JWT + refresh-token** implementation (no ASP.NET Core
> Identity). Endpoint names match `AuthController` / `HouseholdInvitationsController`. The
> step-by-step QA scripts live in `docs/QA_TEST_PLAN.md`.

### 1. Sign in via OAuth (Google / Microsoft / future providers)
**Goal:** a user authenticates using their existing identity provider account.

Flow:
1. User clicks "Continue with Google" (or Microsoft) on the login page.
2. Browser navigates to `GET /api/auth/login/{provider}`; the API challenges the provider.
3. Provider redirects to `GET /api/auth/callback/{provider}`; the external principal rides a
   temporary `External` cookie scheme.
4. `UserService.GetOrCreateUserAsync` resolves the account: known `UserLogin` → that user; else a
   matching **verified** email → links the new provider (an **unverified** email match is refused —
   the takeover guard); else a brand-new `User` + fresh `Tenant` + owner `TenantMembership` are
   created atomically.
5. The API sets the refresh-token cookie and redirects to the client, which calls
   `POST /api/auth/refresh` to obtain its JWT access token.

Notes:
- Adding a provider = one `.AddXxx()` in `ServiceCollectionExtensions` + provider registration.
- A user can link multiple providers (rows in `UserLogin`); there is no `AspNetUserLogins`.
- **MFA step-up (ADR-012):** if the resolved user has MFA enabled, primary auth does **not** issue a
  full session — it returns a short-lived signed **challenge** and the callback redirects to
  `/login?mfa=<challenge>`; the client completes step-up via `POST /api/auth/mfa/verify` (a TOTP or
  recovery code). See §6 for enroll/manage.

### 2. Sign in via magic link (web, passwordless)
**Goal:** a user signs in without a password by clicking an emailed link.

Flow:
1. User requests a link: `POST /api/auth/magic-link/send` (always 200 — no account is created yet,
   so the response can't be used to probe for accounts).
2. `PasswordlessService` stores a single-use, hashed `LoginToken` (`purpose = magic-link`, 15 min
   default) and emails the URL via `IEmailSender` (Mailpit in dev).
3. User clicks it → `GET /api/auth/magic-link/verify`; the token is validated and **consumed**
   (`consumed_at`). The account is resolved/created now (`GetOrCreateByEmailAsync`, marked
   email-verified), provisioning a tenant if new.
4. The API sets the refresh cookie and the client refreshes into its session.

Notes:
- Only the token **hash** is stored; lifetime is `Auth:MagicLink:TokenLifespanMinutes`.
- Single-use: a redeemed or expired link no longer works.
- **MFA step-up (ADR-012):** an MFA-enabled user is redirected to `/login?mfa=<challenge>` instead of
  a session; the client completes it via `POST /api/auth/mfa/verify`.

### 3. Email OTP sign-in (web + native)
**Goal:** authenticate with a one-time 6-digit code — the only passwordless method on native
clients, where a magic-link email can't return to the app.

Flow:
1. User requests a code: `POST /api/auth/otp/send` (always 200). A hashed `LoginToken`
   (`purpose = otp`, 6 digits, 10 min default) is stored.
2. User enters the code: `POST /api/auth/otp/verify`. On match it's consumed and the session is
   issued; wrong codes increment `attempt_count` and lock out after the max (default 5).

Notes:
- **MFA step-up (ADR-012):** on a correct OTP, if the user has MFA enabled the API returns an
  `{ mfa_required, challenge }` response (JSON path) rather than a session; the client completes it via
  `POST /api/auth/mfa/verify` with a TOTP or recovery code. Enforced on **every** sign-in path.
- Email OTP here is the passwordless *primary* factor; authenticator-app **TOTP** is the optional
  *second* factor (enroll/manage in §6). SMS OTP is deferred (needs a phone field + an SMS provider).

### 4. New-tenant onboarding (automatic)
**Goal:** a newly authenticated user lands in their own tenant with no extra step.

Flow:
1. On first sign-in (any method) `UserService` creates the `User`, a fresh `Tenant`
   ("<name>'s Household"), and an owner `TenantMembership` in one transaction.
2. There is **no separate "create household" screen** and **no `tenant_id` on User** — tenancy is
   the membership. The user can rename the household later on `/household`.

### 5. Invite a member to the household
**Goal:** an owner **or admin** invites someone to their tenant.

Flow:
1. An owner or admin submits an email: `POST /api/household/invitations` (gated by
   `Permission.ManageMembers`, which both owner and admin hold — RBAC, ADR-009). A `TenantInvitation`
   is created (status `pending`, hashed token); inviting an existing member is refused (409), a pending
   invite for the same email is refreshed, not duplicated, and hitting the plan's seat cap returns
   **402 `seat_limit_reached`** (BILLING-5 — pending invites reserve a seat).
2. The raw token is returned once (revealed in the UI) **and** emailed as `/join?token=...`.
3. The invitee opens `/join`, signs in if needed, then `POST /api/household/invitations/accept`
   validates the token, moves their `TenantMembership` to the inviting tenant, and consumes the
   invite. A departing solo owner's empty tenant is dissolved (the re-home invariant).

Notes:
- `TenantInvitation.is_valid` = `status == pending AND !is_expired` (derived, not stored).
- An owner or admin can regenerate (new token; the old one dies) or revoke a pending invite.
- A user is always in exactly one tenant — accepting **moves** them, never adds a second.

### 6. Account settings (linked providers + language + theme)
**Goal:** manage per-user account settings on `/settings`.

- **Linked accounts:** `GET /api/auth/logins` lists linked providers;
  `POST /api/auth/link/{provider}` links another (refused if that identity belongs to someone else);
  `DELETE /api/auth/logins/{provider}` unlinks (email sign-in always remains, so this can't lock
  you out).
- **Language:** the switcher (Settings → Preferences card; also on the login page for pre-auth
  picks) persists the user's locale via `PUT /api/auth/locale`; it lands in the JWT on the next
  refresh and localizes the UI and outgoing emails. See `docs/LOCALIZATION.md`.
- **Theme (THEME-1 + PREFS-1):** a Light/Dark/System switcher in the header, Settings →
  Preferences, and the login page. Applies live via Bootstrap's `data-bs-theme`; persists
  device-locally (`localStorage["app_theme"]`, applied pre-paint by `theme.js`) and — signed in —
  server-side via `PUT /api/auth/theme` ("system" stored verbatim — ADR-022). See
  `docs/stories/theme.md`.
- **Preference sync (PREFS-1, ADR-022):** both preferences follow the *user*: reconciled on every
  sign-in (server value wins — theme applies live, a locale mismatch reloads once), and a
  device-local choice made before signing in is adopted into the user record when the account has
  none. See `docs/stories/prefs.md`.
- **MFA (authenticator TOTP; ADR-012):** enroll via `POST /api/auth/mfa/enroll` (returns an
  `otpauth://…` provisioning URI to render as a QR + one-time recovery codes), confirm possession with
  a valid code to enable, and disable/regenerate recovery codes from Settings. Once enabled, every
  sign-in path (§§1–3) requires the step-up (`POST /api/auth/mfa/verify`). The secret is encrypted at
  rest and never returned after enrollment; recovery codes are hashed + single-use, and are short,
  human-typeable `xxxxx-xxxxx` codes (unambiguous alphabet; entry is case/hyphen/space-insensitive).

---

## App-specific flows

> Translated from the donor's shipped behavior (PRD v2.0 §4, stories US-003–056). Each flow names
> the donor stories it comes from so the original acceptance criteria and tests can be consulted.
> Money is always **dual-currency** (₡ CRC + $ USD); "the rate" means the resolved USD→CRC rate
> (§8). Every flow below is household-scoped unless it says otherwise.

### 7. Configure the household's budget structure *(US-003, US-015 · ADR-V003)*
**Goal:** tell the app how this household's months and income work, once.

Flow:
1. On Settings → Budget, a member sets the **week-start weekday** (default Thursday) and the
   **month anchor**: `last_weekday_prev` (default — the month starts on the last Thursday of the
   previous calendar month, matching a weekly pay cycle), `first_weekday_current`, or
   `first_of_month` (monthly pay).
2. They set two incomes (**primary**, **secondary**), each with a **4-week** and a **5-week**
   default amount and **one currency** per income.
3. `PUT /api/budget-settings` saves the single `BudgetSettings` row for the household.

Notes:
- Settings are **per household** (the donor kept them on the user; the port moves them — ADR-V003).
- Changing settings affects **future** months only; existing months keep their stored weeks (§9).

### 8. Resolve the exchange rate *(US-014 · ADR-V006)*
**Goal:** always have a defensible USD→CRC rate, and never fabricate one.

Flow:
1. `GET /api/exchange-rate` (and every transaction create) resolves through the chain:
   **live quote** (a quote cached < 1 h counts as live — free-tier quota) → **stale provider cache**,
   flagged "as of …" → **the most recent transaction's `exchange_rate_used`** → **block** with a
   clear message (`exchange_rate_unavailable`, 400 on create / 503 on read).
2. The New Transaction form pre-fills the resolved rate; the user may override it.
3. Home shows the resolved rate with its provenance badge (live / "as of …" / from the last
   transaction / unavailable) — the member always knows whether the app is guessing.

Notes: a `conversion_rate ≤ 0` from the provider is treated as unavailable, never stored. With no
provider key configured the chain simply starts at the cache (empty on a fresh checkout) — the app
never errors on a missing key, it degrades to "unavailable".

### 9. Months and weeks exist only through transactions *(US-004, US-006, US-009, US-015 · ADR-V005)*
**Goal:** the user never creates or deletes a month; the data does.

Flow:
1. A transaction is created with a date. `GET /api/months/resolve?date=` (and the create path)
   finds the **anchor window** containing that date — which may be a *neighboring* calendar month
   (28 May → June under the default anchor).
2. If no month covers the window, one is **auto-created**: `year`, `month_number`, `week_count`
   (4 or 5, whatever fits before the next anchor), `week1_start_date`, the **weeks** materialized
   (7 days each, the last clamped), and the two incomes **snapshotted** from the 4w/5w defaults.
3. Deleting a month's **last** transaction deletes the month and its weeks.
4. `GET /api/months` lists months newest first; the dashboard's month selector navigates them.
   `PUT /api/months/{id}/income` edits that month's two incomes (amount + currency each).

Notes: validation and rate resolution happen **before** get-or-create, so a rejected request
never leaves an empty month. Refunds are transaction-bound and never keep a month alive.

### 10. Enter, edit and delete a transaction *(US-006–008, US-012, US-056 · ADR-V007)*
**Goal:** record money movement faithfully in both currencies.

Flow:
1. New Transaction: payee, **original amount + currency**, date, **category (required)**,
   **bank (required)**, payment method (`credit_card` default | `bank_account`), **class**:
   `budgeted` | `extraordinary` (UI: "Discretionary") | `unplanned_essential` (UI: "Unplanned")
   | `inflow` | `envelope_contribution`; optional rate override.
2. For `unplanned_essential`, an optional **refund expected %** spawns a derived `Refund` (§11).
   For `envelope_contribution`, an **envelope is required** and the method must be `bank_account`.
3. `POST /api/transactions` validates, resolves the rate, resolves/creates the month (§9), derives
   `amount_crc`/`amount_usd`, **freezes** `exchange_rate_used`, saves with `source = manual`.
4. Edit (`PUT /api/transactions/{id}`) re-derives amounts from the frozen rate — the rate itself
   **never changes**; re-syncs the refund. A derived inflow (`source = refund_realization`) is
   read-only on the edit page.
5. Delete (`DELETE`) removes the transaction and its refund, then the month if emptied.

Notes: categories/banks/envelopes referenced must belong to the household and be active; inactive
names still render on historical rows. `GET /api/months/{id}/transactions` lists newest first with
resolved category/bank names.

### 11. Expected refunds and their realization *(US-012, WU-2 · ADR-V007)*
**Goal:** track money you expect back from an unplanned essential, and book it when it lands.

Flow:
1. A `Refund` is **derived** from its transaction: `percentage × amounts` at the frozen rate,
   status `pending`. It is created/re-derived/removed by the transaction's create/update/delete —
   never edited directly except its status.
2. `GET /api/months/{id}/refunds` lists the month's refunds; `PUT /api/refunds/{id}` flips
   `pending → received` with a `received_date` (default today, never before the purchase), which
   **auto-creates a derived `inflow` transaction** (same amounts and rate, the source transaction's
   bank, `source = refund_realization`) **dated that day and filed in that day's month** — the month
   the money actually arrived in, auto-created if needed (ADR-V017). The refund stays listed under
   its purchase's month, showing the received date and linking the inflow's month. Flipping back
   removes the inflow (and its month if emptied).

Notes: refunds are informational — never in expenses or balance; the realized inflow is what
counts as income. The flip is a conditional update: concurrent flips create **exactly one** inflow
(the loser gets 409 `refund_status_conflict`).

### 12. Manage categories and banks *(US-010, US-013, US-019, US-047 · ADR-V008, ADR-V009)*
**Goal:** keep the household's own vocabulary for spend and sources.

Flow:
1. `GET /api/categories` / `GET /api/banks` — on the **first** call for a household, a small
   starter set is **seeded in the caller's locale** (7 example categories; Cash + 8 Costa Rican
   banks). `?include_inactive=true` for management.
2. Create (`POST`) with a case-insensitive unique name. A clash returns **409** `*_exists` (active)
   or `*_exists_inactive` + the id so the UI can offer **Reactivate** instead of duplicating.
3. Edit/deactivate (`PUT`). There is **no delete** — soft delete keeps historical rows readable.

Notes: seeded rows become ordinary user data (never retranslated). "Cash" is the fallback bank for
email vouchers whose bank can't be matched.

### 13. Manage envelopes (savings buckets) *(US-012 · ADR-V007)*
**Goal:** remember what is being set aside for large annual expenses.

Flow:
1. `GET/POST/PUT /api/envelopes` — name, **annual target** (CRC and/or USD), **reminder cadence**
   `monthly` | `five_week_months` (the extra-week nudge), soft delete.
2. Contributions are `envelope_contribution` transactions (§10). The dashboard shows, for each
   envelope applicable to the viewed month: target, contributed this month, remaining.

Notes: envelopes are carved out of expenses and balance — informational.

### 14. Manage the expense catalog (fixed & variable lines) *(US-016–019, US-054)*
**Goal:** define the budget baseline the dashboard compares actuals against.

Flow:
1. Budget page: two ordered lists — **fixed** (mortgage, subscriptions…) and **variable**
   (groceries, fuel…). Each line: name, budget CRC, budget USD, payment method, **required
   category**, **optional bank**, active flag.
2. `GET/POST/PUT /api/expenses/{fixed|variable}`; `PUT …/order` with `ordered_ids` reorders
   atomically.

Notes: the catalog is global across months (no per-month overrides — OUT). A supplied bank must be
household-owned and active; null = "Unassigned".

### 15. View the dashboard *(US-005, US-012, US-018, US-055)*
**Goal:** see the month truthfully at a glance, on a phone.

Flow:
1. `GET /api/months/{id}` returns month + weeks + the **dashboard summary**, computed by
   `DashboardSummaryService` (pure) from the month's transactions, the expense catalog, envelopes
   and the resolved rate.
2. Sections: **income** (primary, secondary, inflows folded in, total); **expense summary**
   (card total, account total, grand total, remainder); **fixed** and **variable** tables
   (budgeted vs actual per line + "other spending"); **weekly breakdowns** (budgeted and
   extraordinary, per week with date ranges); **unplanned** slice with subtotal; **refunds**;
   **envelopes** reminder; **bank × payment-method** breakdown (budgeted vs actual per cell,
   bankless lines in "Unassigned"); **balance** (current, remainder for debts, pending budgeted,
   actual remainder). Each figure is a CRC/USD pair.
3. A **review banner** shows the count of pending vouchers (§19).

Notes: actuals use each transaction's frozen rate; projections (pending budgeted, remainders) use
the live rate. Semantic colors: green under budget, red over — never the brand gold.

### 16. Category analysis report *(US-043, WU-4)*
**Goal:** answer "where did the money go?" by category and class.

Flow:
1. Reports → Category analysis: pick a month, or a date range. `GET /api/reports/category-analysis`
   returns CRC/USD spend per category, split by class, with a budget comparison for a single month.

Notes: the month window ends on the last stored week's `end_date` — never `week_count × 7`.

### 17. Export transactions as CSV *(US-044, WU-4)*
**Goal:** take the data anywhere.

Flow:
1. "Export CSV" on the dashboard (or any filtered list) calls `GET /api/transactions/export` with
   the same filters as §16 and downloads a CSV (rate formatted to 4 decimals to match storage).

Notes: downloads go through the platform's `IFileDownloadLauncher` seam so native shells work.

### 18. Connect a mailbox *(US-026, US-027, US-035, US-037, WU-5 · ADR-V010)*
**Goal:** let the app read voucher emails — and nothing else — from a member's inbox.

Flow:
1. Settings → Email → Connect: pick **Outlook** or **Gmail**. `GET /api/email/connections/authorize`
   builds the provider consent URL with **read-only** scopes (`Mail.Read` / `gmail.readonly` +
   `offline_access`, `openid email`) and an HMAC-signed state; the callback exchanges the code and
   stores the tokens **encrypted at rest** (Data Protection). One connection per provider per user.
2. Configure: folders (loaded live via `GET …/{id}/folders`), **sender/subject filters** (at least
   one; suggested filters pre-filled with the known BAC/BN sender addresses), `unread_only`
   (default on), `import_from` (don't flood on history), polling interval (5–1440 min), and an
   "ignore cursor — fetch all unread" toggle.
3. Edit/disconnect (`PUT`/`DELETE`). A dead refresh token flips the connection to
   `needs_reconsent`; the UI offers Reconnect.

Notes: the connection is **user-keyed** (ADR-V002) — it belongs to the member, survives their leaving
a household, and is erased with their account. Vouchers it produces land in the household the
member is in **at poll time**.

### 19. Vouchers are staged, deduplicated and suggested — never auto-booked *(US-025, US-028, US-029, US-034, WU-3 · ADR-V010)*
**Goal:** turn bank emails into review-queue drafts safely, in the background.

Flow:
1. A scheduled job ticks every minute and polls each active connection that is **due** by its own
   interval (or on "Sync now", `POST …/{id}/sync`). It reads only filter-matching mail since the
   cursor (with a 5-minute overlap), **paging** until exhausted; it never marks mail read.
2. Each message is routed by `(sender, subject)` to a bank extractor (BAC card voucher, BN card
   voucher, BN payment) → a `ParsedVoucher` (merchant, amount, currency, date, card, auth/ref).
   Spanish dates and `₡1.500`-style amounts are parsed correctly; a parse failure yields blanks.
3. A **fingerprint** (bank + auth|ref + amount + date, or the message id) is checked against the
   household's `IngestedVoucher` tombstones; duplicates are skipped. New ones are staged as
   `PendingVoucher` drafts with the bank matched by name (fallback Cash) and a **suggested
   category/class** from the household's merchant mappings (§20).
4. The cursor advances only past successfully processed messages; a poison message is capped at a
   few attempts and staged incomplete.

Notes: tombstones **persist through confirm and discard**, so a still-unread email never re-stages.
Provider 401 → refresh once → `needs_reconsent`; 429/5xx → skip this poll, cursor untouched.

### 20. Review and confirm vouchers *(US-030, US-033, US-038 · ADR-V010)*
**Goal:** one click from "the bank says you spent this" to a real transaction.

Flow:
1. The Review page lists pending drafts (`GET /api/pending-vouchers`, `/count` for the badge):
   bank, amount, date, payee read-only; **category** and **class** editable and pre-filled from
   the suggestion; a dual-currency preview.
2. **Confirm** (`POST …/{id}/confirm`) builds a `CreateTransactionCommand` from the draft + the
   user's choices and calls the same `TransactionService.CreateAsync` as manual entry
   (`source = email`), inside one transaction with a conditional `pending → confirmed` flip — a
   double-click yields one transaction (loser: 409 `not_pending`). Rate unavailable → 400, draft
   stays pending, nothing written.
3. **Discard** (`POST …/{id}/discard`) marks it `discarded` (tombstone stays).
4. Opt-in **"remember this merchant"** creates a merchant mapping (§20a) — never overwrites.

### 20a. Merchant → category suggestions *(US-029)*
**Goal:** the queue gets smarter without machine learning.

Flow:
1. Settings → Suggestions: `GET/POST/PUT/DELETE /api/merchant-mappings` — a **pattern** (matched
   case-insensitively as "contains"; the **longest** matching pattern wins), a category, and an
   optional suggested class (`budgeted` default | `extraordinary` | `unplanned_essential`).
2. Duplicate patterns (case-insensitive) → 409.

Notes: a mapping is *copied* onto drafts at staging time; deleting one never touches history.

### 21. Language *(US-045–047 · ADR-V009)*
The platform's locale preference (§6) drives the UI (English base, Spanish). User-entered names are
**never translated**; seeded catalog rows are localized **once**, at seeding.

## Sequence diagrams — the two call stacks that matter

Drawn to the same conventions as the platform's [`FLOWS.md`](FLOWS.md): one diagram per hot path,
divergences listed under it. These are the **planned** stacks (donor behavior on platform seams);
they are re-drawn from the code once the slices land.

### A. Create a transaction (manual entry) — §9–11, ADR-V005/V006/V007

```mermaid
sequenceDiagram
    autonumber
    participant C as Client
    participant EP as Transactions endpoint
    participant TS as TransactionService
    participant XR as IExchangeRateResolver
    participant WB as WeekBoundaryService (Core)
    participant UoW as IUnitOfWork
    participant Db as AppDbContext (tenant-filtered)
    C->>EP: POST /api/transactions {payee, amount, currency, date, category, bank, class, refund%?}
    EP->>TS: CreateAsync(command)
    TS->>Db: load BudgetSettings, validate category / bank / envelope are household-owned + active
    TS->>XR: ResolveAsync(USD→CRC)
    XR-->>TS: rate (live | stale-flagged | last transaction) or exchange_rate_unavailable → 400
    TS->>WB: GetBudgetMonthForDate(date, weekStart, anchor)
    WB-->>TS: (year, month) — the anchor window, not the calendar month
    TS->>UoW: BeginTransactionAsync
    TS->>Db: Month for (year, month)? else create it + weeks + income snapshot (4w/5w by week_count)
    Note over TS,Db: unique (tenant, year, month) race → savepoint rollback → re-read, retry once
    TS->>TS: CurrencyMath.DeriveAmounts → amount_crc, amount_usd; freeze exchange_rate_used
    TS->>Db: add Transaction (source = manual)
    TS->>Db: SyncRefundAsync — create / re-derive / remove the Refund from refund%
    TS->>UoW: CommitAsync
    Db-->>C: 201 TransactionResponse (both currencies, month id)
```

Divergences: any validation or rate failure happens **before** get-or-create, so a rejected request
never leaves an empty month. `envelope_contribution` without an envelope or with `credit_card` →
400 `invalid_request`. Delete runs the mirror: remove transaction (+ refund, + a realized inflow),
then `DeleteMonthIfEmptyAsync`. Refund `pending → received` is a separate conditional update that
creates exactly one derived `inflow` (loser: 409 `refund_status_conflict`).

### B. Voucher email → review queue → transaction — §18–20, ADR-V010

```mermaid
sequenceDiagram
    autonumber
    participant SJ as ScheduledJobsHost (1-min tick)
    participant PC as EmailPollCycle (IScheduledJob)
    participant TC as ITenantContext
    participant RD as IEmailReader (Graph | Gmail)
    participant VP as VoucherParser (Core + extractors)
    participant ST as VoucherStagingService
    participant Db as AppDbContext
    participant U as Member (Review page)
    participant TS as TransactionService
    SJ->>PC: RunAsync (system context — no tenant)
    PC->>Db: active EmailConnections due by their own interval (user-keyed, cross-tenant read)
    loop per connection
        PC->>Db: membership for connection.UserId → tenantId (skip + warn if none)
        PC->>TC: EnterTenant(tenantId)
        PC->>RD: FetchAsync(EmailQuery: folders, sender/subject filters, cursor − 5 min, unread)
        RD-->>PC: messages (paged to exhaustion; 401 → refresh once → needs_reconsent; 429/5xx → skip poll)
        loop per message
            PC->>VP: route (sender, subject) → extractor → ParsedVoucher (+ missing_fields)
            VP-->>ST: fingerprint = SHA-256(bank, auth|ref, amount, date) else message id
            ST->>Db: IngestedVoucher exists for (tenant, fingerprint)? → skip
            ST->>Db: bank by name (seed defaults if none; fallback Cash); longest merchant mapping → suggestion
            ST->>Db: add PendingVoucher (pending) + IngestedVoucher tombstone
        end
        PC->>Db: advance last_polled_at past the newest successfully processed message
    end
    U->>Db: GET /api/pending-vouchers — drafts with suggested category/class
    U->>TS: POST /api/pending-vouchers/{id}/confirm {category, class, edits, remember_merchant?}
    Note over TS,Db: one transaction: CreateAsync(source = email) then UPDATE … WHERE status = 'pending'
    TS-->>U: 200 (transaction id) — or 409 not_pending (lost the race, nothing written), 400 rate unavailable
```

Divergences: **nothing touches the budget until confirm.** Discard flips `pending → discarded`
(conditional — it cannot revert a concurrent confirm); the tombstone persists either way so a
still-unread email never re-stages. A poison message is retried a bounded number of times, then
staged as an incomplete draft (blanks are fixable in the queue). The "Sync now" button runs the
same cycle for one connection, in the caller's request context.

## Flow-to-rule cross-reference
| Flow | Key derived rule |
|------|------------------|
| Invite member (§5) | `TenantInvitation.IsValid` (`status == pending && !is_expired`) |
| Magic link / OTP sign-in (§2, §3) | `LoginToken` single-use (`consumed_at`) + expiry |
| Rate (§8) | Resolution chain: live → stale-flagged → last transaction → block |
| Months (§9) | Anchor window → month; `week_count` = weeks before the next anchor; weeks stored at creation |
| Transactions (§10) | `amount_crc`/`amount_usd` = `original` × frozen rate (2-dp fixed point); envelope class ⇒ `bank_account` + envelope |
| Refunds (§11) | `refund.amount_* = percentage × tx.amount_*`; `received` ⇔ a linked `inflow` exists |
| Catalogs (§12) | Uniqueness is case-insensitive per household; inactive ≠ deleted |
| Dashboard (§15) | Every figure derived from transactions + catalog + rate; inflows fold into income; envelope/refund carved out |
| Staging (§19) | Fingerprint = SHA-256(bank, auth\|ref, amount, date) else message id; dedup per household |
| Confirm (§20) | Conditional `pending → confirmed`; `source = email`; same month/rate rules as §9–10 |

## Out of scope
- SMS OTP — deferred until phone-based OTP is needed (no phone field / SMS provider yet).
- Social login beyond Google + Microsoft — infrastructure is provider-agnostic; add per-app.
- Data-driven bank definitions, LLM voucher fallback, Excel import, auto-classification, per-month
  budget overrides — see the OUT list in `PROJECT_BRIEF.md`.

_(Authenticator-app **TOTP MFA** is **implemented** — ADR-012, §6 + the sign-in step-up in §§1–3.)_
