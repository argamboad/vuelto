using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Api.Tests;

/// <summary>
/// #202 data safety (ADR-V026) on a real database built by the real migrations: <c>RefundPercentageOptional</c> only
/// relaxes <c>Refunds.Percentage</c> to nullable — every existing refund keeps its amounts and its percentage, nothing
/// is recomputed — and rolling it back refills a percentage the app has since left empty from the refund's own
/// amounts against its purchase, so the old not-null shape comes back without inventing zeros.
/// </summary>
public sealed class RefundPercentageMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17.11").Build();

    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private AppDbContext Context() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_container.GetConnectionString()).Options, new TestCurrentTenant());

    private static string Migration(AppDbContext db, string name, int offset = 0)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_" + name, StringComparison.Ordinal));
        Assert.True(index > 0, $"{name} migration not found");
        return all[index + offset];
    }

    private async Task<List<(Guid Id, decimal? Pct, decimal Crc, decimal Usd)>> RefundsAsync()
    {
        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""SELECT "Id", "Percentage", "AmountCrc", "AmountUsd" FROM "Refunds" ORDER BY "Payee" """, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<(Guid, decimal?, decimal, decimal)>();
        while (await r.ReadAsync()) rows.Add((r.GetGuid(0), r.IsDBNull(1) ? null : r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3)));
        return rows;
    }

    /// <summary>A household with one purchase in each currency at rate 500 — what the refunds below hang off.</summary>
    private static async Task<(Guid Tenant, Guid Month, Transaction Crc, Transaction Usd)> SeedPurchasesAsync(AppDbContext db)
    {
        var tenant = Guid.CreateVersion7();
        var category = new Category { TenantId = tenant, Name = "Health", CreatedAt = T0, UpdatedAt = T0 };
        var bank = new Bank { TenantId = tenant, Name = "Cash", CreatedAt = T0, UpdatedAt = T0 };
        var month = new Month { TenantId = tenant, Year = 2026, MonthNumber = 10, WeekCount = 4, Week1StartDate = new DateOnly(2026, 9, 29), CreatedAt = T0, UpdatedAt = T0 };
        Transaction Tx(string payee, decimal amount, string currency, decimal crc, decimal usd) => new()
        {
            TenantId = tenant, MonthId = month.Id, BankId = bank.Id, CategoryId = category.Id, Payee = payee, PaymentMethod = "credit_card",
            OriginalAmount = amount, Currency = currency, TransactionDate = new DateOnly(2026, 10, 5), AmountCrc = crc, AmountUsd = usd,
            ExchangeRateUsed = 500m, TransactionType = "unplanned_essential", Source = "manual", CreatedAt = T0, UpdatedAt = T0,
        };
        var crcTx = Tx("Clinic", 50_000m, "CRC", 50_000m, 100m);
        var usdTx = Tx("Pharmacy", 80m, "USD", 40_000m, 80m);
        db.AddRange(category, bank, month, crcTx, usdTx);
        await db.SaveChangesAsync();
        return (tenant, month.Id, crcTx, usdTx);
    }

    private static Refund RefundOn(Guid tenant, Guid month, Transaction tx, decimal? pct, decimal crc, decimal usd) => new()
    {
        TenantId = tenant, MonthId = month, TransactionId = tx.Id, Payee = tx.Payee, TransactionDate = tx.TransactionDate,
        Percentage = pct, AmountCrc = crc, AmountUsd = usd, CreatedAt = T0, UpdatedAt = T0,
    };

    [Fact]
    public async Task Up_KeepsEveryRefundVerbatim_AndOnlyRelaxesTheColumn()
    {
        await using var db = Context();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(Migration(db, "RefundPercentageOptional", -1));
        var (tenant, month, crcTx, _) = await SeedPurchasesAsync(db);
        db.Add(RefundOn(tenant, month, crcTx, 30m, 15_000m, 30m));
        await db.SaveChangesAsync();
        var before = await RefundsAsync();

        await migrator.MigrateAsync();

        Assert.Equal(before, await RefundsAsync()); // amounts and the legacy percentage, to the cent
        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""SELECT is_nullable FROM information_schema.columns WHERE table_name = 'Refunds' AND column_name = 'Percentage'""", conn);
        Assert.Equal("YES", await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Down_RefillsAnEmptyPercentage_FromTheAmounts_OnThePurchasesOwnSide_AndKeepsTheOthers()
    {
        await using var db = Context();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync();
        var (tenant, month, crcTx, usdTx) = await SeedPurchasesAsync(db);
        db.AddRange(
            RefundOn(tenant, month, crcTx, 30m, 15_000m, 30m),           // legacy: entered as 30 %
            RefundOn(tenant, month, usdTx, null, 6_170m, 12.34m));       // #202: an amount, $12.34 of $80
        await db.SaveChangesAsync();

        await migrator.MigrateAsync(Migration(db, "RefundPercentageOptional", -1));

        var rows = await RefundsAsync();
        Assert.Equal((30m, 15_000m, 30m), (rows[0].Pct, rows[0].Crc, rows[0].Usd));          // Clinic untouched
        Assert.Equal((15.43m, 6_170m, 12.34m), (rows[1].Pct, rows[1].Crc, rows[1].Usd));     // Pharmacy: 12.34 / 80 on the dollar side
    }
}
