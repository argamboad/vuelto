using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Api.Tests.Infrastructure;

/// <summary>
/// A test-only <see cref="ITenantScoped"/> fixture entity. The platform tenancy/GDPR/outbox tests use
/// THIS as their generic tenant-scoped row instead of the DELETE-ME <c>Note</c> sample, so a downstream
/// app can delete the sample without breaking the tests that guard tenant isolation (v2 audit TR-1).
/// </summary>
public sealed class TestWidget : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A test-only <see cref="ISharedOrTenantScoped"/> fixture entity (Arch A4): rows that are shared (null) or one
/// tenant's. The platform owns no such table itself, so the second filter, the write rule, the four RLS policies and
/// the lifecycle facets are proven on this one (SharedOrTenantTests, SharedOrTenantRlsTests).
/// </summary>
public sealed class TestSharedWidget : ISharedOrTenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? TenantId { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Test DbContext = the real <see cref="AppDbContext"/> plus the <see cref="TestWidget"/> fixture set.
/// TestWidget is discovered from the DbSet property, so the base model's tenant-filter loop covers it
/// automatically; the write-stamping interceptor (wired in the base <c>OnConfiguring</c>) applies too.
/// </summary>
public sealed class TestAppDbContext(DbContextOptions<TestAppDbContext> options, ICurrentTenant currentTenant)
    : AppDbContext(options, currentTenant)
{
    public DbSet<TestWidget> TestWidgets => Set<TestWidget>();
    public DbSet<TestSharedWidget> TestSharedWidgets => Set<TestSharedWidget>();
}

/// <summary>
/// Test <see cref="ITenantDataContributor"/> over <see cref="TestWidget"/> — the generic tenant-data
/// contributor the dissolve/export/erasure tests use in place of <c>NotesDataContributor</c>.
/// </summary>
public sealed class TestWidgetDataContributor(IRepository<TestWidget> widgets) : ITenantDataContributor
{
    public Task<bool> HasDataAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        widgets.QueryAllTenants().AnyAsync(w => w.TenantId == tenantId, cancellationToken);

    public async Task WipeAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await widgets.QueryAllTenants().Where(w => w.TenantId == tenantId).ExecuteDeleteAsync(cancellationToken);

    public string ExportKey => "test_widgets";

    public async Task<object?> ExportAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await widgets.QueryAllTenants()
            .Where(w => w.TenantId == tenantId)
            .OrderBy(w => w.CreatedAt)
            .Select(w => new { w.Id, w.Name, w.CreatedAt })
            .ToListAsync(cancellationToken);
}

/// <summary>
/// The tenant-data contributor of the shared-or-tenant fixture (Arch A4): wipes and exports the tenant's own rows,
/// never the shared ones — every query carries the non-null tenant predicate, and the delete policy agrees.
/// </summary>
public sealed class TestSharedWidgetDataContributor(IRepository<TestSharedWidget> widgets) : ITenantDataContributor
{
    public Task<bool> HasDataAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        widgets.QueryAllTenants().AnyAsync(w => w.TenantId == tenantId, cancellationToken);

    public async Task WipeAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await widgets.QueryAllTenants().Where(w => w.TenantId == tenantId).ExecuteDeleteAsync(cancellationToken);

    public string ExportKey => "test_shared_widgets";

    public async Task<object?> ExportAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await widgets.QueryAllTenants()
            .Where(w => w.TenantId == tenantId)
            .OrderBy(w => w.CreatedAt)
            .Select(w => new { w.Id, w.Name, w.CreatedAt })
            .ToListAsync(cancellationToken);
}
