namespace Vuelto.Shared.Ui.Components;

/// <summary>
/// One budget line as the dashboard shows it: what it is, what was planned, what happened, and how far the
/// bar has run. <paramref name="Fill"/> is 0..1 of the plan; <paramref name="Over"/> is decided by the
/// CALLER, because whether a line is over depends on its own currency — a $2.99 line paid at $2.99 must not
/// turn red because its colon projection moved a few colones. The component draws; it does not judge.
/// </summary>
public sealed record BudgetLineRow(string Name, string Budget, string Delta, decimal Fill, bool Over, bool Inactive = false);
