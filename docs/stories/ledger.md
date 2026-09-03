# Epic `LEDGER` — Months, weeks & transactions

> Registered epic key: **LEDGER**. Port slice **P5** (ADR-V001), delivered in two PRs: **P5a** =
> LEDGER-1 + LEDGER-2 below (the core loop), **P5b** = LEDGER-3 (expected refunds and their realization
> as inflows — pinned at the end). Re-homed from donor stories **US-004 / US-006 / US-007 / US-008 /
> US-009 / US-015** (+ US-012's class model, US-056 inflow entry, WU-2/WU-4 fixes) —
> `vuelto-legacy/docs/stories/`. Decision context: ADR-V005 (pay-cycle months, auto lifecycle, stored
> weeks, two incomes), ADR-V006 (frozen rate), ADR-V007 (five classes, required bank + category,
> envelope contributions), ADR-V008 (uniform 404), `FEATURES.md` §9–10, `DATA_MODEL.md` → `Month`,
> `Week`, `Transaction`.

### LEDGER-1 — Months exist only through transactions

**As a** household member
**I want** budget months that follow our pay cycle and appear or disappear with our transactions
**So that** I never create, fix, or delete a month by hand and history never re-slices itself

**Context / notes:** a date belongs to the month whose **anchor window** contains it — which may be a
neighbouring calendar month (28 May → June under the default "last Thursday of the previous month").
`GET /api/months/resolve?date=` answers for any date without writing: the existing month, or the one
that *would* be created (`is_new`). When a transaction lands in an uncovered window the month is
**auto-created** with its `week_count` (4 | 5), `week1_start_date`, its **weeks materialized** (7 days
each, the last clamped to the day before the next anchor) and the two **incomes snapshotted** from
`BudgetSettings` (the 5-week defaults for a 5-week month; the platform defaults when the household
never saved). Boundaries are computed from the settings **at creation** and stored — a later settings
change never touches existing months. Deleting (or moving) a month's **last** transaction deletes the
month and its weeks. Month income stays editable per month. Months store no exchange rate.

```gherkin
Scenario: Resolve never writes
  Given my household has no months and the default settings
  When I GET /api/months/resolve?date=2026-07-10
  Then I receive { month_id: null, year: 2026, month_number: 7, is_new: true } and no month exists

Scenario: The first transaction creates its month with weeks and an income snapshot
  Given my settings say Thursday / last_weekday_prev, 5-week primary income 3750 USD, secondary 312500 CRC
  When I create a transaction dated 2026-07-10
  Then a July 2026 month exists with week_count 5, week1_start_date 2026-06-25, five weeks ending 2026-07-29
  And primary income 3750 USD and secondary income 312500 CRC

Scenario: A covered date reuses its month across the calendar boundary
  Given a June 2026 month (window 2026-05-28 – 2026-06-24)
  When I create a transaction dated 2026-05-30
  Then it lands in June 2026 and no second month is created

Scenario: A rejected transaction never leaves an empty month
  Given no exchange rate can be resolved and I gave none
  When I create a transaction dated 2026-07-10
  Then I receive 400 "exchange_rate_unavailable" and no month or transaction exists

Scenario: Months leave with their last transaction
  Given June 2026 holds two transactions
  When I delete one
  Then the month stays
  When I delete the other
  Then the month and its weeks are gone and GET /api/months/{id} is 404

Scenario: A date fix moves the transaction and cleans up
  Given June 2026 holds one transaction created at rate 500
  When I change its date to 2026-07-10
  Then July 2026 is created, the transaction moves there with exchange_rate_used still 500, and June is gone

Scenario: Month income is editable and validated
  When I PUT /api/months/{id}/income with amounts and currencies
  Then both incomes change; a negative amount or an unknown currency is 400 "invalid_request"; an unknown id is 404
```

### LEDGER-2 — Enter, edit and delete a transaction

**As a** household member
**I want** to record money movement with its payee, bank, category, class and amount
**So that** both currencies are captured faithfully at the rate of that day, forever

**Context / notes:** `POST /api/transactions` validates everything first (payee, amount > 0, currency
CRC | USD, date, class, payment method, **required bank and category** that exist in the household and
are active; an `envelope_contribution` needs an active envelope and `bank_account`, any other class
must not carry an envelope), settles the rate (a manual `exchange_rate` override wins; else the
ADR-V006 chain; nothing → 400 `exchange_rate_unavailable`), resolves or stages the month, derives
`amount_crc` / `amount_usd` (2 dp) and **freezes** `exchange_rate_used`; month, weeks and transaction
are saved together. `PUT` re-derives the amounts **from the frozen rate** and re-resolves the month on
a date change. `DELETE` is a hard delete. Rows with `source != manual` (email confirms, refund
realizations) are read-only here → 400 `derived_transaction`. Another household's id is **404**.
`GET /api/months/{id}/transactions` lists newest first with category/bank names resolved (inactive
names still render). Any member may edit.

```gherkin
Scenario: Creating a transaction derives both amounts at the frozen rate
  Given the resolved rate is 500
  When I POST payee "AutoMercado", 50000 CRC on 2026-06-05, category Groceries, bank Cash, class budgeted
  Then I receive 201 with amount_crc 50000, amount_usd 100, exchange_rate_used 500, source "manual"

Scenario: A manual rate override wins
  When I POST 20 USD with exchange_rate 510
  Then amount_crc is 10200 and exchange_rate_used 510 — even when the chain has no rate

Scenario: Invalid requests write nothing
  When I POST with a blank payee, a zero amount, EUR, no date, an unknown class or payment method,
       no bank, no category, an unknown or inactive bank/category, a non-positive exchange_rate,
       an envelope on a non-contribution, a contribution without an envelope or with credit_card
  Then I receive 400 "invalid_request" naming the field, and nothing exists

Scenario: Editing re-derives from the frozen rate
  Given a transaction created at rate 500
  When I PUT original_amount 100000 CRC
  Then amount_usd is 200 and exchange_rate_used is still 500

Scenario: Foreign ids do not exist
  When I GET / PUT / DELETE /api/transactions/{another household's id}
  Then I receive 404 and their row is unchanged

Scenario: The pages
  Given I am signed in
  When I open Months (nav) I see my months newest first with their week counts; a month page shows its
       weeks, editable income, and its transactions with Edit and a two-step Delete
  When I open New transaction, the rate is pre-filled from today's quote (or I am told to enter one),
       the date announces "Goes to July 2026 — a new month will be created", and Save takes me to the month
  When I open Edit, the rate is shown frozen and never sent back
```

**Out of scope (P5a):** LEDGER-3 below; the dashboard summary on a month read (P6); the voucher
review path that books `source = email` rows (P10).

### LEDGER-3 — Expected refunds and their realization *(P5b — pinned, not built)*

An `unplanned_essential` transaction flagged "refund expected" with a percentage spawns a derived
`Refund` (amounts = % × the frozen amounts) that follows every edit and dies with its transaction;
flipping it `pending → received` creates a read-only `inflow` (`source = refund_realization`) under a
conditional update so concurrent flips yield exactly one inflow; flipping back removes it (ADR-V007,
ADR-V014; donor US-012, WU-2). Adds `refund_expected` / `refund_percentage` to the transaction DTOs and
the form, and `/api/refunds`.

**Definition of done (P5a):** tests first; Core.Tests (`CurrencyMath`, vocabulary), Api.Tests slice
tests on Postgres (lifecycle, snapshot by week count + defaults, reuse across the calendar boundary,
resolve without writing, rate-unresolvable writes nothing, manual override, delete-last, date move,
income edit/validation/404, the invalid theory, envelope rules, inactive names in history, list order,
cross-tenant read AND write negatives, the recent-rate tier, the contributor) + HTTP (401, the
create → month → list → income → delete → gone loop, 400 shape, uniform 404), bUnit page tests
(form create/edit/validation/rate states, month page list/income/delete, months list); migration
`AddLedger` with RLS DDL for three tables; contributor; Postman folders; QA-LED-01..04 + regenerated
PDFs; EN/ES resx; nav + Home entry points; merged, app working.
