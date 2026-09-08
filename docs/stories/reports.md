# Epic `REPORTS` — Category analysis & CSV export

> Registered epic key: **REPORTS**. Port slice **P8** (ADR-V001): the two read-only reporting features,
> re-homed from donor stories **US-043** (category analysis) and **US-044** (CSV export) plus the **WU-4**
> money-correctness fixes (A3 month window from the last week's end date, B5 four-decimal rate) —
> `vuelto-legacy/docs/stories/`. Decision context: ADR-V004 (dual currency), ADR-V005 (anchor-window
> months), ADR-V006 (frozen amounts, never recomputed), platform ADR-010 (files behind signed links),
> `FEATURES.md` §16–17.

Shared period rule (both stories): `month_id` **or** `from` + `to` (`yyyy-MM-dd`, inclusive) — never both,
never neither. A month's period is its anchor window: first week start → **the last week's end date**
(never `WeekCount × 7`, which over-counts next-month rows for anchors that clamp to the calendar month).
400 codes: `period_required`, `period_ambiguous`, `period_incomplete`, `period_invalid`. An unknown or
another household's `month_id` is a uniform **404**. Any member may read.

### REPORTS-1 — Analyse spend by category

> 2026-09-07 (owner request): each entry also carries `transaction_count` — how many rows are behind the sum — shown as a `#` column in the tables (Table view), totalled in the Total row.

**As a** household member reviewing spending patterns
**I want** actual spend grouped by category within each spending class, for a month or any date range
**So that** I can answer "how much did we spend on groceries last quarter?" beyond the current dashboard

**Context / notes:** `GET /api/reports/category-analysis`. Only the spending classes appear — `budgeted`,
`extraordinary`, `unplanned_essential`; `inflow` and `envelope_contribution` are not spending. Totals are
the frozen per-transaction amounts, 2 dp. Zero-spend categories are absent; entries sort by name; names
come from the **all-states** catalog so a deactivated category still labels its row. When the period is a
**single month** (`month_id`), every `budgeted` entry also carries `budgeted_crc` / `budgeted_usd` — the
sum of the active fixed + variable lines backing that category (null when none). A custom range omits them
(`single_month: false`): a monthly budget does not multiply cleanly across arbitrary ranges. Pure calculation
in Core (`CategoryAnalysisCalculator`); the slice handler only resolves the period and gathers rows.

```gherkin
Scenario: Spend grouped by class and category for a month
  Given June 2026 (window May 28 – Jun 24) with Groceries budgeted ₡60,000 (one fixed line)
  And transactions: Groceries budgeted ₡5,000 (May 28) + ₡3,000 (Jun 24), Dining extraordinary ₡2,000, an inflow ₡9,000, one on May 27 and one on Jun 25
  When I GET /api/reports/category-analysis?month_id={id}
  Then period is 2026-05-28 – 2026-06-24 and single_month is true
  And budgeted has Groceries total_crc 8000 with budgeted_crc 60000; extraordinary has Dining 2000; unplanned_essential is empty
  And the inflow and the two rows outside the window are not counted

Scenario: A custom range never shows budgets
  When I GET ?from=2026-01-01&to=2026-06-30
  Then single_month is false and budgeted_crc / budgeted_usd are null on every entry

Scenario: Period validation
  When I GET with no period → 400 period_required; with month_id AND from → 400 period_ambiguous
  When I GET with only from → 400 period_incomplete; with from > to or a non-ISO date → 400 period_invalid
  When I GET with another household's month_id → 404

Scenario: The Reports page
  Given I am signed in and open Reports (nav)
  Then the newest month loads: Budgeted (with "Budgeted (month)" and "Actual" columns, red over / green under, "—" without a line, totals row), Discretionary, Unplanned
  When I switch to "Date range", pick From and To and Load → the range loads without budget columns
  When I pick From after To → "From must not be after To" and nothing loads
```

### REPORTS-2 — Export the transactions of a period as CSV

**As a** household member who analyses spending outside the app
**I want** to download every transaction of the shown period as a CSV file
**So that** I can open it in a spreadsheet, archive it, or share it

**Context / notes:** `POST /api/reports/transactions/export` with the shared period rule plus optional
`category_id` and `class` filters. The CSV (RFC 4180, UTF-8, CRLF) has the fixed columns `date, payee,
category, class, amount_crc, amount_usd, exchange_rate_used, payment_method, bank, source`; amounts are plain
decimals with two places and no symbol; the frozen rate keeps **four** places (NUMERIC(10,4)) so both
amounts can be reproduced from it; dates are `yyyy-MM-dd`; rows are unpaginated and ordered date desc,
created desc; an empty result is a header-only file, never a 404. **Delivery follows the platform's file
seam (ADR-010):** the CSV is stored through `IFileStorage` under `exports/transactions/…/transactions-
{today}.csv` and the response carries a 15-minute signed `download_url`, `file_name` and `row_count`. The
pages hand that link to the shared `IFileDownloadLauncher` — a browser downloads it natively, the MAUI
shells open the OS share sheet (NATIVE-3) — so one code path serves web and native. Today's date comes
from the injected clock.

```gherkin
Scenario: The CSV of a month
  Given June 2026 with rows on Jun 5 ("Older") and two on Jun 20 (saved five minutes apart), one in an inactive category
  When I POST /api/reports/transactions/export?month_id={id}
  Then 200 with download_url, file_name "transactions-<today>.csv", row_count 3
  And GET download_url (no token needed — the link is the authorization) returns text/csv with Content-Disposition attachment; filename="transactions-<today>.csv"
  And line 1 is the header, line 2 the later-saved Jun 20 row naming the inactive category, then the other Jun 20 row, then Jun 5
  And a payee like Café, "El" Punto is quoted with doubled quotes; exchange_rate_used reads 500.0000

Scenario: Filters and the empty file
  When I add category_id → only that category; class=Extraordinary → normalized, only that class
  When nothing matches → 200, row_count 0, header-only CSV

Scenario: Isolation and validation
  When another household has rows in the same period → they never appear
  When the period is missing or ambiguous → the same 400 codes as the analysis; a foreign month_id → 404

Scenario: Export buttons
  Given I am on Reports with a period shown → "Export CSV" POSTs exactly that period and the download starts; "CSV ready — N rows"
  Given I am on a month page → "Export CSV" exports that month the same way
  When no month exists yet → the Reports export button is disabled
```

**Out of scope:** per-month budget grids across ranges (deferred by the donor too); PDF or Excel formats;
scheduled/emailed reports.

**Definition of done:** tests first; Core.Tests (`CategoryAnalysisCalculatorTests` — the donor
`ReportServiceTests` re-homed as a pure calculation; `TransactionCsvWriterTests` — columns, 2/4 dp, quoting,
header-only); Api.Tests slice on Postgres (period codes theory, anchor window incl. the first-of-month
clamp, unknown/foreign not-found, analysis with names + budget, range without budget + isolation, export
ordering/filters/header-only/isolation/stored key + link lifetime) + HTTP (401, 400, 404, analysis, export →
anonymous download of the signed link with the exact CSV); bUnit (newest month + columns + tone, range
validation + load, export POST + launcher, empty state, month-page export); no entity, no migration; Postman
folder; QA-REP-01..02 + regenerated PDFs; EN/ES resx; nav entry; merged, app working.

### REPORTS-3 — Charts for the category analysis *(owner request, 2026-09-04)* ✅

**As a** household member
**I want** to see the category analysis as pictures as well as numbers
**So that** the shape of a month — what dominates, what is over budget — is visible at a glance

**Context / notes:** presentation only — the report endpoint is unchanged. Charts are **inline SVG**
components in `Shared.Ui/Components/Charts` (no JS library; same markup on web and MAUI; colours from
Bootstrap tokens so dark mode follows; bUnit can assert every bar). A **View: Table / Chart** toggle
and a **₡/$** switch sit in the period card, remembered **per device** (`appUi.getPref/setPref` in
`js/ui.js`; never account data). Chart view: a **Spend by class** donut (budgeted / discretionary /
unplanned, with shares) and one **horizontal bar chart per class**, largest first; on the budgeted
class a line's budget is a muted track under the actual, which turns red past it — judged in the
line's own currency, so a $-budgeted line carries a track only while the chart is in $. The total is
printed under each chart. Tables are untouched.

**Income vs spend donut** *(owner request, 2026-09-05)*: beside "Spend by class", a second donut measures
the same three class slices against the **month's income** — the dashboard's own definition (both
configured incomes at today's rate + inflows), now the shared `Core` `IncomeCalculator` so the two pages
can never disagree — with a fourth, muted **Remaining** slice and the income in the hole. Overspent → no
remaining slice and a red "Over income by X" line. The endpoint gains `income` (`{crc, usd}`): present
for a single month with a resolvable rate, **null** for a date range (income is per month) or when the
ADR-V006 chain is empty (the spend report never depends on the rate; the card then says so).

**Income vs budget donut** *(owner request, same day)*: a third card — what the **active budget lines
commit** of the income (`Budget lines`) and what is left **Uncommitted**; the plan exceeding the income →
no uncommitted slice and a red "Budget exceeds income by X". Each line is single-currency (EXPENSES-1), so
the endpoint converts them at the same resolved rate — the shared `Core` `BudgetTotals` (now also behind
the dashboard's budget column) — and returns `budget_total` (`{crc, usd}`), null exactly when `income`
is. The three donut cards share one row (a third each) in month mode; in range mode only "Spend by
class" remains, at full width.

```gherkin
Scenario: Chart view draws the same rows
  Given the July report is on screen as tables
  When I click View → Chart
  Then the budgeted class shows one bar per category, largest first, with a budget track where the line is ₡-budgeted
  And an over-budget actual is red, the total is printed under the chart, and a donut splits the spend by class
  When I click $ → the bars, totals and donut are in dollars, and only $-budgeted lines keep a track
  When I reload → the view and currency are as I left them (this device only)

Scenario: Income vs spend
  Given July's income is ₡200,000 and ₡99,704.87 was spent
  When I view July as charts
  Then an "Income vs spend" donut shows the three class slices plus Remaining ₡100,295 with ₡200,000 in the hole
  When the month's spend exceeds its income → the Remaining slice is gone and "Over income by ₡…" is shown in red
  When no exchange rate can be resolved → the card says income needs today's rate; the spend donut still draws
  When I switch to a date range → there is no income card (income is per month)

Scenario: Income vs budget
  Given July's income is ₡200,000 and the active lines add up to ₡150,000
  When I view July as charts
  Then an "Income vs budget" donut shows Budget lines ₡150,000 and Uncommitted ₡50,000 with ₡200,000 in the hole
  When the lines add up to more than the income → the Uncommitted slice is gone and "Budget exceeds income by ₡…" is shown in red
  And a $-budgeted line counts at today's rate, so the plan is one number in either currency
```

---

### REPORTS-4 — Pace, month by month, and where the money leaves from *(owner request, 2026-09-05)* ✅

**As a** household member
**I want** to see whether this month is on pace, how the months compare, and which banks and payment
methods the money leaves through
**So that** the report answers "am I ahead or behind", "am I getting better" and "card or account"
at a glance, not only "how much"

**Context / notes:** three more chart cards, same inline-SVG family, same ₡/$ switch.
- **Pace** (month mode): `LineChart` — cumulative spend as a step line through the days that had spend
  (`spend_by_day` on the analysis response, single month only), the straight **plan** line from zero to
  `budget_total` at the last day, a dashed **today** marker clamped to the window, red once the running
  total passes the plan. Caption "{elapsed}% of the month elapsed · {spent}% of the plan spent". No
  rate → the actual line still draws, no plan, and the card says why.
- **Month by month** (month mode): new `GET /api/reports/months-trend?count=` (1–36, default 12) —
  the household's last months by anchor date, oldest first, each with `spend` (the three expense
  classes, frozen amounts) and `income` (the shared `IncomeCalculator` at today's rate; null when no
  rate — `rate_available` says so). Drawn with the existing `BarChart`: spend on an **income track**,
  red when a month overspent, legend relabelled Income / Spend / "Spent more than the income"
  (`ActualLabel`/`BudgetLabel`/`OverLabel` parameters). The gap on each bar is that month's remaining.
  Loaded only when Chart view is on. Pure `MonthTrendCalculator` in Core.
- **Spend by bank** and **Card vs account** (any period): `by_bank` (largest first, all-states names;
  a nameless bank reads "Unknown bank") and `by_method` (card, then account — fixed order so the colours
  stay put) on the analysis response, from the same `CategoryAnalysisCalculator`. Two donuts under the
  others; they follow a date range too, since they need no rate.

```gherkin
Scenario: Pace
  Given July runs Jun 25 – Jul 29 with a ₡150,000 plan and spend on Jun 26, Jul 3 and Jul 20
  When I view July as charts on Jul 15
  Then the pace card shows three points on a step line, the plan line, a today marker at day 21
  And the caption reads "60% of the month elapsed · 66% of the plan spent"
  When today is after the month → "100% of the month elapsed"; when no rate → no plan line and the card says so

Scenario: Month by month
  Given May overspent (₡250,000 of ₡200,000) and June and July did not
  When I view charts
  Then "Month by month" has one bar per month, oldest first, spend on an income track, May in red
  And with no rate the bars show spend only and the card says so

Scenario: Where the money leaves from
  Given July's spend was BAC ₡90,000 and a bank with no name ₡9,704.87, card ₡80,000 and account ₡19,704.87
  When I view charts (month or date range)
  Then "Spend by bank" lists BAC then Unknown bank, and "Card vs account" lists Credit card then Bank account
```

### REPORTS-5 — Budgeted vs spent by payment method *(owner question, 2026-09-08)* ✅

**As a** household member
**I want** to see how much of the month's plan is meant to be paid by credit card and how much from the
bank account, against what actually left each way
**So that** I know what the card statement and the account should absorb before the month ends

**Context / notes:** two homes. On the **dashboard**, the bank × method table is grouped by payment method
(card first) with a **subtotal row per method** and a grand total — no API change, the summary already
carries every cell. In **Reports** chart view, "Budgeted vs spent, by payment method" sits under the
Card vs account donut: one bar pair per method, the spend (frozen amounts) against the plan cut the same
way (each line's own payment method, converted at today's rate like every budget figure — ADR-V019).
Single month with a rate only; a date range has no plan. API: `budget_by_method[]` on the analysis
(`BudgetTotals.PlannedByMethod`).

```gherkin
Scenario: The plan cut by payment method
  Given June has a ₡60,000 Supermarket line paid by card and ₡8,000 spent on it by card
  When I open Reports chart view for June
  Then "Budgeted vs spent, by payment method" shows Credit card ₡8,000 against ₡60,000 and no Bank account bar
  And GET /api/reports/category-analysis?month_id=… carries budget_by_method = [{ credit_card, 60000, 120 }]
  When I switch to a date range
  Then the bars are gone (budget_by_method is null) while the Card vs account donut stays
```
