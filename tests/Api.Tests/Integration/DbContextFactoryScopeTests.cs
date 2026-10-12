using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Api.Tests.Integration;

/// <summary>
/// v4 T42 (JOBS-10): the webhook handler writes its failure row through <c>IDbContextFactory&lt;AppDbContext&gt;</c>
/// from inside the outbox's scope. That registration (scoped, next to the scoped context it must not share a
/// connection with) had no DI test — a change to its lifetime would only show as a runtime resolution error the
/// first time a delivery failed.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class DbContextFactoryScopeTests(IntegrationTestFactory factory)
{
    [Fact]
    public async Task DbContextFactory_ResolvesInsideAScope_AndHandsOutAnIndependentContext()
    {
        using var scope = factory.Services.CreateScope();

        var scoped = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var made = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
        await using (made)
        {
            Assert.NotSame(scoped, made); // its own connection: a failure row survives the ambient rollback
            Assert.True(await made.Database.CanConnectAsync());
        }
    }
}
