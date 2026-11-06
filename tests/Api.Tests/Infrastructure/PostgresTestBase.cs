using Perezosoft.Testing;

namespace Vuelto.Api.Tests.Infrastructure;

/// <summary>
/// Base for this repo's relational test classes: the packaged one (<see cref="PlatformPostgresTestBase{TTestContext}"/>,
/// Pkg 5.1b), which truncates the shared database before every test, over this repo's fixture.
/// </summary>
public abstract class PostgresTestBase(PostgresFixture fixture) : PlatformPostgresTestBase<TestAppDbContext>(fixture)
{
    /// <summary>The shared container fixture, typed as this repo's.</summary>
    protected new PostgresFixture Fixture { get; } = fixture;
}
