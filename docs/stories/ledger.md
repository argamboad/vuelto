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

**Context / notes:** the form's category picker creates a category in place (**+ New** → the shared
`CategoryPicker`, CATALOG-1 rules: active clash selects the existing entry, inactive clash offers
Reactivate) so entering a transaction never detours through Settings. `POST /api/transactions` validates everything first (payee, amount > 0, currency
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

### LEDGER-3 — Expected refunds and their realization *(P5b)*

**As a** household member
**I want** to note that part of an unplanned essential will come back, and to book it the day it does
**So that** the money I am owed is visible without being counted, and the money that lands counts as income

**Context / notes:** a `Refund` is **derived** from an `unplanned_essential` transaction flagged
`refund_expected` with `refund_percentage` (0 < p ≤ 100): its amounts are that percentage of the
transaction's frozen amounts (2 dp), status `pending`, one per transaction. The flag means nothing on
any other class (ignored, never an error). The refund **follows its transaction**: re-derived on every
edit (amounts, payee, date, month), removed when the flag clears or the class changes, deleted with
the transaction. The only thing edited directly is its **status**: `PUT /api/refunds/{id}` with
`received` (+ optional `received_date`, default today, never before the purchase) books a derived
`inflow` transaction — same amounts, the source's frozen rate, bank, category and payment method,
`source = refund_realization`, **dated the day the money landed and filed in that day's month**
(auto-created like any transaction's month; the refund itself stays in its purchase's month and
reports `received_date` + `inflow_month_id` — ADR-V017) — and links it; `pending` removes the inflow
(and its month if emptied). Same status is a no-op. The flip is a
**conditional update inside a unit-of-work scope** (ADR-V014): two concurrent flips book exactly one
inflow; the loser gets 409 `refund_status_conflict`. A realized refund's inflow **tracks re-derived
amounts** when the source is edited and disappears when the refund is dropped — never orphaned income.
Derived inflows are read-only through the transaction API (400 `derived_transaction`).
`GET /api/months/{id}/refunds` lists a month's refunds. Foreign ids are 404.

```gherkin
Scenario: A flagged unplanned essential spawns a pending refund
  When I POST 50000 CRC unplanned_essential at rate 500 with refund_expected true, refund_percentage 30
  Then the response carries refund_expected true / refund_percentage 30
  And GET /api/months/{month}/refunds lists one refund: 15000 CRC / 30 USD, status "pending"

Scenario: The flag needs a valid percentage, and only means something on an unplanned essential
  When I POST with refund_expected true and no percentage, 0, or 150
  Then I receive 400 "invalid_request" and nothing exists
  When I POST an extraordinary / inflow / budgeted row with the flag
  Then it is created without a refund

Scenario: The refund follows its transaction
  Given the refund above
  When I PUT original_amount 80000 → the refund is 40000 / 80
  When I PUT refund_expected false (or class budgeted) → the refund is gone
  When I PUT a date in July → the refund moves to July with its transaction
  When I DELETE the transaction → the refund (and the emptied month) are gone

Scenario: Marking received books a derived inflow
  When I PUT /api/refunds/{id} { status: "received", received_date: "2026-06-20" }
  Then an inflow exists: 15000 CRC / 30 USD, exchange_rate_used 500, the source's bank and category,
       source "refund_realization", dated 2026-06-20, and the refund carries inflow_transaction_id,
       received_date and inflow_month_id
  And PUT / DELETE on that inflow is 400 "derived_transaction"
  When I PUT { status: "received" } again → nothing changes (one inflow)
  When I PUT { status: "pending" } → the inflow is gone, the source stays

Scenario: The money lands in a later month (ADR-V017)
  Given the June purchase above with a pending refund
  When I PUT { status: "received", received_date: "2026-07-03" }
  Then the inflow is dated 2026-07-03 and lives in July (created if needed); June's transactions hold only the purchase
  And GET /api/months/{june}/refunds still lists the refund, with received_date 2026-07-03 and inflow_month_id = July
  When I PUT { status: "received" } with no date → the inflow is dated today
  When I PUT { status: "received", received_date: "2026-06-04" } (before the purchase) → 400 "invalid_request"
  When I PUT { status: "pending" } → the inflow is gone, and July with it if that emptied it

Scenario: A realized refund's inflow follows the source
  Given a received refund (inflow 25000)
  When I PUT the source to 80000 → the inflow is 40000 / 80
  When I PUT refund_expected false → refund and inflow are both gone
  When I DELETE the source → source, refund, inflow and the emptied month are gone

Scenario: Concurrent flips book exactly one inflow
  When two callers PUT { status: "received" } at the same time
  Then one receives 200, the other 409 "refund_status_conflict", and exactly one inflow exists

Scenario: The pages
  Given the class is Unplanned on the form
  Then a "Refund expected" switch appears; on, a percentage field and "Expected back: 15,000.00 CRC"
  And the month page lists expected refunds with a status badge and Mark received / Back to pending;
       a lost concurrent flip shows a message and reloads the list
```

**Definition of done (P5b):** tests first; Api.Tests slice tests on Postgres (derivation, invalid
percentage theory, ignored on other classes, follows edits / moves / deletes, received books the inflow
with the source's rate/bank/category, idempotent, revert, invalid / 404, derived read-only, source
delete/edit/clear after received, list, cross-tenant, two-context concurrency) + HTTP (401, the
flag → list → received → derived 400 → revert loop, 400/404), bUnit (fields only on Unplanned + payload
+ local validation; month page list, flip, conflict); migration `AddRefunds` with RLS DDL; contributor
extended; Postman folder 17 + refund fields on folder 16; QA-LED-05..06 + regenerated PDFs; EN/ES resx;
merged, app working.

**Definition of done (P5a):** tests first; Core.Tests (`CurrencyMath`, vocabulary), Api.Tests slice
tests on Postgres (lifecycle, snapshot by week count + defaults, reuse across the calendar boundary,
resolve without writing, rate-unresolvable writes nothing, manual override, delete-last, date move,
income edit/validation/404, the invalid theory, envelope rules, inactive names in history, list order,
cross-tenant read AND write negatives, the recent-rate tier, the contributor) + HTTP (401, the
create → month → list → income → delete → gone loop, 400 shape, uniform 404), bUnit page tests
(form create/edit/validation/rate states, month page list/income/delete, months list); migration
`AddLedger` with RLS DDL for three tables; contributor; Postman folders; QA-LED-01..04 + regenerated
PDFs; EN/ES resx; nav + Home entry points; merged, app working.
