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
