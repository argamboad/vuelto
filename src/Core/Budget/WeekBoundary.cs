namespace Vuelto.Core.Budget;

/// <summary>One week's boundary within a budget month (inclusive dates).</summary>
public record WeekBoundary(int WeekNumber, DateOnly StartDate, DateOnly EndDate);
