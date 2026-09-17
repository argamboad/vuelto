-- INCOME-1 (ADR-V023, plan §4a): after the AddIncomeLines migration, every month's income rows must add up, per
-- currency, to the two old slots they were copied from. Run as the owner role right after the deploy, BEFORE anyone
-- edits a month's income in the new UI (an edit legitimately makes the two differ). MUST RETURN NO ROWS.
--   psql "<owner connection>" -f tools/check-income-parity.sql
-- The tables force row-level security and Neon's owner is not a superuser: lift the policy for this session only.
DO $$ BEGIN PERFORM set_config('app.rls_bypass', 'on', false); END $$;

WITH old AS (
    SELECT "Id" AS month_id, "PrimaryIncomeCurrency" AS currency, "PrimaryIncomeAmount" AS amount
    FROM "Months" WHERE "PrimaryIncomeAmount" <> 0
    UNION ALL
    SELECT "Id", "SecondaryIncomeCurrency", "SecondaryIncomeAmount"
    FROM "Months" WHERE "SecondaryIncomeAmount" <> 0
), old_totals AS (
    SELECT month_id, currency, SUM(amount) AS total FROM old GROUP BY month_id, currency
), new_totals AS (
    SELECT "MonthId" AS month_id, "Currency" AS currency, SUM("Amount") AS total
    FROM "MonthIncomes" GROUP BY "MonthId", "Currency"
)
SELECT COALESCE(o.month_id, n.month_id) AS month_id,
       COALESCE(o.currency, n.currency) AS currency,
       o.total AS old_total,
       n.total AS new_total
FROM old_totals o
FULL OUTER JOIN new_totals n ON n.month_id = o.month_id AND n.currency = o.currency
WHERE o.total IS DISTINCT FROM n.total;
