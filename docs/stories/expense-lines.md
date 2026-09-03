# Epic `EXPENSES` — Fixed & variable budget lines

> Registered epic key: **EXPENSES**. Port slice **P6** (ADR-V001): the household's budget baseline,
> re-homed from donor stories **US-016** (entities + API), **US-017** (management UI with reorder),
> **US-054** (optional bank on lines) and the **US-019** seed policy as decided by the owner (ship
> *no* starter lines) — `vuelto-legacy/docs/stories/`. Decision context: ADR-V007 (required category,
> payment method), ADR-V008 (soft delete, 409 reactivation offer, uniform 404), `FEATURES.md` §14,
> `DATA_MODEL.md` → `FixedExpense` / `VariableExpense` (one shape, `IExpenseLine`).

### EXPENSES-1 — Keep the household's budget lines

**As a** household member
**I want** two ordered lists — fixed and variable — of what we plan to spend each month, each line tied
to the category whose transactions count against it
**So that** the dashboard (P7) can show budgeted vs actual per line and the household knows its baseline

**Context / notes:** `FixedExpense` and `VariableExpense` are structurally identical tables served by
one generic handler under `/api/expenses/fixed` and `/api/expenses/variable`. A line is a catalog entry
(`IExpenseLine : ICatalogEntry`): unique name **per list**, case-insensitively (the same name may exist
in both lists); deactivate instead of delete; the inactive clash offers **Reactivate** (restoring the
stored name, applying the budget/category/bank just typed). Each line carries a **single-currency**
budget (exactly one of `budget_crc` / `budget_usd` is non-zero), a payment method, a **required active
category that backs at most one active line across both lists** (the dashboard maps a category's spend
to one line; inactive lines release their category), an **optional active bank** (null = "Unassigned";
a bank deactivated later still names its line), and a `sort_order` that `PUT …/order` owns — the body
must name **exactly the active set**, and the new order lands atomically. Create appends. **Never
seeded** (owner decision: households build their own catalog). Any member may edit. Another
household's id is **404**; another household's category or bank is simply "unknown".

```gherkin
Scenario: A new household has no lines
  When I GET /api/expenses/fixed and /api/expenses/variable
  Then both are [] — nothing is seeded

Scenario: Creating a line appends it
  When I POST /api/expenses/fixed name "  Mortgage ", budget_crc 300000, payment_method bank_account, category Housing, bank BAC
  Then I receive 201 with name "Mortgage", sort_order 0, is_active true, bank_id BAC
  When I POST name "Netflix", budget_usd 13, credit_card, category Entertainment
  Then sort_order is 1 and bank_id is null (unassigned)

Scenario: Invalid requests write nothing
  When I POST with a blank name, an unknown payment method, no category, a negative budget, both budgets zero,
       both budgets non-zero, an unknown/inactive/foreign category, or an unknown/inactive/foreign bank
  Then I receive 400 "invalid_request" naming the rule, and no row exists

Scenario: Names are unique per list, with the reactivation offer
  Given "Mortgage" is active in fixed
  When I POST fixed "MORTGAGE" → 409 "expense_exists"
  When I POST variable "Mortgage" → 201 (the other list)
  Given "Water" is inactive in fixed
  When I POST fixed "water" → 409 "expense_exists_inactive" with existing_id and existing_name "Water"
  When I PUT /api/expenses/fixed/{existing_id} with name existing_name, budget_crc 15000, is_active true
  Then "Water" is active again with the new budget

Scenario: A category backs at most one active line, across both lists
  Given fixed "Mortgage" uses Housing
  When I POST fixed "Rent" with Housing, or variable "Groceries" with Housing → 400 "…already backs another budget line"
  When I PUT another line onto Housing → 400; keeping my own category is fine
  When "Mortgage" is deactivated → Housing is free again

Scenario: Update changes every editable field but never sort_order; reorder owns the order
  When I PUT /api/expenses/fixed/{id} with new name/budget/method/category/bank (or bank null)
  Then all change and sort_order is untouched
  When I PUT /api/expenses/fixed/order with ordered_ids
  Then 400 unless the ids are exactly the active lines (no missing, unknown, inactive or repeated id)
  And on success GET returns the lines in that order with sort_order 0..n

Scenario: The Budget page
  Given I am signed in
  When I open Budget (nav)
  Then I see Fixed and Variable sections, each line with its budget in its currency, category, bank
       (or Unassigned), method, Active/Inactive badge, ▲▼ on active lines and Edit
  When I add a line (name, amount + currency, category, optional bank, method) → it appears in order
  When I click ▼ → the active order is PUT (inactive lines excluded) and the list reloads
  And an inactive clash shows Reactivate, which restores the line
```

**Out of scope:** the dashboard's budgeted-vs-actual per line (P7); per-month budget overrides (never —
the catalog is global); seeded starter lines (owner decision).

**Definition of done:** tests first; Api.Tests slice tests on Postgres (no seeding, create/append/round,
the invalid theory, 409 per list + offer + restore, category-uniqueness across lists incl. release on
deactivation, update-never-sort-order + 404, list order + deactivated-bank reference, reorder guards +
atomic order, cross-tenant read AND write negatives, contributors) + HTTP (401 both lists, the full
build/reorder/deactivate loop, 400/409/404), bUnit (section list, single-currency payload, reorder PUT
excluding inactive, reactivate, page wiring); migration `AddExpenseLines` with RLS DDL for two tables;
two contributors; Postman folder; QA-EXP-01..03 + regenerated PDFs; EN/ES resx; nav entry; merged,
app working.
