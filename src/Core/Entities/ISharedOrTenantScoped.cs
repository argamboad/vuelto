namespace Vuelto.Core.Entities;

/// <summary>
/// Marks an entity whose rows are EITHER shared (<c>TenantId</c> null: a curated catalog every tenant reads and none
/// owns) OR owned by one tenant (<c>TenantId</c> set). The deliberate sibling of <see cref="ITenantScoped"/>, not a
/// way around it (Arch A4, #364; first built in jigger-jot as JJ-031 and moved upstream): <see cref="ITenantScoped"/>
/// cannot express these rows at all — its <c>TenantId</c> is non-nullable, and both the global query filter and the
/// forced RLS policy test <c>TenantId = current</c>, so a shared row would be invisible in the app and at the
/// database.
/// <para>
/// What the platform guarantees for an entity marked here, each with its own gate:
/// <list type="number">
/// <item><b>Reads</b> see the shared rows plus the current tenant's own, never another tenant's — a second global
/// query filter in <c>AppDbContext</c> (<c>TenantId == null || TenantId == CurrentTenantId</c>), and with no tenant
/// current only the shared rows (<c>EveryTenantScopedEntity_HasAGlobalQueryFilter</c> covers both markers).</item>
/// <item><b>Writes</b> under a tenant may touch only that tenant's own rows: <c>TenantStampingInterceptor</c> refuses
/// an insert, update or delete of a shared row (null) or of another tenant's row while a tenant is current. There is
/// no stamping: a row meant to be shared and a row meant to be owned are different intents, so the writer says which
/// (<c>TenantId</c> set explicitly). Shared rows are written by tenant-less contexts only (a seeder, a curator job).</item>
/// <item><b>The database agrees</b> through four command-scoped RLS policies (<c>RlsDdl.SharedOrTenantStatementsFor</c>):
/// SELECT admits shared and own, INSERT/UPDATE/DELETE admit own only, so a tenant cannot delete the catalog even past
/// the EF filter; writing a shared row needs the bypass GUC a tenant-less context alone gets. The migration-parity gate
/// (<c>EverySharedOrTenantTable_HasForcedRlsAndAllFourPolicies_AfterMigrations</c>) holds every such table to it.</item>
/// <item><b>Lifecycle</b>: an <c>ITenantDataContributor</c> wipes and exports the tenant's own rows and never the
/// shared ones; the tenant-axis canary (<c>EveryTenantOwnedEntity_IsWiredIntoTenantDissolution</c>) counts nullable
/// keys and so sees these tables; and each such entity ships four facet tests named
/// <c>&lt;Entity&gt;_SharedOrTenant_{Dissolve,Export,SharedWrites,Erasure}_*</c>
/// (<c>EverySharedOrTenantEntity_ShipsItsLifecycleSpec</c>): what a dissolve removes and keeps, what the export
/// carries, who may write a shared row, and that an account erasure leaves the tenant's rows (they are the
/// tenant's, not the user's).</item>
/// </list>
/// </para>
/// </summary>
public interface ISharedOrTenantScoped
{
    /// <summary>Null = a shared row, owned by no tenant; set = owned by that tenant.</summary>
    Guid? TenantId { get; }
}
