using Microsoft.EntityFrameworkCore;
using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;
using Perezosoft.Infrastructure.Persistence;

namespace Perezosoft.Api.Tests.Infrastructure;

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
/// Test DbContext = the real <see cref="AppDbContext"/> plus the <see cref="TestWidget"/> fixture set.
/// TestWidget is discovered from the DbSet property, so the base model's tenant-filter loop covers it
/// automatically; the write-stamping interceptor (wired in the base <c>OnConfiguring</c>) applies too.
/// </summary>
public sealed class TestAppDbContext(DbContextOptions<TestAppDbContext> options, ICurrentTenant currentTenant)
    : AppDbContext(options, currentTenant)
{
    public DbSet<TestWidget> TestWidgets => Set<TestWidget>();
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
