using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence;

/// <summary>
/// Generates the row-level-security DDL for the tenancy backstop (ADR-020): one fail-closed policy
/// per <see cref="ITenantScoped"/> table, derived from the EF model so the list can never drift from
/// the entities. Consumers: the RLS migration froze this output for the tables that existed at the
/// time; tests apply it to model-built (<c>EnsureCreated</c>) databases; and the migration-parity
/// gate asserts a migrated database matches the current model — so adding an <see cref="ITenantScoped"/>
/// entity without shipping its policy migration fails CI.
/// </summary>
public static class RlsDdl
{
    /// <summary>GUC carrying the current tenant id; set per command by <see cref="RlsSessionInterceptor"/>.</summary>
    public const string TenantGuc = "app.tenant_id";

    /// <summary>GUC marking a sanctioned cross-tenant/system command; "on" bypasses the tenant policy.</summary>
    public const string BypassGuc = "app.rls_bypass";

    /// <summary>Name of the policy created on every tenant-scoped table.</summary>
    public const string PolicyName = "rls_tenant_isolation";

    /// <summary>The tenant-scoped tables (and their tenant-id column) in the given model.</summary>
    public static IReadOnlyList<(string Table, string TenantColumn)> TenantTables(IModel model) =>
        model.GetEntityTypes()
            .Where(e => typeof(ITenantScoped).IsAssignableFrom(e.ClrType))
            .Select(e => (
                Table: e.GetTableName() ?? throw new InvalidOperationException($"{e.ClrType.Name} has no table."),
                TenantColumn: e.FindProperty(nameof(ITenantScoped.TenantId))!
                    .GetColumnName() ?? nameof(ITenantScoped.TenantId)))
            .Distinct()
            .OrderBy(t => t.Table, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The full DDL for every tenant-scoped table in the model. Idempotent (policies are dropped and
    /// recreated), so tests can re-apply it freely.
    /// </summary>
    public static IReadOnlyList<string> StatementsFor(IModel model) =>
        TenantTables(model).SelectMany(t => StatementsFor(t.Table, t.TenantColumn)).ToList();

    /// <summary>
    /// The DDL for one table. <c>FORCE</c> subjects even the table owner to the policy (superusers and
    /// <c>BYPASSRLS</c> roles always bypass — which is why local dev's superuser sees no change while a
    /// non-superuser owner, e.g. Neon's, gets real enforcement). The policy fails closed: an unset or
    /// empty tenant GUC matches no rows (<c>NULLIF</c> guards the empty-string cast), and the bypass
    /// GUC must be exactly "on". <c>WITH CHECK</c> mirrors <c>USING</c>, so foreign-tenant writes are
    /// rejected at the database as well (mirrors <see cref="TenantStampingInterceptor"/>).
    /// </summary>
    public static IReadOnlyList<string> StatementsFor(string table, string tenantColumn)
    {
        var predicate =
            $"""
             "{tenantColumn}" = NULLIF(current_setting('{TenantGuc}', true), '')::uuid
                     OR current_setting('{BypassGuc}', true) = 'on'
             """;
        return
        [
            $"""ALTER TABLE "{table}" ENABLE ROW LEVEL SECURITY;""",
            $"""ALTER TABLE "{table}" FORCE ROW LEVEL SECURITY;""",
            $"""DROP POLICY IF EXISTS {PolicyName} ON "{table}";""",
            $"""
             CREATE POLICY {PolicyName} ON "{table}"
                 AS PERMISSIVE FOR ALL
                 USING ({predicate})
                 WITH CHECK ({predicate});
             """,
        ];
    }
}
