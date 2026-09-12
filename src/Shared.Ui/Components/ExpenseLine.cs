using System.Globalization;
using System.Text.Json.Serialization;

namespace Vuelto.Shared.Ui.Components;

/// <summary>One budget line as the API sends it (EXPENSES-1): a single-currency budget — exactly one of the two sides is set.</summary>
public sealed record ExpenseLine(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("budget_crc")] decimal BudgetCrc,
    [property: JsonPropertyName("budget_usd")] decimal BudgetUsd,
    [property: JsonPropertyName("payment_method")] string PaymentMethod,
    [property: JsonPropertyName("category_id")] Guid CategoryId,
    [property: JsonPropertyName("bank_id")] Guid? BankId,
    [property: JsonPropertyName("sort_order")] int SortOrder,
    [property: JsonPropertyName("is_active")] bool IsActive)
{
    public string Currency => BudgetCrc > 0 ? MoneyDisplay.Crc : MoneyDisplay.Usd;
}

/// <summary>
/// The budget's arithmetic (SKIN-9): lines live in their own currency and are only ever ADDED in one, at
/// today's rate — a projection, never a stored figure. Without a rate nothing is converted: each side is
/// summed on its own and shown side by side, which is the honest answer rather than a guess.
/// </summary>
public static class BudgetMoney
{
    /// <summary>The active lines' sum on the "show in" side, or the colón side alone when nothing can convert.</summary>
    public static decimal Side(IEnumerable<ExpenseLine> lines, decimal? rate, string display)
    {
        var (crc, usd) = Sums(lines);
        if (rate is not { } r || r <= 0) return display == MoneyDisplay.Usd ? usd : crc;
        return display == MoneyDisplay.Usd ? usd + crc / r : crc + usd * r;
    }

    /// <summary>"₡406,500.00" with a rate; "₡400,000.00 + $13.00" without one.</summary>
    public static string Format(IEnumerable<ExpenseLine> lines, decimal? rate, string display)
    {
        var (crc, usd) = Sums(lines);
        if (rate is { } r && r > 0) return Amount(Side(lines, rate, display), display);
        var parts = new List<string>();
        if (crc > 0) parts.Add(Amount(crc, MoneyDisplay.Crc));
        if (usd > 0) parts.Add(Amount(usd, MoneyDisplay.Usd));
        return parts.Count == 0 ? Amount(0, display) : string.Join(" + ", parts);
    }

    public static string Amount(decimal value, string display) =>
        (display == MoneyDisplay.Usd ? "$" : "₡") + value.ToString("N2", CultureInfo.CurrentCulture);

    private static (decimal Crc, decimal Usd) Sums(IEnumerable<ExpenseLine> lines)
    {
        decimal crc = 0, usd = 0;
        foreach (var l in lines.Where(l => l.IsActive)) { crc += l.BudgetCrc; usd += l.BudgetUsd; }
        return (crc, usd);
    }
}
