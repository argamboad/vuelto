using Vuelto.Host.Persistence;
using Microsoft.EntityFrameworkCore;
using Perezosoft.Core.Abstractions;
using Perezosoft.Testing;

namespace Vuelto.Api.Tests.Infrastructure;

/// <summary>This app's context plus the harness's fixture entities, for the tenancy suite and the relational tests.</summary>
public sealed class TestAppDbContext(DbContextOptions<TestAppDbContext> options, ICurrentTenant currentTenant)
    : AppDbContext(options, currentTenant)
{
    public DbSet<TestWidget> TestWidgets => Set<TestWidget>();
    public DbSet<TestSharedWidget> TestSharedWidgets => Set<TestSharedWidget>();
}
