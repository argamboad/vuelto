using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Perezosoft.Core.Abstractions;
using Perezosoft.Infrastructure.Persistence;

namespace Perezosoft.Api;

/// <summary>
/// Design-time factory so EF tooling (<c>migrations add</c>,
/// <c>migrations has-pending-model-changes</c>, <c>database update</c>) can build the context
/// WITHOUT booting the API host — routing through the host runs startup config validation
/// (e.g. <c>Jwt:Secret</c>) that isn't present at design time.
/// <para>
/// The connection string comes from <c>ConnectionStrings__DefaultConnection</c>, the SAME single
/// source the running app uses: the repo-root <c>.env</c> (loaded here via DotNetEnv, mirroring
/// <c>Program.cs</c>) in local dev, or a real environment variable in CI/prod. There is no
/// hardcoded fallback — the DB connection lives only in <c>.env</c>/env, alongside the other
/// secrets (ADR-001). Model building never reads the current tenant, so a null-tenant stub
/// suffices; offline operations like <c>has-pending-model-changes</c> open no DB connection.
/// </para>
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // Load .env so design-time tooling sees the same config as the running app. No-op when
        // there's no .env (CI/prod), where the connection string comes from real env vars.
        try { DotNetEnv.Env.TraversePath().Load(); } catch { /* no .env present */ }

        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? throw new InvalidOperationException(
                "ConnectionStrings__DefaultConnection is not set. Add it to .env for local dev " +
                "(copy .env.example), or set it as an environment variable in CI/prod.");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new AppDbContext(options, NullCurrentTenant.Instance);
    }

    /// <summary>Design-time stub: no request, so no current tenant.</summary>
    private sealed class NullCurrentTenant : ICurrentTenant
    {
        public static readonly NullCurrentTenant Instance = new();
        public Guid? TenantId => null;
    }
}
