using Perezosoft.Testing;

namespace Vuelto.Api.Tests.Infrastructure;

/// <summary>The database fixture: the packaged one over this app's <see cref="TestAppDbContext"/>.</summary>
public sealed class PostgresFixture : PlatformPostgresFixture<TestAppDbContext>;

/// <summary>One container, shared by every relational test class (<c>[Collection(PostgresCollection.Name)]</c>).</summary>
[CollectionDefinition(PostgresCollection.Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
