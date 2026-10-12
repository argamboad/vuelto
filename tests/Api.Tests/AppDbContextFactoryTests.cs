using Microsoft.EntityFrameworkCore;
using Vuelto.Api;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Tests;

/// <summary>
/// Guards the design-time DbContext factory that EF tooling uses for migrations /
/// <c>has-pending-model-changes</c>. It must build the context from the
/// <c>ConnectionStrings__DefaultConnection</c> key alone — NEVER via the API host — so
/// <c>dotnet ef</c> doesn't trip the host's runtime config validation (e.g. <c>Jwt:Secret</c>).
/// No DB is touched: the model is built in-memory, mirroring the offline model-diff CI step.
/// </summary>
public class AppDbContextFactoryTests
{
    [Fact]
    public void CreateDbContext_BuildsContext_FromConnectionStringEnv_NoHost()
    {
        var original = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__DefaultConnection",
            "Host=localhost;Port=5432;Database=designtime_test;Username=t;Password=t");
        try
        {
            // No Jwt:Secret / host configured — exactly the design-time/CI situation that
            // previously failed when EF routed through the API host.
            var context = new AppDbContextFactory().CreateDbContext([]);

            Assert.NotNull(context);
            // The model resolves — proves the context is usable for migration/model-diff operations.
            Assert.NotNull(context.Model.FindEntityType(typeof(User)));
            Assert.NotNull(context.Model.FindEntityType(typeof(Tenant)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", original);
        }
    }
}
