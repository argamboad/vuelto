using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vuelto.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// ADR-020: row-level security as the DB-level second tenancy wall. Enables + FORCEs RLS and
    /// creates the fail-closed tenant-isolation policy on every ITenantScoped table that exists at
    /// this point (frozen list — a future ITenantScoped entity ships its own policy migration; the
    /// RlsMigrationGateTests parity gate fails CI if one is forgotten). The SQL is the frozen output
    /// of RlsDdl for these tables.
    /// <para>
    /// Enforcement is role-dependent by design: superusers and BYPASSRLS roles (local dev, CI
    /// containers) bypass RLS entirely; a non-superuser connection — including a table OWNER, which
    /// is what FORCE is for (e.g. Neon's database owner) — is subject to the policy, with the
    /// tenant/bypass GUCs set per command by RlsSessionInterceptor. Roles and grants are environment
    /// provisioning (docker/db/provision-rls-runtime-role.sql, DEPLOYMENT.md), not schema —
    /// deliberately not in this migration.
    /// </para>
    /// </summary>
    public partial class RlsTenancyBackstop : Migration
    {
        // Frozen output of RlsDdl.StatementsFor for the ITenantScoped tables as of 2026-07-06.
        private static readonly string[] Tables =
        [
            "ApiKeys",
            "AuditEvents",
            "Subscriptions",
            "TenantInvitations",
            "UsageCounters",
            "WebhookSubscriptions",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
            {
                migrationBuilder.Sql($"""ALTER TABLE "{table}" ENABLE ROW LEVEL SECURITY;""");
                migrationBuilder.Sql($"""ALTER TABLE "{table}" FORCE ROW LEVEL SECURITY;""");
                migrationBuilder.Sql($"""DROP POLICY IF EXISTS rls_tenant_isolation ON "{table}";""");
                migrationBuilder.Sql(
                    $"""
                     CREATE POLICY rls_tenant_isolation ON "{table}"
                         AS PERMISSIVE FOR ALL
                         USING ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                                OR current_setting('app.rls_bypass', true) = 'on')
                         WITH CHECK ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid
                                OR current_setting('app.rls_bypass', true) = 'on');
                     """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables)
            {
                migrationBuilder.Sql($"""DROP POLICY IF EXISTS rls_tenant_isolation ON "{table}";""");
                migrationBuilder.Sql($"""ALTER TABLE "{table}" NO FORCE ROW LEVEL SECURITY;""");
                migrationBuilder.Sql($"""ALTER TABLE "{table}" DISABLE ROW LEVEL SECURITY;""");
            }
        }
    }
}
