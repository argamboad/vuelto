namespace Vuelto.Shared.Ui.Components.Charts;

/// <summary>One horizontal bar: the actual value, and optionally the budget it is measured against (same currency).</summary>
public sealed record BarItem(string Label, decimal Value, decimal? Budget = null);

/// <summary>One donut slice; <paramref name="CssColor"/> is a CSS colour expression (a Bootstrap token, so it follows the theme).</summary>
public sealed record DonutSlice(string Label, decimal Value, string CssColor);
