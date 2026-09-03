using Vuelto.Core.Vouchers;
using Vuelto.Infrastructure.Vouchers;

namespace Vuelto.Api.Tests.Vouchers;

/// <summary>EMAIL-1 (donor US-025): the three built-in extractors over anonymized real-shape bodies — every field, and graceful degradation on broken HTML.</summary>
public class VoucherExtractorTests
{
    private const string BacHtml = """
        <html><body>
        <table>
          <tr><td>Comercio:</td><td>SUPER MARIANO</td></tr>
          <tr><td>Fecha:</td><td>12 Ene, 2026 - 14:30</td></tr>
          <tr><td>VISA</td><td>************1234</td></tr>
          <tr><td>Autorización:</td><td>123456</td></tr>
          <tr><td>Referencia:</td><td>987654321</td></tr>
          <tr><td>Tipo de Transacción:</td><td>COMPRA</td></tr>
          <tr><td>Monto:</td><td>CRC 52000.00</td></tr>
        </table>
        </body></html>
        """;

    private const string BnVoucherHtml = """
        <html><body>
        <p style="background-color:#b0bd20; color:#fff">AUTOMERCADO ESCAZU</p>
        <p>Adjunto el comprobante de COMPRA realizada con su tarjeta.</p>
        <table id="tCompra">
          <tr><td>15 Feb 2026</td></tr>
          <tr><td>MASTERCARD</td><td>************5678</td></tr>
          <tr><td>NRO. AUT:</td><td>445566</td></tr>
          <tr><td>REF:</td><td>112233445</td></tr>
          <tr><td>TOTAL:</td><td>CRC 18750.50</td></tr>
        </table>
        </body></html>
        """;

    private const string BnPaymentHtml = """
        <html><body>
        <div>
          <b><font color="#102356">INSTITUTO COSTARRICENSE DE ELECTRICIDAD<br/>No_SERVICIO : </font></b>123456<br/>
          <b><font color="#102356">No. comprobante débito :</font></b> 7788990<br/>
          <b><font color="#102356">Moneda :</font></b> COLONES<br/>
          <b><font color="#102356">Monto :</font></b> 32450.00<br/>
          <b><font color="#102356">Tarjeta de crédito :</font></b> ************4321<br/>
          <b><font color="#102356">Fecha y hora del pago :</font></b> 16/06/2026 14:30:00<br/>
        </div>
        </body></html>
        """;

    [Fact]
    public void Identities()
    {
        Assert.Equal((VoucherBank.Bac, VoucherSources.Bac), (new BacVoucherExtractor().Bank, new BacVoucherExtractor().Key));
        Assert.Equal((VoucherBank.BN, VoucherSources.BnVoucher), (new BnVoucherExtractor().Bank, new BnVoucherExtractor().Key));
        Assert.Equal((VoucherBank.BN, VoucherSources.BnPayment), (new BnPaymentExtractor().Bank, new BnPaymentExtractor().Key));
    }

    [Fact]
    public void Bac_ExtractsAllFields()
    {
        var v = new BacVoucherExtractor().Extract(BacHtml);
        Assert.Equal((VoucherBank.Bac, "SUPER MARIANO", new DateOnly(2026, 1, 12), "************1234"), (v.Bank, v.Merchant, v.Date, v.CardNumber));
        Assert.Equal(("123456", "987654321", "COMPRA", "CRC", 52000.00m), (v.Authorization, v.Reference, v.TransactionType, v.Currency, v.Amount));
    }

    [Fact]
    public void BnVoucher_ExtractsAllFields()
    {
        var v = new BnVoucherExtractor().Extract(BnVoucherHtml);
        Assert.Equal((VoucherBank.BN, "AUTOMERCADO ESCAZU", "COMPRA", new DateOnly(2026, 2, 15)), (v.Bank, v.Merchant, v.TransactionType, v.Date));
        Assert.Equal(("************5678", "445566", "112233445", "CRC", 18750.50m), (v.CardNumber, v.Authorization, v.Reference, v.Currency, v.Amount));
    }

    [Fact]
    public void BnPayment_ExtractsAllFields_ServiceNameFromTheFirstHeadingLine()
    {
        var v = new BnPaymentExtractor().Extract(BnPaymentHtml);
        Assert.Equal((VoucherBank.BN, "INSTITUTO COSTARRICENSE DE ELECTRICIDAD", "PAGO"), (v.Bank, v.Merchant, v.TransactionType));
        Assert.Equal(("7788990", "7788990", "CRC", 32450.00m, "************4321", new DateOnly(2026, 6, 16)), (v.Reference, v.Authorization, v.Currency, v.Amount, v.CardNumber, v.Date));
    }

    [Theory]
    [InlineData("<html><body><table><tr><td>broken")]
    [InlineData("")]
    [InlineData(null)]
    public void Bac_MalformedOrEmpty_DegradesToBankOnly(string? html)
    {
        var v = new BacVoucherExtractor().Extract(html!);
        Assert.Equal(VoucherBank.Bac, v.Bank);
        Assert.Null(v.Amount); Assert.Null(v.Merchant);
    }

    [Fact]
    public void BnVoucher_Malformed_DegradesWithoutThrowing()
    {
        var v = new BnVoucherExtractor().Extract("<html><body><p>comprobante de");
        Assert.Equal(VoucherBank.BN, v.Bank);
        Assert.Null(v.Amount);
    }

    [Fact]
    public void BnPayment_Malformed_KeepsPagoType_AndDoesNotThrow()
    {
        var v = new BnPaymentExtractor().Extract("<html><body><font color='#102356'>broken");
        Assert.Equal((VoucherBank.BN, "PAGO"), (v.Bank, v.TransactionType));
        Assert.Null(v.Amount);
    }
}
