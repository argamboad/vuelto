# Epic `INCOME` — Income lines: who earns what, and how it is paid

> Registered epic key: **INCOME**. Owner request (2026-09-16). Plan: `docs/REPORTS_PDF_INCOME_PLAN.md` §4
> (decisions D4–D6, D9, the field table, the data-safety rules §4a); decision record **ADR-V023**. Supersedes the
> income half of ADR-V003 (4-week / 5-week defaults on the budget settings) and ADR-V005's "two incomes per month".
> The budget unit is unchanged: the pay-cycle month (ADR-V005).

### INCOME-1 — Keep the household's income lines

**As a** household member
**I want** each income listed as its own line — whose it is, its currency, whether it is fixed or an estimate, and
how often it is paid
**So that** a weekly salary, a monthly salary and a variable side income all land correctly in each month's plan,
without typing four-week and five-week figures

**Context / notes:**
- **Line** (`IncomeLine`, household-scoped, RLS): `name` (≤ 100, unique per household case-insensitively — 409
  `income_exists`, or `income_exists_inactive` + `existing_id` / `existing_name` for the reactivation offer),
  `member_user_id` (optional; must be a current member of the household), `currency` (CRC | USD), `kind`
  (`fixed` | `variable`), `pay_period` (`weekly` | `biweekly` | `monthly`), `amount` (per period, > 0, 2 dp),
  `pay_days` (biweekly only: two different days of the month 1–31, 31 = the month's last day; default `[15, 31]`),
  `is_active`, `sort_order` (appended on create, owned by `PUT /api/incomes/order` which must name exactly the active
  set), `needs_review` (set only by the migration for a line whose old 4w/5w pair didn't fit a pay period; cleared by
  any update). Never seeded. Any member may edit any line (the budget is shared).
- **API** (`/api/incomes`): `GET` (`include_inactive`), `POST`, `PUT /{id}`, `PUT /order`. Another household's id → 404.
- **The month's income** (`MonthIncome`, RLS, deleted with its month): one row per active line, **snapshotted when the
  month is created** — `amount` and `planned_amount` = the line's amount × the paydays inside the month's window:
  weekly → × the month's week count; biweekly → × the pay days that fall between the first week's start and the last
  week's end; monthly → × 1. A line whose member has **left the household** is skipped (computed at snapshot time —
  nothing is flipped on the line). Rows carry the line's name, member and currency as they were, so renaming a line
  never rewrites history.
- **Editing a month** (`PUT /api/months/{id}/income` with `rows`): the full list — an existing row by `id` changes its
  `label` / `amount` / `currency`; a row without `id` is a one-off for that month (`income_line_id` null,
  `planned_amount` null); a row left out is removed. `GET /api/months/{id}` returns `income_rows`.
- **Totals are unchanged in shape:** `IncomeCalculator` sums the month's rows (each converted at the day's rate by the
  ADR-V019 direction rule) plus inflows. The dashboard summary and the reports keep `income_total`; the dashboard's
  `income_primary` / `income_secondary` are replaced by `income_lines` (label, member, pair) and `income_inflows`.
- **Budget settings** keep only the week start and the month anchor; the income fields leave the request and the
  response. The Budget page's commitment header measures the plan against a typical 4-week month of the active lines
  (weekly × 4, biweekly × 2, monthly × 1).
- **Migration `AddIncomeLines` — additive only (plan §4a):** creates the two tables (RLS), then copies: each household's
  primary / secondary 4w/5w default becomes a line (5w = 4w → monthly; 5w/5 = 4w/4 → weekly at 4w/4; otherwise weekly
  at 4w/4 — or 5w/5 when 4w is zero — with `needs_review`); each month's non-zero slot becomes a row with its stored
  amount and currency **verbatim**. Deterministic ids and `NOT EXISTS` make it idempotent; the RLS bypass is set for
  the migration's own transaction (a non-superuser owner — Neon — would otherwise copy nothing). The old columns stay,
  untouched, as the rollback baseline (dropped later by the owner-gated INCOME-3). The same SQL ships as
  `tools/backfill-income-lines.sql` for a pre-migration household snapshot restored afterwards.
- **Erasure:** the per-user contributor clears `member_user_id` on lines and rows in every household; amounts stay.
- **UI:** Settings → **Income** (`/incomes`): the ordered list with member, pay period and amount, an inline add/edit
  form like the envelopes page (name, member, currency, fixed/variable, pay period, amount, pay days for biweekly,
  active), up/down reorder of the active lines, and a "check this" badge for `needs_review`. A member who has left
  still shows on their line as "a former member" and can be kept on save. The month page's income block lists the rows, each editable, the planned
  figure beside an edited one, and a one-off row can be added. The budget-settings card loses its income inputs, and
  the settings page says the week start is the day the money moves.

