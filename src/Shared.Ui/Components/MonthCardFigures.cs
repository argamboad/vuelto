namespace Vuelto.Shared.Ui.Components;

/// <summary>A dual-currency amount as the API sends it — both sides frozen or projected, never derived here.</summary>
public sealed record MoneyPair(decimal Crc, decimal Usd);

/// <summary>
/// What a <see cref="MonthCard"/> draws once the month's summary has arrived (SKIN-8). <paramref name="Planned"/>
/// is what is budgeted and not yet spent; <paramref name="Result"/> is the month's outcome — the forecast while
/// the month runs, what was actually left once it has closed. The page decides which; the card only draws.
/// </summary>
public sealed record MonthCardFigures(MoneyPair Income, MoneyPair Spent, MoneyPair Planned, MoneyPair Result);
