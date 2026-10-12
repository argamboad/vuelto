using Vuelto.Core.Vouchers;

namespace Vuelto.Core.Tests.Vouchers;

/// <summary>EMAIL-1 (donor US-025 AC3): routing is data — subject prefix / sender substring rules, first match wins, no accidental catch-all.</summary>
public class BankVoucherMapTests
{
    [Theory]
    [InlineData("Notificación de transacción", VoucherSources.Bac)]
    [InlineData("Notificación de transacción - SUPER MARIANO", VoucherSources.Bac)]
    [InlineData("notificación de transacción", VoucherSources.Bac)]
    [InlineData("Voucher Digital", VoucherSources.BnVoucher)]
    [InlineData("BN Conectividad le informa", VoucherSources.BnPayment)]
    [InlineData("Your monthly statement", null)]
    [InlineData(null, null)]
    public void DefaultMap_ResolvesBySubject(string? subject, string? expected) => Assert.Equal(expected, BankVoucherMap.Default.Resolve(sender: null, subject));

    [Fact]
    public void DefaultMap_ExposesSubjectFilters_AndNoSenderFilters()
    {
        Assert.Equal(["Notificación de transacción", "Voucher Digital", "BN Conectividad le informa"], BankVoucherMap.Default.SubjectFilters);
        Assert.Empty(BankVoucherMap.Default.SenderFilters);
        Assert.Equal(["notificacion@notificacionesbaccr.com", "bncontacto@bncr.fi.cr"], KnownVoucherSenders.All);
    }

    [Fact]
    public void Rule_MatchesSender_AsCaseInsensitiveSubstring()
    {
        var rule = new VoucherRoutingRule(VoucherSources.Bac, SenderContains: "baccredomatic.com");
        Assert.True(rule.Matches("Alertas@BACcredomatic.com", subject: null));
        Assert.True(rule.Matches("notificacion@mail.baccredomatic.com", subject: null));
        Assert.False(rule.Matches("noreply@bn.fi.cr", subject: null));
        Assert.False(rule.Matches(sender: null, subject: null));
    }

    [Fact]
    public void Rule_WithBothFields_RequiresBoth()
    {
        var rule = new VoucherRoutingRule(VoucherSources.Bac, SenderContains: "baccredomatic.com", SubjectPrefix: "Compra");
        Assert.True(rule.Matches("x@baccredomatic.com", "Compra aprobada"));
        Assert.False(rule.Matches("x@baccredomatic.com", "Algo más"));
        Assert.False(rule.Matches("x@otra.com", "Compra aprobada"));
    }

    [Fact]
    public void Rule_WithNoCriteria_NeverMatches() => Assert.False(new VoucherRoutingRule(VoucherSources.Bac).Matches("anyone@anywhere.com", "any subject"));

    [Fact]
    public void FirstMatchingRule_Wins()
    {
        var map = new BankVoucherMap(
        [
            new VoucherRoutingRule(VoucherSources.BnVoucher, SubjectPrefix: "Pago"),
            new VoucherRoutingRule(VoucherSources.BnPayment, SubjectPrefix: "Pago de servicios"),
        ]);
        Assert.Equal(VoucherSources.BnVoucher, map.Resolve(null, "Pago de servicios públicos"));
    }
}
