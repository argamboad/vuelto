using System.Reflection;
using Vuelto.Api.Tests.App;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Host.Persistence;
using Perezosoft.Infrastructure.Persistence;
using Perezosoft.Testing.Suites;

namespace Vuelto.Api.Tests;

/// <summary>The platform's architecture rules, run against this app: its host, its model, its slices, its allowlists.</summary>
public class ArchitectureTests : PlatformArchitectureSuite
{
    protected override Assembly HostAssembly => typeof(Vuelto.Host.AppComposition).Assembly;
    protected override string FeatureNamespace => "Vuelto.Host.Features";
    protected override PlatformDbContext CreateModelContext() => new AppDbContext(ModelOptions<AppDbContext>(), new TestCurrentTenant());
    protected override PlatformDbContext CreateTestModelContext() => new TestAppDbContext(ModelOptions<TestAppDbContext>(), new TestCurrentTenant());

    protected override IReadOnlySet<string> ErasureHandled => AppAllowlists.ErasureHandled;
    protected override IReadOnlySet<string> DissolutionHandled => AppAllowlists.DissolutionHandled;
    protected override IReadOnlyDictionary<string, string> LifecycleSpecExceptions => AppAllowlists.LifecycleSpecExceptions;
    protected override IReadOnlySet<string> TenantIdByConvention => AppAllowlists.TenantIdByConvention;
    protected override IReadOnlyDictionary<string, string> OutboundHttpSenders => AppAllowlists.OutboundHttpSenders;
    protected override IReadOnlySet<string> ControllersOutsideTheBases => AppAllowlists.ControllersOutsideTheBases;
    protected override IReadOnlyDictionary<string, string> EntityWriters => AppAllowlists.EntityWriters;
    protected override IReadOnlyDictionary<string, string> EnterTenantRowSupplied => AppAllowlists.EnterTenantRowSupplied;
    protected override IReadOnlySet<string> TypeNameExceptions => AppAllowlists.TypeNameExceptions;
}
