using Microsoft.Extensions.DependencyInjection;
using Vuelto.Core.Vouchers;
using Vuelto.Infrastructure.Vouchers;

namespace Vuelto.Api.Tests.Vouchers;

/// <summary>EMAIL-1 (donor US-025): the facade routes by the map, validates required fields, never throws, and the DI registration wires all three extractors.</summary>
public class VoucherParserTests
{
    private static readonly IBankVoucherExtractor[] Extractors = [new BacVoucherExtractor(), new BnVoucherExtractor(), new BnPaymentExtractor()];

    private static IVoucherParser BuildParser(BankVoucherMap? map = null) => new VoucherParser(Extractors, map ?? BankVoucherMap.Default);

    private static VoucherMessage Message(string? subject, string html) =>
        new("msg-1", subject, "notificacion@example.com", DateTimeOffset.Parse("2026-06-16T00:00:00Z"), html);

    private const string BacHtml = """
        <table>
          <tr><td>Comercio:</td><td>SUPER MARIANO</td></tr>
          <tr><td>Fecha:</td><td>12 Ene, 2026</td></tr>
          <tr><td>Monto:</td><td>CRC 52000.00</td></tr>
        </table>
        """;

    [Fact]
    public void RoutesBacSubject_ToTheBacExtractor_AndIsComplete()
    {
        var v = BuildParser().Parse(Message("Notificación de transacción", BacHtml))!;
        Assert.Equal((VoucherBank.Bac, "SUPER MARIANO", true), (v.Bank, v.Merchant, v.IsComplete));
        Assert.Empty(v.MissingFields);
    }

    [Fact]
    public void RoutesBnPaymentSubject_ToThePaymentExtractor()
    {
        const string html = """
            <font color="#102356">ICE</font>
            <div><b><font color="#102356">Monto :</font></b> 1000.00<br/>
            <b><font color="#102356">Moneda :</font></b> COLONES<br/>
            <b><font color="#102356">Fecha y hora del pago :</font></b> 16/06/2026</div>
            """;
        var v = BuildParser().Parse(Message("BN Conectividad le informa", html))!;
        Assert.Equal((VoucherBank.BN, "PAGO"), (v.Bank, v.TransactionType));
    }

    [Fact]
    public void UnrelatedMail_AndNullMessage_ReturnNull()
    {
        Assert.Null(BuildParser().Parse(Message("Your statement is ready", "<html/>")));
        Assert.Null(BuildParser().Parse(Message(null, "<html/>")));
        Assert.Null(BuildParser().Parse(null!));
    }

    [Fact]
    public void MissingRequiredFields_AreFlagged_NotComplete()
    {
        var v = BuildParser().Parse(Message("Notificación de transacción", "<table></table>"))!;
        Assert.False(v.IsComplete);
        Assert.Equal([nameof(ParsedVoucher.Merchant), nameof(ParsedVoucher.Amount), nameof(ParsedVoucher.Currency), nameof(ParsedVoucher.Date)], v.MissingFields);
    }

    [Fact]
    public void ZeroAmount_CountsAsMissing()
    {
        const string html = """
            <table>
              <tr><td>Comercio:</td><td>X</td></tr>
              <tr><td>Fecha:</td><td>12 Ene 2026</td></tr>
              <tr><td>Monto:</td><td>CRC 0.00</td></tr>
            </table>
            """;
        Assert.Contains(nameof(ParsedVoucher.Amount), BuildParser().Parse(Message("Notificación de transacción", html))!.MissingFields);
    }

    [Fact]
    public void CustomMap_RoutesBySender_WithMultiplePairsPerBank()
    {
        var map = new BankVoucherMap(
        [
            new VoucherRoutingRule(VoucherSources.Bac, SenderContains: "baccredomatic.com"),
            new VoucherRoutingRule(VoucherSources.Bac, SubjectPrefix: "Compra aprobada"),
        ]);
        var parser = BuildParser(map);

        Assert.Equal(VoucherBank.Bac, parser.Parse(new VoucherMessage("m1", "anything at all", "alertas@baccredomatic.com", null, BacHtml))?.Bank);
        Assert.Equal(VoucherBank.Bac, parser.Parse(new VoucherMessage("m2", "Compra aprobada en SUPER MARIANO", "noreply@elsewhere.com", null, BacHtml))?.Bank);
        Assert.Null(parser.Parse(Message("Notificación de transacción", BacHtml))); // the default subject is NOT in this map
    }

    [Fact]
    public void ThrowingExtractor_IsContained_AsAnIncompleteVoucher()
    {
        var parser = new VoucherParser([new ThrowingExtractor()], new BankVoucherMap([new VoucherRoutingRule("boom", SubjectPrefix: "X")]));
        var v = parser.Parse(Message("X", "<html/>"))!;
        Assert.Equal(VoucherBank.Unknown, v.Bank);
        Assert.False(v.IsComplete);
    }

    [Fact]
    public void AddVoucherParsing_WiresTheThreeExtractors_TheDefaultMap_AndTheFacade()
    {
        var services = new ServiceCollection().AddVoucherParsing().BuildServiceProvider();
        Assert.Equal(3, services.GetServices<IBankVoucherExtractor>().Count());
        Assert.Same(BankVoucherMap.Default, services.GetRequiredService<BankVoucherMap>());
        Assert.NotNull(services.GetRequiredService<IVoucherParser>().Parse(Message("Voucher Digital", "<html/>")));
    }

    private sealed class ThrowingExtractor : IBankVoucherExtractor
    {
        public string Key => "boom";
        public VoucherBank Bank => VoucherBank.Unknown;
        public ParsedVoucher Extract(string htmlBody) => throw new InvalidOperationException("boom");
    }
}
