using Microsoft.Extensions.DependencyInjection;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Abstractions;

namespace Vuelto.Api.Tests.Integration;

/// <summary>
/// v3 audit TB-TEN-11 (T45a): the DI half of the contributor completeness story. The model canaries in
/// <c>ArchitectureTests</c> prove every tenant-/user-keyed ENTITY maps to a named contributor — but that
/// mapping is a hand-maintained list, and nothing there proves the named contributor is actually
/// REGISTERED in the app's DI. Dissolve/export/erasure iterate the RESOLVED set, so an unregistered
/// contributor silently orphans its tables while both canaries stay green. This gate closes the seam:
/// every concrete contributor class the app assemblies declare must come back from the REAL app's
/// container (booted by <see cref="IntegrationTestFactory"/>, i.e. the production <c>Program</c> DI).
/// </summary>
[Collection(IntegrationCollection.Name)]
public class ContributorRegistrationTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    [Fact]
    public void EveryDeclaredTenantDataContributor_IsRegisteredInTheAppsDi() =>
        AssertAllRegistered<ITenantDataContributor>();

    [Fact]
    public void EveryDeclaredUserDataContributor_IsRegisteredInTheAppsDi() =>
        AssertAllRegistered<IUserDataContributor>();

    private void AssertAllRegistered<TContributor>() where TContributor : class
    {
        // Every non-abstract implementation in the app's own assemblies (Api incl. Features/, and
        // Infrastructure — where AuditDataContributor lives)…
        var declared = new[] { typeof(Program).Assembly, typeof(Vuelto.Infrastructure.ServiceCollectionExtensions).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(TContributor).IsAssignableFrom(t))
            .ToList();
        Assert.NotEmpty(declared); // the scan itself must be alive — an empty set means a broken probe, not success

        // …must be resolvable from the REAL app container (one instance per concrete type).
        using var scope = _factory.Services.CreateScope();
        var registered = scope.ServiceProvider.GetServices<TContributor>()
            .Select(c => c.GetType())
            .ToHashSet();

        var missing = declared.Where(t => !registered.Contains(t)).Select(t => t.Name).ToList();
        Assert.True(missing.Count == 0,
            $"{typeof(TContributor).Name} implementations not registered in DI — dissolve/export/erasure "
            + $"will silently skip their tables. Register in Program.cs/ServiceRegistrationExtensions: {string.Join(", ", missing)}");
    }
}
