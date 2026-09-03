using Vuelto.Core.Vouchers;

namespace Vuelto.Core.Tests.Vouchers;

/// <summary>EMAIL-1 (donor US-025 + WU-4 A8): label normalization, money parsing incl. the dot-thousands rule, currency words, card brands.</summary>
public class VoucherTextTests
{
    [Theory]
    [InlineData("Comercio:", "COMERCIO")]
    [InlineData(" comercio ", "COMERCIO")]
    [InlineData("COMERCIO", "COMERCIO")]
    [InlineData("Nro. Aut:", "NRO AUT")]
    [InlineData("NRO. AUT:", "NRO AUT")]
    [InlineData("Autorización:", "AUTORIZACION")]
    [InlineData("Tipo de Transacción:", "TIPO DE TRANSACCION")]
    [InlineData("No. comprobante débito :", "NO COMPROBANTE DEBITO")]
    public void NormalizeLabel_IsTolerantOfCosmetics(string raw, string expected) => Assert.Equal(expected, VoucherText.NormalizeLabel(raw));

    [Theory]
    [InlineData("CRC 52000.00", "CRC", 52000.00)]
    [InlineData("₡52,000.00", "CRC", 52000.00)]
    [InlineData("COLONES 5000", "CRC", 5000)]
    [InlineData("$ 1,234.50", "USD", 1234.50)]
    [InlineData("USD 1234.50", "USD", 1234.50)]
    [InlineData("18750.50", null, 18750.50)]
    [InlineData("5.000,75", null, 5000.75)]
    [InlineData("₡1.500", "CRC", 1500)]        // a lone dot with 3 trailing digits is a thousands separator
    [InlineData("1.500", null, 1500)]
    [InlineData("₡1.234", "CRC", 1234)]
    [InlineData("1.234.567", null, 1234567)]
    public void TryParseMoney_ReadsCurrencyAndAmount(string raw, string? currency, double amount)
    {
        Assert.True(VoucherText.TryParseMoney(raw, out var cur, out var amt));
        Assert.Equal(currency, cur);
        Assert.Equal((decimal)amount, amt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no digits here")]
    public void TryParseMoney_IsFalseWithoutANumber(string? raw)
    {
        Assert.False(VoucherText.TryParseMoney(raw, out _, out var amt));
        Assert.Equal(0m, amt);
    }

    [Theory]
    [InlineData("COLONES", "CRC")]
    [InlineData("DOLARES", "USD")]
    [InlineData("CRC", "CRC")]
    [InlineData("USD", "USD")]
    [InlineData("EUROS", null)]
    public void NormalizeCurrency_MapsKnownWords(string raw, string? expected) => Assert.Equal(expected, VoucherText.NormalizeCurrency(raw));

    [Theory]
    [InlineData("VISA", true)]
    [InlineData("MASTERCARD", true)]
    [InlineData("AMEX", true)]
    [InlineData("COMERCIO", false)]
    public void IsCardBrand_RecognizesBrands(string normalized, bool expected) => Assert.Equal(expected, VoucherText.IsCardBrand(normalized));
}
