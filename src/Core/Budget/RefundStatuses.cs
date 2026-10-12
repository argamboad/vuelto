namespace Vuelto.Core.Budget;

/// <summary>Refund lifecycle (ADR-V007): expected (<see cref="Pending"/>) or landed (<see cref="Received"/>, which books a derived inflow).</summary>
public static class RefundStatuses
{
    public const string Pending = "pending";
    public const string Received = "received";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Pending, Received };

    /// <summary>Normalizes user input to a stored code, or null when it is not a status.</summary>
    public static string? Normalize(string? value)
    {
        var lower = value?.Trim().ToLowerInvariant();
        return lower is not null && All.Contains(lower) ? lower : null;
    }
}
