using Vuelto.Host.Persistence;
using Perezosoft.Testing;

namespace Vuelto.Api.Tests.Infrastructure;

/// <summary>The integration host: the packaged harness over this app's <c>Program</c> and <see cref="AppDbContext"/>.</summary>
public sealed class IntegrationTestFactory : PlatformIntegrationFactory<Program, AppDbContext>;

[CollectionDefinition(IntegrationCollection.Name)]
public sealed class IntegrationCollection : ICollectionFixture<IntegrationTestFactory>
{
    public const string Name = "integration";
}
