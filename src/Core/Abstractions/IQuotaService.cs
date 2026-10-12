namespace Vuelto.Core.Abstractions;

/// <summary>
/// Enforces plan-tier **quotas** (BILLING-5) — countable limits, distinct from feature entitlements
/// (booleans, <see cref="IEntitlementService"/>) and from per-IP rate limits (abuse throttling). Two kinds:
/// <b>seats</b> (tenant members + pending invites vs the plan's seat limit) and <b>metered usage</b>
/// (a monthly counter per action). A plan with no limit for a dimension means <b>unlimited</b>, so quotas
/// stay inert until a plan sets a real number. Reads the current tenant's plan, failing closed to Free.
/// </summary>
public interface IQuotaService
{
    /// <summary>Current seat usage (members + pending invites) against the plan's seat limit.</summary>
    Task<SeatUsage> GetSeatUsageAsync(CancellationToken cancellationToken = default);

    /// <summary>True if the current tenant's plan permits adding <paramref name="count"/> more seat(s).</summary>
    Task<bool> CanAddSeatsAsync(int count = 1, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically consumes <paramref name="amount"/> of a metered key for the current month if it stays
    /// within the plan's limit; returns <c>false</c> without incrementing when it would exceed. A plan
    /// with no limit for the key is unlimited — returns <c>true</c> and records nothing.
    /// </summary>
    Task<bool> TryConsumeAsync(string usageKey, int amount = 1, CancellationToken cancellationToken = default);
}

/// <summary>Seat usage vs the plan cap; <see cref="Limit"/> null = unlimited.</summary>
public readonly record struct SeatUsage(int Used, int? Limit)
{
    /// <summary>True when the plan caps seats and usage is at or over the cap.</summary>
    public bool AtLimit => Limit is { } limit && Used >= limit;

    /// <summary>True when the plan allows <paramref name="count"/> more seat(s).</summary>
    public bool CanAdd(int count = 1) => Limit is not { } limit || Used + count <= limit;
}
