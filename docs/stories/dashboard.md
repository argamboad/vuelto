# Epic `DASH` — The month dashboard

> Registered epic key: **DASH**. Port slice **P7** (ADR-V001): the month at a glance, re-homed from donor
> stories **US-005** (dashboard), **US-012** (unplanned essentials, refunds, envelopes), **US-018**
> (budgeted vs actual on the real catalog, other spending) and **US-055** (bank × payment-method
> breakdown) — `vuelto-legacy/docs/stories/`. Decision context: ADR-V004 (dual currency), ADR-V006
> (frozen actuals, live projections, block when no rate), ADR-V007 (transaction classes, refunds,
> envelopes), `FEATURES.md` §15.

### DASH-1 — See the month truthfully at a glance

**As a** household member
**I want** one page that shows this month's income, spend, budget lines against their actuals, weekly
totals, balance, unplanned essentials, expected refunds, envelope reminders and the bank × method view
**So that** I know where the money went and what is still committed before the month ends

**Context / notes:** the dashboard **is the home page** (owner decision 2026-09-04): `/` renders it for a
signed-in member (the public hero stays for anonymous visitors); the rate badge lives in its header and its
empty state. the four tables (fixed, variable, other spending, week by week) end with a **Total**
row summed on the page from the rows shown. the calculation is a **pure Core service** (`DashboardSummaryService`) fed by the
`Features/Dashboard` handler: the month, its weeks, its transactions and refunds, the active envelopes,
both budget-line lists, and **all** categories and banks (a deactivated name still labels its row). Every
figure is a ₡/$ pair rounded to 2 dp. **Actuals** sum each transaction's frozen amounts; **projections**
(income conversion, budget display in the other currency, pending budgeted, remainder for debts) use the
rate resolved through the ADR-V006 chain — live → cache → the household's last transaction. When nothing
resolves the response carries `rate_unavailable: true` and **no summary**; the page shows the month header
and a red block instead of guessing. Rules ported verbatim: inflows fold into income; `envelope_contribution`
and `inflow` are carved out of expenses; the card/account split follows the transaction's own method;
a line's actual is the expense-class spend in its category; **other spending** is expense-class spend in
categories no active line backs (sorted ₡ desc, then name; sum of line actuals + other = grand total);
remainder for debts = income − account-paid fixed budgets; pending budgeted = Σ max(0, native budget −
actual) valued once at the rate (never both columns); actual remainder = current balance − pending;
refunds total counts **pending only** (a received refund is already an inflow); envelope reminders show
monthly ones always and five-week ones only on 5-week months, remaining clamped ≥ 0; the bank × method
grid budgets by each line's (bank, method) and actuals by each row's, bankless lines in "Unassigned" last.
Semantic colour only: green under budget, red over — never the brand gold. The route hangs off the month
(`GET /api/months/{id}/summary`) in its own slice; the Ledger owns the month, the Dashboard reads it.

```gherkin
Scenario: The dashboard for a month
  Given June 2026 (4 weeks from May 28), income 3000 USD, rate 500, a fixed line Mortgage ₡350,000 (Housing, BAC, bank account)
  And transactions: Mortgage ₡300,000 (account, Jun 5), lunch ₡10,000 unplanned_essential (card, Jun 12, refund expected 50%)
  When I GET /api/months/{id}/summary
  Then exchange_rate is 500 with its source and as_of, rate_unavailable is false
  And income_total is ₡1,500,000 / $3,000; expenses_account ₡300,000, expenses_card ₡10,000, expenses_total ₡310,000
  And fixed_expenses has Mortgage budget ₡350,000 / $700 with actual ₡300,000
  And other_spending has the lunch's category (even if that category is inactive) at ₡10,000
  And unplanned_essential_total is ₡10,000 and refunds_total is ₡5,000 (pending only)
  And weekly_budgeted has one row per week with the mortgage in week 2
  And bank_method_breakdown has BAC / bank_account budget ₡350,000 actual ₡300,000
  And current_balance is ₡1,190,000 and pending_budgeted ₡50,000

Scenario: No rate resolves
  Given no provider key, an empty cache and no transaction in the household
  When I GET /api/months/{id}/summary
  Then rate_unavailable is true, exchange_rate and summary are null, and the month header is still present

Scenario: Uniform 404
  When I GET the summary for an unknown id or another household's month
  Then I receive 404

Scenario: The Dashboard page
  Given I am signed in
  When I open Dashboard (nav or Home) → the newest month loads; /dashboard/{id} opens that month
  Then I see the month title, "N weeks · from – to", the rate line (live / as of / from your last transaction),
       a month selector, Month details and New transaction buttons
  And cards Income, Expenses (card, account, total, remainder + the frozen-rate note), Balance
  And Fixed / Variable tables (Line, Budgeted, Actual — green under, red over), Other spending, Week by week,
       Unplanned essentials & refunds, Envelope reminders, By bank and payment method
  When no rate resolves → the red "projections are blocked" message and no figures
  When there are no months → "Nothing to show yet" with New transaction
  When the month was removed (404) → the month-no-longer-exists message
```

**Out of scope:** the CSV export button and the pending-voucher review banner (later slices); per-month
budget overrides (never); editing income here (the month page owns it).

**Definition of done:** tests first; Core.Tests port of the donor `DashboardSummaryServiceTests` (45 cases:
income both currencies, card/account split, lines active-only in order, weekly rows, balance trio, empty
states, inflow, envelope contribution + reminders, 2-dp rounding, dual-currency budget display + the
double-count guard, other spending incl. inactive names/sort/reconciliation, bank × method); Api.Tests
slice on Postgres (assembly with names, no-rate, unknown/foreign null) + HTTP (401, member read through
the chain's last tier, 404); bUnit (newest month + every section + tone, empty state, rate-unavailable,
404); no entity, no migration; Postman folder; QA-DASH-01..02 + regenerated PDFs; EN/ES resx; nav + Home
+ month-page entry points; merged, app working.
