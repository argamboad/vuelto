# Epic `BUDGET` — Budget settings

> Registered epic key: **BUDGET**. Port slice **P1** (ADR-V001): the household's budget structure,
> re-homed from donor stories **US-003** (week settings) and **US-015 AC1** (income defaults) —
> `vuelto-legacy/docs/stories/`. Decision context: ADR-V003 (settings are per household, not per
> user), ADR-V005 (pay-cycle months), `FEATURES.md` §7, `DATA_MODEL.md` → `BudgetSettings`.

### BUDGET-1 — Configure the household's budget structure

**As a** household member
**I want** to set the weekday our weeks start on, where our budget month begins, and our two
incomes' 4-week / 5-week defaults
**So that** months are tiled the way we get paid and every auto-created month is born with the
right income

**Context / notes:** one `BudgetSettings` row per household (tenant-scoped, unique on `TenantId`),
created on the first save; until then the API serves the donor's defaults (Thursday, last weekday
of the previous month, both incomes `0 USD`) flagged `is_default`. Any member may edit (no extra
`Permission` — budget data is the member baseline, ADR-V002). Changing settings affects **future**
months only: existing months keep their stored weeks (ADR-V005; the month slice enforces that).
`WeekBoundaryService` ports verbatim into Core with its donor tests — it is the consumer of these
settings, exercised here only through its unit tests. Wire format: snake_case via
`[JsonPropertyName]`, the platform's convention.

**Acceptance criteria**

```gherkin
Scenario: A new household sees the defaults before anything is saved
  Given I am a member of a household that has never saved budget settings
  When I GET /api/budget-settings
  Then I receive week_start_weekday 4, month_anchor "last_weekday_prev",
       both incomes 0 with currency "USD", and is_default true
  And no row was written

Scenario: Saving creates the household's single row
  Given my household has never saved budget settings
  When I PUT /api/budget-settings with weekday 1, anchor "first_of_month",
       primary 1500.00/1800.00 USD and secondary 400000/500000 CRC
  Then I receive 200 with those values and is_default false
  And a second PUT updates the same row (still exactly one row for the household)

Scenario: Settings are visible only to their household
  Given household A saved its settings
  When a member of household B reads or saves budget settings
  Then B sees the defaults, B's save creates B's own row, and A's row is unchanged

Scenario: Invalid input is rejected without writing
  Given I am a household member
  When I PUT a weekday outside 0..6, an unknown month_anchor, a currency other than CRC/USD,
       or a negative income amount
  Then I receive 400 with error "invalid_request" naming the field
  And nothing was written

Scenario: An unauthenticated caller is refused
  When I call GET or PUT /api/budget-settings without a token
  Then I receive 401

Scenario: The Settings page shows and saves the budget structure
  Given I am signed in
  When I open /settings
  Then a "Budget" card shows the weekday, the anchor (labelled with that weekday's name),
       and the two incomes' 4-week / 5-week amounts with currency
  When I change values and click Save
  Then the card confirms the save and a reload shows the saved values

Scenario: Dissolving the household removes its settings
  Given a household with saved settings
  When the household is dissolved
  Then its BudgetSettings row is gone and the tenant export included a "budget_settings" section
```

**Out of scope:** using the settings to build months (P5); per-month income edits (P5); any
validation that the two currencies differ (they may be equal).
**Definition of done:** tests written first (TDD); Core.Tests (`WeekBoundaryService`, 14 theories
from the donor) + Api.Tests slice tests on real Postgres (defaults, upsert, cross-tenant read AND
write negatives, validation, contributor) + HTTP tests (401, 200, 400) + bUnit card tests green;
migration with RLS DDL; contributor registered; Postman folder; QA cases + regenerated PDFs;
EN/ES resx; merged, app working.
