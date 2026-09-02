namespace Vuelto.Api.Tests.Infrastructure;

/// <summary>
/// Base for relational test classes that share the Postgres container. Truncates the database
/// before EVERY test via <see cref="IAsyncLifetime.InitializeAsync"/>, so isolation is structural
/// rather than by-convention: there is no per-test <c>ResetAsync()</c> to forget, and absolute-count
/// assertions are safe by construction (B6-3). xUnit runs the members of a collection serially, so
/// the shared database is never reset out from under a concurrent test.
/// </summary>
public abstract class PostgresTestBase(PostgresFixture fixture) : IAsyncLifetime
{
    /// <summary>The shared container fixture — derived tests build contexts from it.</summary>
    protected PostgresFixture Fixture { get; } = fixture;

    public Task InitializeAsync() => Fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;
}
