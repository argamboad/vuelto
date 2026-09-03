namespace Vuelto.Core.Budget;

/// <summary>
/// The single keyed source of default catalog names (ADR-V009). Each seed has a stable,
/// language-independent key — later slices reference categories by key, never by a localized name,
/// which is what once produced Spanish households with duplicate English categories. Seeded rows
/// become ordinary household data: they are localized once, at seeding, and never retranslated.
/// The shipped set is deliberately minimal (owner decision): a handful of examples, not a full budget.
/// </summary>
public static class SeedCatalog
{
    public sealed record CatalogSeed(string Key, string En, string Es);

    public static readonly IReadOnlyList<CatalogSeed> Categories =
    [
        new("food",          "Food",          "Alimentación"),
        new("transport",     "Transport",     "Transporte"),
        new("housing",       "Housing",       "Vivienda"),
        new("health",        "Health",        "Salud"),
        new("entertainment", "Entertainment", "Entretenimiento"),
        new("internet",      "Internet",      "Internet"),
        new("other",         "Other",         "Otro"),
    ];

    /// <summary>
    /// Cash + the common Costa Rican banks. Only Cash/Efectivo differs by language; the rest are
    /// proper nouns. Cash stays first — it is the fallback source for cash and unmatched vouchers.
    /// </summary>
    public static readonly IReadOnlyList<CatalogSeed> Banks =
    [
        new("cash",           "Cash",           "Efectivo"),
        new("bac_credomatic", "BAC Credomatic", "BAC Credomatic"),
        new("banco_nacional", "Banco Nacional", "Banco Nacional"),
        new("bcr",            "BCR",            "BCR"),
        new("banco_popular",  "Banco Popular",  "Banco Popular"),
        new("scotiabank",     "Scotiabank",     "Scotiabank"),
        new("davivienda",     "Davivienda",     "Davivienda"),
        new("promerica",      "Promerica",      "Promerica"),
        new("lafise",         "Lafise",         "Lafise"),
    ];

    /// <summary>The key of the fallback bank (<c>Cash</c> / <c>Efectivo</c>).</summary>
    public const string CashKey = "cash";

    /// <summary>Spanish for an <c>es</c> / <c>es-*</c> locale; English for anything else (the base language).</summary>
    public static bool IsSpanish(string? locale) =>
        locale is not null && (locale.Equals("es", StringComparison.OrdinalIgnoreCase)
                               || locale.StartsWith("es-", StringComparison.OrdinalIgnoreCase));

    public static string Localize(CatalogSeed seed, string? locale) => IsSpanish(locale) ? seed.Es : seed.En;

    public static IReadOnlyList<string> CategoryNames(string? locale) => Categories.Select(s => Localize(s, locale)).ToList();

    public static IReadOnlyList<string> BankNames(string? locale) => Banks.Select(s => Localize(s, locale)).ToList();

    /// <summary>The localized name for a category key; throws on an unknown key (a seed-definition bug, caught by tests).</summary>
    public static string CategoryName(string key, string? locale) =>
        Localize(Categories.Single(s => s.Key == key), locale);
}