```gherkin
Scenario: A weekly salary follows the month's week count
  Given Allan's line: USD, fixed, weekly, $500 a week
  When a transaction creates September 2026 (Aug 25 – Sep 28, 5 weeks)
  Then September's income has a row "Allan salary" of $2,500 planned and $2,500 amount
  And October (4 weeks) gets $2,000

Scenario: A monthly salary doesn't
  Given the son's line: CRC, fixed, monthly, ₡600,000
  Then every new month gets ₡600,000 whatever its week count

Scenario: A biweekly salary counts its pay days
  Given a line paid biweekly on the 15th and the last day, ₡400,000 each
  When a month runs Aug 25 – Sep 28
  Then it holds Aug 31 and Sep 15 — 2 pay days — and the row is ₡800,000
  And a window holding the 15th, the last day and the next 15th gets 3 pay days

Scenario: Someone who left
  Given a line whose member has left the household
  When a new month is created
  Then that line gets no row, and it stays in the list for history

Scenario: Correcting a month
  Given September's "Freelance" row planned at $300
  When I set it to $420 and add a one-off "Sold the bike" ₡150,000
  Then GET /api/months/{id} shows Freelance $420 (planned $300) and the one-off without a plan
  And the dashboard's income_total counts both

Scenario: The catalog rules
  When I create "Allan salary" twice → 409 income_exists
  When I create a line named like an inactive one → 409 income_exists_inactive with its id
  When I name a member who isn't in the household → 400 invalid_request
  When I send amount 0, pay_period "yearly", currency "EUR" or pay days on a monthly line → 400 invalid_request
  When I reorder with a list that isn't exactly the active lines → 400 invalid_request
  When I touch another household's line → 404

Scenario: The migration keeps every month's income
  Given a household whose settings say primary $2,000 (4w) / $2,500 (5w) and secondary ₡600,000 / ₡600,000
  And three months with their stored incomes, one of them edited by hand
  When the migration runs
  Then "Primary income" is weekly $500 and "Secondary income" is monthly ₡600,000
  And every month's rows equal its stored amounts and currencies, per currency, to the cent
  And the old columns still hold their values, and running the backfill again adds nothing
```

### INCOME-2 — Income by member

**As a** household member
**I want** the month's report to say whose income it is
**So that** I can see how much each of us brings in, and what came in as one-offs or inflows

**Context / notes:**
- **Pure rule** (`IncomeByMember.Group`, Core): the month's income rows (INCOME-1) and inflows, as the same pairs
  `IncomeCalculator` produced, cut into slices — one per **current member** (named with their display name, else
  their email), **the household** (rows with no member), **former members** (rows whose member left — one slice, no
  names kept), **inflows**. Members first, largest first (ties by name), then household, former members, inflows.
  Empty slices are left out. The slices add up to the month's income.
- **API:** `GET /api/reports/category-analysis` gains `income_by_member: [{kind, member_user_id, name, amount{crc,usd}}]`,
  `kind` = `member` | `household` | `former_member` | `inflows`. Present exactly when `income` is (a single month with a
  rate); null for a range or without a rate. No new endpoint, no migration.
- **Reports page** (chart view, month mode): an **Income by member** donut card in the chart currency; "no rate" and
  "no income recorded" say so instead of drawing.
- **PDF:** the same donut among the charts, and an **Income by member** table (whose · income on the "show in" side ·
  share) with its total, before the category tables; its heading never ends a page. Left out for a range or without a
  rate. EN/ES.

```gherkin
Scenario: The month's income by whose it is
  Given September's income rows: Allan's salary $2,000 and freelance $300, a household rent ₡150,000, and a row of a member who left
  And a ₡25,000 inflow
  When I open the September report
  Then income_by_member lists Allan ($2,300 at the day's rate), the household, former members and inflows
  And the slices add up to the report's income

Scenario: A range or a missing rate has no cut
  When I report a date range, or the rate cannot be resolved
  Then income_by_member is null and the page says why instead of drawing

Scenario: The chart and the PDF
  When I switch to the chart view
  Then an "Income by member" donut names each slice in my language, in the chart currency
  When I download the PDF
  Then it has the same donut and an "Income by member" table with a share column and a total
```
