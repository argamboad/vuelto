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
names still render). Any member may edit. **CARDS-1 (2026-09-08):** an optional `card_id` — an active card of
the household (400 otherwise); the list carries `card_name`; the month page shows, sorts and filters by card.
**Notes (2026-09-08, owner request):** an optional `notes` field (≤ 250 characters, trimmed, blank = null, over the cap
= 400) on create, update, the row, the month list and the CSV; the voucher confirm accepts it too. The month page shows
a note icon on the payee whose hover text is the note; opening the transaction shows it in full.

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
  And (2026-09-07) the transactions table sorts by Date, Payee, Category, Bank or Class from its headers
       and filters by date range, payee text, category, bank and class — on the loaded rows, no request
  When I open New transaction, the rate is pre-filled from today's quote (or I am told to enter one),
       the date announces "Goes to July 2026 — a new month will be created", and Save takes me to the month
  When I open Edit, the rate is shown frozen and never sent back
  And (2026-09-08) New/Edit carry an optional Notes box with a 0/250 counter; a row with a note shows an icon on
       the month page whose hover text is the note and whose tap opens the transaction; the review queue's
       confirm card offers the same box; the CSV ends with a notes column
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

### LEDGER-4 — A refund carries its case number and a note *(owner request, 2026-09-11)* ✅

**As** a household member, **I want** a case number and a note on an expected refund, **so that** I can chase an
insurance claim by its reference and remember why the money is owed at all.

