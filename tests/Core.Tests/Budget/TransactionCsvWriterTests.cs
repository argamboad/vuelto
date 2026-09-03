using Vuelto.Core.Budget;

namespace Vuelto.Core.Tests.Budget;

/// <summary>REPORTS-2 (donor US-044 + WU-4 B5): fixed columns, plain 2-dp amounts, 4-dp frozen rate, ISO dates, RFC 4180 quoting, header-only when empty.</summary>
public class TransactionCsvWriterTests
{
    private static TransactionExportRow Row(string payee = "Super MAS", string? category = "Groceries", string? bank = "BAC", decimal crc = 15750m, decimal usd = 31.5m, decimal rate = 500m) =>
        new(new DateOnly(2026, 6, 15), payee, category, "budgeted", crc, usd, rate, "credit_card", bank, "manual");

    private static string[] Lines(string csv) => csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Empty_IsHeaderOnly()
    {
        var lines = Lines(TransactionCsvWriter.Write([]));
        Assert.Single(lines);
        Assert.Equal("date,payee,category,class,amount_crc,amount_usd,exchange_rate_used,payment_method,bank,source", lines[0]);
    }

    [Fact]
    public void Row_HasEveryColumnInOrder_WithPlainAmounts()
    {
        var fields = Lines(TransactionCsvWriter.Write([Row()]))[1].Split(',');
        Assert.Equal(["2026-06-15", "Super MAS", "Groceries", "budgeted", "15750.00", "31.50", "500.0000", "credit_card", "BAC", "manual"], fields);
        Assert.DoesNotContain("₡", string.Join(',', fields));
        Assert.DoesNotContain("$", string.Join(',', fields));
    }

    [Fact]
    public void ExchangeRate_KeepsFourDecimals()
    {
        var fields = Lines(TransactionCsvWriter.Write([Row(rate: 512.3456m)]))[1].Split(',');
        Assert.Equal("512.3456", fields[6]);
    }

    [Fact]
    public void Text_IsQuotedOnlyWhenNeeded()
    {
        var line = Lines(TransactionCsvWriter.Write([Row(payee: "Café \"El\" Punto, S.A.", category: null, bank: "Line\nBreak")]))[1];
        Assert.Equal("2026-06-15,\"Café \"\"El\"\" Punto, S.A.\",,budgeted,15750.00,31.50,500.0000,credit_card,\"Line\nBreak\",manual", line);
    }

    [Fact]
    public void Rows_KeepTheCallersOrder()
    {
        var csv = TransactionCsvWriter.Write([Row(payee: "Newer"), Row(payee: "Older")]);
        var lines = Lines(csv);
        Assert.Contains("Newer", lines[1]);
        Assert.Contains("Older", lines[2]);
        Assert.EndsWith("\r\n", csv);
    }
}
