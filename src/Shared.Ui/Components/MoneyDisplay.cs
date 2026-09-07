using System.Globalization;

namespace Vuelto.Shared.Ui.Components;

/// <summary>
/// The per-device "show amounts in" preference the Reports tables and the dashboard share: colones, dollars,
/// or both sides of every pair (the default, and the only way to see a purchase's frozen amounts side by side).
/// A figure set in ONE currency — a budget line — always shows on its own side (<see cref="Native"/>); only
/// converted pairs and totals follow the preference. Remembered per device, never account data.
/// </summary>
public static class MoneyDisplay
{
    public const string PrefKey = "display.currency";
    public const string Crc = "CRC", Usd = "USD", Both = "both";

    public static string Parse(string? value) => value is Crc or Usd or Both ? value : Both;

    public static string Format(decimal crc, decimal usd, string display) => display switch
    {
        Crc => "₡" + crc.ToString("N2", CultureInfo.CurrentCulture),
        Usd => "$" + usd.ToString("N2", CultureInfo.CurrentCulture),
        _ => $"₡{crc.ToString("N2", CultureInfo.CurrentCulture)} · ${usd.ToString("N2", CultureInfo.CurrentCulture)}",
    };

    /// <summary>A single-currency figure on its own side, whatever the preference.</summary>
    public static string Native(decimal crc, decimal usd, string? currency) =>
        currency == Usd ? "$" + usd.ToString("N2", CultureInfo.CurrentCulture) : "₡" + crc.ToString("N2", CultureInfo.CurrentCulture);
}
