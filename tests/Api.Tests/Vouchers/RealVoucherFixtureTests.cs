using Vuelto.Infrastructure.Vouchers;

namespace Vuelto.Api.Tests.Vouchers;

/// <summary>
/// EMAIL-1 against <b>real, anonymized</b> voucher bodies captured from live BAC/BN emails. These guard the
/// exact real-world DOM — the <c>&lt;td&gt;&lt;p&gt;</c> BAC rows, the single colspan date cell, the Spanish
/// month abbreviations, and the inline <c>&lt;b&gt;&lt;font&gt;</c> BN-payment layout — so a silent bank
/// format change shows up here first.
/// </summary>
public class RealVoucherFixtureTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vouchers", "Fixtures", name));

    [Fact]
    public void Bac_RealBody_ParsesEveryField()
    {
        var v = new BacVoucherExtractor().Extract(Fixture("bac-anthropic.html"));
        Assert.Equal(("ANTHROPIC* CLAUDE SUB", "USD", 83.99m, new DateOnly(2026, 6, 13)), (v.Merchant, v.Currency, v.Amount, v.Date));
        Assert.Equal(("************0000", "000000", "000000000000", "COMPRA"), (v.CardNumber, v.Authorization, v.Reference, v.TransactionType));
    }

    [Fact]
    public void BnVoucher_RealBody_ParsesEveryField()
    {
        var v = new BnVoucherExtractor().Extract(Fixture("bn-voucher.html"));
        Assert.Equal(("Google YouTubePremium  Mountain View USA", "COMPRA", "CRC", 8390.00m, new DateOnly(2026, 6, 9)), (v.Merchant, v.TransactionType, v.Currency, v.Amount, v.Date));
        Assert.Equal(("************0000", "000000", "000000000000"), (v.CardNumber, v.Authorization, v.Reference));
    }

    [Fact]
    public void BnPayment_RealBody_ParsesTheServiceName_NotTheTableHeader()
    {
        var v = new BnPaymentExtractor().Extract(Fixture("bn-payment.html"));
        Assert.Equal("ICETELECOMUNICACIONES -  COBRO DE RECIBOS TELEFONICOS", v.Merchant);
        Assert.Equal(("PAGO", "CRC", 29730.00m, new DateOnly(2026, 6, 15)), (v.TransactionType, v.Currency, v.Amount, v.Date));
        Assert.Equal(("XXXXXXXXXXX0000X", "00000000", "00000000"), (v.CardNumber, v.Reference, v.Authorization));
    }
}
