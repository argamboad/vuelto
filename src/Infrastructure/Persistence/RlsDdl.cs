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

    // ── The shared-or-tenant shape (Arch A4, ISharedOrTenantScoped) ──────────────────────────────────────────────
    // The policy above cannot express a shared row: it tests TenantId = current, so a row with a NULL TenantId
    // matches nothing and a seeded catalog would be invisible at the database. ISharedOrTenantScoped tables get
    // their own, deliberately ASYMMETRIC set of policies — reads admit the shared rows, writes never do.

    /// <summary>The four command-scoped policy names on a shared-or-tenant table, in DDL order.</summary>
    public static readonly IReadOnlyList<string> SharedOrTenantPolicyNames =
    [
        "rls_shared_or_tenant_select",
        "rls_shared_or_tenant_insert",
        "rls_shared_or_tenant_update",
        "rls_shared_or_tenant_delete",
    ];

    /// <summary>The shared-or-tenant tables (and their nullable tenant-id column) in the given model.</summary>
    public static IReadOnlyList<(string Table, string TenantColumn)> SharedOrTenantTables(IModel model) =>
        model.GetEntityTypes()
            .Where(e => typeof(ISharedOrTenantScoped).IsAssignableFrom(e.ClrType))
            .Select(e => (
                Table: e.GetTableName() ?? throw new InvalidOperationException($"{e.ClrType.Name} has no table."),
                TenantColumn: e.FindProperty(nameof(ISharedOrTenantScoped.TenantId))!
                    .GetColumnName() ?? nameof(ISharedOrTenantScoped.TenantId)))
            .Distinct()
            .OrderBy(t => t.Table, StringComparer.Ordinal)
            .ToList();

    /// <summary>The full shared-or-tenant DDL for every such table in the model. Idempotent.</summary>
    public static IReadOnlyList<string> SharedOrTenantStatementsFor(IModel model) =>
        SharedOrTenantTables(model).SelectMany(t => SharedOrTenantStatementsFor(t.Table, t.TenantColumn)).ToList();

    /// <summary>
    /// The DDL for one shared-or-tenant table. Four command-scoped policies rather than one <c>FOR ALL</c>, because
    /// reading and writing a shared row are not the same question:
    /// <list type="bullet">
    /// <item><b>SELECT</b> admits shared rows (<c>TenantId IS NULL</c>) plus the tenant's own — the point of the shape.</item>
    /// <item><b>INSERT / UPDATE / DELETE</b> admit ONLY the tenant's own. A single <c>FOR ALL</c> policy would let a
    /// tenant DELETE the shared rows, since <c>DELETE</c> is checked against <c>USING</c> and <c>WITH CHECK</c> never
    /// applies to it. That is the read-only-catalog rule enforced at the database, not just in code.</item>
    /// </list>
    /// Seeding and curating the shared rows is therefore a bypass-GUC operation by construction (a tenant-less context).
    /// Fail-closed exactly like the tenant policy: an unset or empty tenant GUC matches no owned rows.
    /// </summary>
    public static IReadOnlyList<string> SharedOrTenantStatementsFor(string table, string tenantColumn)
    {
        var owned =
            $"""
             "{tenantColumn}" = NULLIF(current_setting('{TenantGuc}', true), '')::uuid
                     OR current_setting('{BypassGuc}', true) = 'on'
             """;
        var readable = $"""
                        "{tenantColumn}" IS NULL
                                OR {owned}
                        """;
        return
        [
            $"""ALTER TABLE "{table}" ENABLE ROW LEVEL SECURITY;""",
            $"""ALTER TABLE "{table}" FORCE ROW LEVEL SECURITY;""",
            $"""DROP POLICY IF EXISTS {SharedOrTenantPolicyNames[0]} ON "{table}";""",
            $"""
             CREATE POLICY {SharedOrTenantPolicyNames[0]} ON "{table}"
                 AS PERMISSIVE FOR SELECT
                 USING ({readable});
             """,
            $"""DROP POLICY IF EXISTS {SharedOrTenantPolicyNames[1]} ON "{table}";""",
            $"""
             CREATE POLICY {SharedOrTenantPolicyNames[1]} ON "{table}"
                 AS PERMISSIVE FOR INSERT
                 WITH CHECK ({owned});
             """,
            $"""DROP POLICY IF EXISTS {SharedOrTenantPolicyNames[2]} ON "{table}";""",
            $"""
             CREATE POLICY {SharedOrTenantPolicyNames[2]} ON "{table}"
                 AS PERMISSIVE FOR UPDATE
                 USING ({owned})
                 WITH CHECK ({owned});
             """,
            $"""DROP POLICY IF EXISTS {SharedOrTenantPolicyNames[3]} ON "{table}";""",
            $"""
             CREATE POLICY {SharedOrTenantPolicyNames[3]} ON "{table}"
                 AS PERMISSIVE FOR DELETE
                 USING ({owned});
             """,
        ];
    }
}
