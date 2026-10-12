namespace Vuelto.Infrastructure.Persistence;

/// <summary>
/// INCOME-1 (ADR-V023, plan §4a): copies the old two-income data into income lines and month income rows. Run once by
/// the <c>AddIncomeLines</c> migration and shipped verbatim as <c>tools/backfill-income-lines.sql</c> for a household
/// restored from a snapshot taken before that migration (<c>IncomeBackfillSqlTests</c> keeps the two identical).
/// <list type="bullet">
/// <item><b>Additive.</b> Only INSERTs; the old columns are read, never changed.</item>
/// <item><b>Verbatim months.</b> Each month's non-zero slot becomes a row with its stored amount and currency — history
/// is copied, never re-derived.</item>
/// <item><b>Lines.</b> A household's non-zero primary/secondary default becomes a line: 5w = 4w → monthly at that
/// amount; 5w/5 = 4w/4 → weekly at 4w/4; otherwise weekly (4w/4, or 5w/5 when 4w is zero) flagged NeedsReview.</item>
/// <item><b>Idempotent.</b> A household with any income line, or a month with any income row, is skipped; ids are
/// deterministic (md5 of the source key) so a month row links to its line.</item>
/// <item><b>RLS.</b> The bypass GUC is set for the current transaction: a non-superuser table owner (Neon) is subject
/// to the forced policies and would otherwise read and write nothing.</item>
/// </list>
/// </summary>
public static class IncomeBackfill
{
    public const string Sql = """
        -- INCOME-1 (ADR-V023): copy the old two-income settings and month slots into "IncomeLines" / "MonthIncomes".
        -- Additive and idempotent. Run as the owner role; safe to run twice.
        SELECT set_config('app.rls_bypass', 'on', true);

        INSERT INTO "IncomeLines" ("Id", "TenantId", "Name", "MemberUserId", "Currency", "Kind", "PayPeriod", "Amount",
                                   "PayDay1", "PayDay2", "IsActive", "SortOrder", "NeedsReview", "CreatedAt", "UpdatedAt")
        SELECT md5('vuelto-income-line:' || s."TenantId"::text || ':' || slot.key)::uuid,
               s."TenantId", slot.name, NULL, slot.currency, 'fixed',
               CASE WHEN slot.w5 = slot.w4 THEN 'monthly' ELSE 'weekly' END,
               CASE WHEN slot.w5 = slot.w4 THEN slot.w4
                    WHEN slot.w4 <> 0 THEN round(slot.w4 / 4, 2)
                    ELSE round(slot.w5 / 5, 2) END,
               NULL, NULL, TRUE, slot.ord,
               NOT (slot.w5 = slot.w4 OR slot.w5 * 4 = slot.w4 * 5),
               now(), now()
        FROM "BudgetSettings" s
        CROSS JOIN LATERAL (VALUES
            ('primary', 'Primary income', s."PrimaryIncome4w", s."PrimaryIncome5w", s."PrimaryIncomeCurrency", 0),
            ('secondary', 'Secondary income', s."SecondaryIncome4w", s."SecondaryIncome5w", s."SecondaryIncomeCurrency", 1)
        ) AS slot(key, name, w4, w5, currency, ord)
        WHERE (slot.w4 <> 0 OR slot.w5 <> 0)
          AND NOT EXISTS (SELECT 1 FROM "IncomeLines" l WHERE l."TenantId" = s."TenantId");

        INSERT INTO "MonthIncomes" ("Id", "TenantId", "MonthId", "IncomeLineId", "Label", "MemberUserId", "Currency",
                                    "Amount", "PlannedAmount", "SortOrder", "CreatedAt", "UpdatedAt")
        SELECT md5('vuelto-month-income:' || m."Id"::text || ':' || slot.key)::uuid,
               m."TenantId", m."Id",
               (SELECT l."Id" FROM "IncomeLines" l
                 WHERE l."Id" = md5('vuelto-income-line:' || m."TenantId"::text || ':' || slot.key)::uuid),
               slot.name, NULL, slot.currency, slot.amount, slot.amount, slot.ord, now(), now()
        FROM "Months" m
        CROSS JOIN LATERAL (VALUES
            ('primary', 'Primary income', m."PrimaryIncomeAmount", m."PrimaryIncomeCurrency", 0),
            ('secondary', 'Secondary income', m."SecondaryIncomeAmount", m."SecondaryIncomeCurrency", 1)
        ) AS slot(key, name, amount, currency, ord)
        WHERE slot.amount <> 0
          AND NOT EXISTS (SELECT 1 FROM "MonthIncomes" r WHERE r."MonthId" = m."Id");
        """;
}
