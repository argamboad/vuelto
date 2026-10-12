using Vuelto.Core.Budget;

namespace Vuelto.Core.Tests.Budget;

/// <summary>The default catalog (ADR-V009): stable keys, both languages, Cash first, English fallback.</summary>
public class SeedCatalogTests
{
    [Fact]
    public void Categories_HaveUniqueStableKeys_AndBothLanguages()
    {
        Assert.Equal(7, SeedCatalog.Categories.Count);
        Assert.Equal(SeedCatalog.Categories.Count, SeedCatalog.Categories.Select(c => c.Key).Distinct().Count());
        Assert.All(SeedCatalog.Categories, c => { Assert.NotEmpty(c.En); Assert.NotEmpty(c.Es); });
    }

    [Theory]
    [InlineData("en", "Food", "Other")]
    [InlineData("es", "Alimentación", "Otro")]
    [InlineData("es-CR", "Alimentación", "Otro")]
    [InlineData("fr", "Food", "Other")]   // unsupported → English base
    [InlineData(null, "Food", "Other")]   // no claim → English base
    public void CategoryNames_LocalizeByLocale_FallingBackToEnglish(string? locale, string first, string last)
    {
        var names = SeedCatalog.CategoryNames(locale);
        Assert.Equal(first, names[0]);
        Assert.Equal(last, names[^1]);
    }

    [Theory]
    [InlineData("en", "Cash")]
    [InlineData("es", "Efectivo")]
    [InlineData("pt", "Cash")]
    public void Banks_CashIsFirst_AndIsTheOnlyLocalizedBank(string locale, string cash)
    {
        var names = SeedCatalog.BankNames(locale);
        Assert.Equal(9, names.Count);
        Assert.Equal(cash, names[0]);
        Assert.Equal(SeedCatalog.CashKey, SeedCatalog.Banks[0].Key);
        Assert.Equal(SeedCatalog.BankNames("en").Skip(1), SeedCatalog.BankNames("es").Skip(1)); // proper nouns
    }

    [Fact]
    public void CategoryName_ByKey_ResolvesAndRejectsUnknownKeys()
    {
        Assert.Equal("Vivienda", SeedCatalog.CategoryName("housing", "es"));
        Assert.Throws<InvalidOperationException>(() => SeedCatalog.CategoryName("nope", "en"));
    }
}
