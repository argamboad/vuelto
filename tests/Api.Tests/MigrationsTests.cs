using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Vuelto.Api.Tests;

/// <summary>
/// Guards against migration/snapshot drift, which the rest of the suite cannot see because the
/// shared fixture builds its schema with <c>EnsureCreated</c> (CONF-18 / B6-2). This applies the
/// REAL migrations to a throwaway database — catching a broken migration — and asserts the model is
/// fully captured by them (no pending model changes), so a future entity edit without a migration
/// fails the build instead of silently drifting.
/// </summary>
public sealed class MigrationsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17.11").Build();

    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;
        return new AppDbContext(options, new TestCurrentTenant());
    }

    [Fact]
    public async Task Migrations_ApplyCleanly_AndModelHasNoPendingChanges()
    {
        await using var db = CreateContext();

        await db.Database.MigrateAsync(); // every migration runs against a real DB — throws if broken

        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); // all applied
        Assert.False(db.Database.HasPendingModelChanges());          // model == last snapshot (no drift)
    }

    [Fact]
    public async Task Migrations_Down_RevertCleanly_ToEmptySchema()
    {
        // v2 audit B8: exercise every Down — a broken rollback (e.g. dropping a renamed column) is
        // otherwise invisible until a production rollback. Migrate up, then all the way back to empty.
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        await db.GetService<IMigrator>().MigrateAsync(Migration.InitialDatabase); // runs every Down; throws if broken

        Assert.Empty(await db.Database.GetAppliedMigrationsAsync()); // schema fully reverted
    }

    [Fact]
    public async Task Migrations_Down_OnSeededRows_EveryDownRunsAgainstData_AndSurvivingTablesKeepTheirRows()
    {
        // R150 (v4 audit T26, TB-TEN-27): the walk above reverts an EMPTY schema, so a Down that fails or damages
        // rows on a populated table passes CI and shows up in a real rollback. Here every Down runs against
        // rows: seed at the head, step back one migration at a time, and after each step every seeded table
        // that still exists still holds its row. A Down may drop a column — that loses the column's data and
        // the migration says so (see ColumnDroppingDowns_StateTheirDataLoss) — but never a row.
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        db.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = "Household", CreatedAt = now });
        db.Set<User>().Add(new User { Id = userId, Email = "member@x.com", DisplayName = "Member" });
        db.Set<RefreshToken>().Add(new RefreshToken
        {
            UserId = userId, TokenHash = "h", IssuedFromIp = "127.0.0.1", Provider = "test", IssuedAt = now, ExpiresAt = now.AddDays(30),
            IsRevoked = true, RotatedAt = now, ReplacedByTokenId = Guid.CreateVersion7(), GraceUsedAt = now, SessionExpiresAt = now.AddDays(90),
        });
        db.Set<OutboxMessage>().Add(new OutboxMessage { Type = "email", Payload = "{}", TenantId = tenantId, CreatedAt = now, NextAttemptAt = now });
        await db.SaveChangesAsync();
        string[] seeded = ["Tenants", "Users", "RefreshTokens", "OutboxMessages"];

        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        var migrator = db.GetService<IMigrator>();
        var sawRotationLinkDown = false;
        for (var i = applied.Count - 1; i >= 0; i--)
        {
            await migrator.MigrateAsync(i == 0 ? Migration.InitialDatabase : applied[i - 1]); // runs applied[i]'s Down; throws if broken

            foreach (var table in seeded)
            {
                if (await ScalarAsync<long>(db, $"SELECT count(*) FROM information_schema.tables WHERE table_name = '{table}'") == 0) continue; // its creating migration is reverted
                Assert.True(await ScalarAsync<long>(db, $"SELECT count(*) FROM \"{table}\"") == 1, $"{table} lost its row when {applied[i]} was reverted");
            }

            if (applied[i].EndsWith("_AddRefreshTokenRotationLink", StringComparison.Ordinal))
            {
                // The one-way loss this Down documents: the two columns go, the token row stays.
                sawRotationLinkDown = true;
                Assert.Equal(0, await ScalarAsync<long>(db,
                    "SELECT count(*) FROM information_schema.columns WHERE table_name = 'RefreshTokens' AND column_name IN ('RotatedAt', 'ReplacedByTokenId')"));
            }
        }

        Assert.True(sawRotationLinkDown, "probe: the rotation-link migration was not in the walk");
        Assert.Empty(await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public void ColumnDroppingDowns_StateTheirDataLoss()
    {
        // R150: reverting a migration that added a column throws that column's values away, and nothing said so —
        // the rotation-link Down silently discards RotatedAt and ReplacedByTokenId. From that migration on, a Down
        // that drops a column or a table carries a "Data loss on Down:" comment saying what is lost and why that
        // is acceptable. Earlier migrations predate the rule and are left as they shipped.
        const string since = "20260922145324"; // this repo's id for AddRefreshTokenRotationLink (the platform's is 20260919010912)
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.EnumerateFiles("*.slnx").Any()) dir = dir.Parent;
        var migrations = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src", "Infrastructure", "Persistence", "Migrations"), "*.cs")
            .Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal) && char.IsDigit(Path.GetFileName(f)[0]))
            .Where(f => string.CompareOrdinal(Path.GetFileName(f)[..14], since) >= 0)
            .ToList();
        Assert.True(migrations.Count >= 3, "probe: the migrations folder moved");

        var silent = migrations.Where(f =>
        {
            var text = File.ReadAllText(f);
            var down = text[text.IndexOf("void Down(", StringComparison.Ordinal)..];
            return (down.Contains("DropColumn(") || down.Contains("DropTable(")) && !down.Contains("Data loss on Down:");
        }).Select(Path.GetFileName).ToList();
        Assert.True(silent.Count == 0, "a Down drops a column or table without a 'Data loss on Down:' comment: " + string.Join(", ", silent));
    }

    private static async Task<T> ScalarAsync<T>(AppDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
