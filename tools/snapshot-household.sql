-- ¿Y el vuelto? — snapshot ONE household as a restorable SQL file (operator tool, no UI, no API route).
--
-- Purpose: carry a household that was built up locally (categories, banks, budget lines, months, weeks,
-- transactions with their frozen rates, refunds, merchant rules, the review queue) to another server —
-- typically local → staging → production — without starting over. Ids are preserved, so every
-- reference survives and the owner's next OTP sign-in on the target matches the pre-seeded user.
--
-- Usage (source = the database that holds the household; -Atq keeps the output raw):
--   docker exec -i vuelto-db-1 psql -U dev -d dev_db -Atq -v email=you@example.com -f - \
--     < tools/snapshot-household.sql > my-household.sql
--   psql "<target connection string, OWNER / migrations role>" -f my-household.sql
--
-- Rules:
--   • Apply ONCE to a target whose schema is current (the API migrates on start) and where the
--     household does not exist yet. Every INSERT is ON CONFLICT DO NOTHING, so a re-run is a no-op.
--   • Run the restore as the owner / migrations role: the runtime role is fenced by RLS (ADR-020).
--   • Included (FK order): Users (the members), Tenants, TenantMemberships, UserLogins,
--     BudgetSettings, Categories, Banks, Envelopes, FixedExpenses, VariableExpenses,
--     MerchantCategoryMappings, Months, Weeks, Transactions, Refunds, PendingVouchers, IngestedVouchers.
--   • Excluded on purpose: EmailConnections (OAuth tokens are bound to the source server's Data
--     Protection key ring — reconnect the inbox on the target), Subscriptions (billing state belongs to
--     the target's Stripe), ApiKeys, AuditEvents, OutboxMessages, TenantInvitations, UsageCounters,
--     WebhookSubscriptions, WebhookDeliveries, UserMfa, MfaRecoveryCodes, RefreshTokens, LoginTokens,
--     Notifications, NotificationPreferences, InboxMessages.
--   `HouseholdSnapshotTests` keeps this list honest: every tenant-scoped table must be named above.

CREATE OR REPLACE FUNCTION pg_temp.snapshot_household(p_email text) RETURNS text
LANGUAGE plpgsql AS $fn$
DECLARE
    v_tenant uuid;
    v_out    text := '';
    v_json   text;
    v_count  bigint;
    v_cond   text;
    v_table  text;
    v_kind   text;
    v_tables text[][] := ARRAY[
        ['Users', 'members'], ['Tenants', 'tenant'], ['TenantMemberships', 'tenantid'], ['UserLogins', 'members'],
        ['BudgetSettings', 'tenantid'], ['Categories', 'tenantid'], ['Banks', 'tenantid'], ['Envelopes', 'tenantid'],
        ['FixedExpenses', 'tenantid'], ['VariableExpenses', 'tenantid'], ['MerchantCategoryMappings', 'tenantid'],
        ['Months', 'tenantid'], ['Weeks', 'tenantid'], ['Transactions', 'tenantid'], ['Refunds', 'tenantid'],
        ['PendingVouchers', 'tenantid'], ['IngestedVouchers', 'tenantid']];
    i int;
BEGIN
    -- The household = the membership of that email (an owner membership wins when there are several).
    SELECT m."TenantId" INTO v_tenant
      FROM "TenantMemberships" m JOIN "Users" u ON u."Id" = m."UserId"
     WHERE lower(u."Email") = lower(p_email)
     ORDER BY (m."Role" = 'owner') DESC, m."JoinedAt"
     LIMIT 1;
    IF v_tenant IS NULL THEN
        RAISE EXCEPTION 'No household membership found for %', p_email;
    END IF;

    v_out := format(E'-- ¿Y el vuelto? household snapshot\n-- household %s · taken for %s at %s\n-- Apply ONCE to an empty target as the owner / migrations role: psql "<target>" -f this-file.sql\nBEGIN;\n',
                    v_tenant, p_email, now());

    FOR i IN 1 .. array_length(v_tables, 1) LOOP
        v_table := v_tables[i][1];
        v_kind  := v_tables[i][2];
        v_cond := CASE v_kind
            WHEN 'tenantid' THEN format('t."TenantId" = %L', v_tenant)
            WHEN 'tenant'   THEN format('t."Id" = %L', v_tenant)
            WHEN 'members'  THEN format('t.%I IN (SELECT "UserId" FROM "TenantMemberships" WHERE "TenantId" = %L)',
                                        CASE WHEN v_table = 'Users' THEN 'Id' ELSE 'UserId' END, v_tenant)
        END;
        EXECUTE format('SELECT COALESCE(json_agg(t), ''[]''::json)::text, count(*) FROM %I t WHERE %s', v_table, v_cond)
           INTO v_json, v_count;
        v_out := v_out || format(E'-- %s: %s row(s)\nINSERT INTO %I SELECT * FROM json_populate_recordset(NULL::%I, %L::json) ON CONFLICT DO NOTHING;\n',
                                 v_table, v_count, v_table, v_table, v_json);
    END LOOP;

    RETURN v_out || E'COMMIT;\n';
END
$fn$;

SELECT pg_temp.snapshot_household(:'email');