**Context / notes:** a refund is **derived** — `SyncRefundAsync` rewrites its month, payee, date, percentage and
amounts every time the transaction changes — so the two user-owned fields sit beside that and are never touched
by the re-derivation. They are lost only when "refund expected" is unticked, which deletes the row outright.
`Refund.CaseNumber` (≤ 60) and `Refund.Notes` (≤ 250, the same cap as a transaction's note) move through
`PUT /api/refunds/{id}/details { case_number, notes }`, deliberately **off** the status route: there an omitted
field would be ambiguous between "leave alone" and "clear", and here blank means clear. The month page's
Expected refunds table gains a **Case No.** column and a note icon on the payee (the same affordance a
transaction's note has), with an inline **Edit** row for both. Any household member may edit them (ADR-V002);
a foreign or unknown id is the uniform 404.

```gherkin
Scenario: An insurance claim, and a loan to a son
  Given an unplanned essential expecting a refund
  When I set its Case No. to "CASE-2026-4471" and its note to "lent to Diego"
  Then the refund row shows the case number and a note icon reading it on hover
  When I edit the transaction's amount
  Then the refund's amounts re-derive and both fields are still there
  When I clear them
  Then blank stores null, not an empty string
```

### LEDGER-5 — A discretionary purchase can expect a refund *(owner request, 2026-10-09 · #201)* ✅

**As** a household member, **I want** to expect a refund on a discretionary purchase too, **so that** money I'll get
back on something I chose to buy is tracked like an insurance refund is.

**Context / notes:** ADR-V025. The refund expectation is an **attribute** of a transaction, not a sixth class: the
class says which part of the plan the money came from, a refund says some of it comes back, and a discretionary
purchase you'll be reimbursed for is still discretionary spend. It rides on `unplanned_essential` and
`extraordinary` only — not `budgeted` ("why would I budget something I expect a refund for?"). The flag on any other
class is now a **400** `invalid_request` (it used to be silently ignored — fail closed). The dashboard summary splits
the pending refunds by the transaction's class (`unplanned_refunds`, `discretionary_refunds`; `refunds_total` stays
the sum), so the Reports page and the PDF give each tile its own "refundable" figure.

```gherkin
Scenario: A refund on a discretionary purchase
  Given a discretionary purchase of ₡50,000
  When I tick "refund expected" and enter 30 %
  Then a pending refund of ₡15,000 is listed under the month's refunds
  And the Discretionary tile on Reports says "₡15,000 refundable", the Unplanned tile does not

Scenario: Not on budgeted spending
  Given a budgeted purchase
  Then the form offers no refund fields
  And an API call flagging it is refused with 400 invalid_request, and nothing is written

Scenario: Moving between the two classes keeps the refund
  Given an unplanned essential expecting 50 %
  When I change it to discretionary
  Then the refund stays, re-derived as before
```

### LEDGER-6 — A refund is an amount, typed directly or as a percentage; a received one is locked *(owner request, 2026-10-09 · #202)* ✅

**As** a household member, **I want** to enter an expected refund as a fixed amount or as a percentage, **so that** a
"₡12,000 back from the insurer" and a "half of it back" are both quick to type — and **I want** a refund I already
received to stay put, **so that** an edit can't quietly rewrite money that's already booked.

**Context / notes:** ADR-V026. The amount is stored (in the purchase's currency; the other side at the frozen rate); a
percentage is only how the form computes it — `refund_amount` replaces `refund_percentage` on `POST/PUT
/api/transactions` and on the voucher confirm, and `refund_status` rides on the transaction response. A purchase
amount edit keeps the refund's amount (a purchase below it is a 400). `Refunds.Percentage` is nullable and no longer
written (`RefundPercentageOptional`, expand-only); the refunds list computes the share. A **received** refund is locked:
amount change, switching it off, an incompatible class or deleting the purchase → 409 `refund_status_conflict`; put it
back to pending first (that path stays, owner-confirmed).

```gherkin
Scenario: A fixed refund
  Given a purchase of ₡80,000
  When I tick "refund expected", keep "₡" and type 12000
  Then the refund is ₡12,000 and the hint says "15 % of ₡80,000"

Scenario: A refund typed as a percentage is stored as its amount
  Given a purchase of ₡50,000
  When I switch to "%" and type 30
  Then the hint says "Expected back: ₡15,000.00" and ₡15,000 is what is saved

Scenario: Correcting it by percentage later
  Given that refund of ₡15,000 on a purchase now of ₡60,000
  When I edit the transaction
  Then the refund opens on ₡15,000 ("25 % of ₡60,000")
  When I switch to "%" and type 20
  Then ₡12,000 is saved

Scenario: A received refund is locked
  Given a refund marked received
  When I edit its purchase
  Then the refund switch and amount are disabled, saying to mark it back to pending first
  And deleting the purchase is refused with 409 refund_status_conflict
  When I put the refund back to pending on the month page, correct it and mark it received again
  Then the booked income follows the new amount
```

### LEDGER-7 — Refunds show what came in and what is still out *(owner request, 2026-10-09 · #206)* ✅

**As** a household member, **I want** the month's refunds to show the total received and the total still pending,
**so that** I can see at a glance how much of what I'm owed has come back.

**Context / notes:** a pure `RefundTotals` in Core (golden rule 4: computed, never stored), both currencies, 2 dp;
`GET /api/months/{id}/refunds` now answers `{ refunds, totals }` with `totals = { pending, received, expected }`. The
month page's Expected refunds header shows *Received · Pending · of expected* in the display currency.

```gherkin
Scenario: A month with one refund in, one out
  Given a ₡15,000 refund pending and a ₡6,170 refund received this month
  When I open the month
  Then the refunds header reads Received ₡6,170.00 · Pending ₡15,000.00 · of ₡21,170.00
```

### LEDGER-8 — Every refund across months, filtered, grouped, and marked received together *(owner request, 2026-10-09 · #208)* ✅

**As** a household member, **I want** one place with every refund I'm owed — filterable by payee and status, groupable,
with how long each has been out — **so that** I know what to chase and can tick off a deposit that paid several at once.

**Context / notes:** `GET /api/refunds?status=&payee=&from=&to=` (Ledger slice, tenant-scoped through `Query()`):
oldest purchase first, each with its budget month and `pending_days`, the totals (`RefundTotals`) over every match,
`truncated` past 1000 rows instead of paging — grouping and subtotals happen on the page, and a household's refunds
are few. `/refunds` page: status chips (Pending default · Received · All), payee search, From / To, Group by (none ·
payee · status · month) with per-group received / pending, multi-select + one received date → one guarded
`PUT /api/refunds/{id}` per refund (ADR-V007/V014: each books its own inflow; failures are named, the rest land).
Status and grouping persist per device (`appUi` prefs `refunds.status`, `refunds.group`). Linked from the month
page's refunds header and under the dashboard's forecast step.

```gherkin
Scenario: What's still out, oldest first
  Given a pending refund from June and one from July
  When I open Refunds
  Then June's comes first, with "pending 90 days"

Scenario: One deposit paid two refunds
  Given two pending refunds
  When I tick both, set the received date to September 1 and click "Mark them received"
  Then both are received on September 1, each with its own income row in that month

Scenario: Group by payee
  When I group by payee
  Then "Hospital" and "hospital" are one group showing its received and its pending
```
