namespace Perezosoft.Core.Billing;

/// <summary>
/// A plan tier: the entitlement keys it grants (feature on/off) plus its **quotas** — countable limits
/// on seats and metered usage (BILLING-5). Plans are **code/config, not tenant data** (ADR-006).
/// A <c>null</c> seat limit or an absent usage key means <b>unlimited</b> — so quotas stay inert until a
/// plan sets a real number.
/// </summary>
public sealed record Plan(
    string Key,
    IReadOnlySet<string> Entitlements,
    int? SeatLimit = null,
    IReadOnlyDictionary<string, int>? UsageLimits = null)
{
    public bool Includes(string entitlementKey) => Entitlements.Contains(entitlementKey);

    /// <summary>The monthly limit for a metered-usage key, or <c>null</c> (unlimited) if the plan sets none.</summary>
    public int? UsageLimit(string usageKey) =>
        UsageLimits is not null && UsageLimits.TryGetValue(usageKey, out var limit) ? limit : null;
}

/// <summary>Plan-tier keys. <c>Free</c> is the fail-closed default for any tenant without an active paid plan.</summary>
public static class PlanKeys
{
    public const string Free = "free";
    public const string Pro = "pro";
}

/// <summary>
/// Entitlement (feature-gate) keys. These are **example** keys for the platform — replace them with the
/// app's real feature gates, then gate endpoints with <c>.RequireEntitlement(Entitlements.Xxx)</c>.
/// </summary>
public static class Entitlements
{
    public const string ProFeature = "pro-feature";
}

/// <summary>
/// Metered-usage keys (BILLING-5) — countable actions capped per month by a plan's <c>UsageLimits</c> and
/// enforced by calling <c>IQuotaService.TryConsumeAsync(key)</c> at the action. **Example** for the
/// platform; replace with the app's real metered actions.
/// </summary>
public static class UsageKeys
{
    public const string Export = "export";
}

/// <summary>
/// The plan catalog: plan key → <see cref="Plan"/>. Edit/extend this (or move it to configuration) to
/// define the app's tiers. <see cref="Get"/> fails closed — an unknown plan key resolves to Free.
/// </summary>
public static class PlanCatalog
{
    private static readonly IReadOnlyDictionary<string, Plan> Plans = new Dictionary<string, Plan>
    {
        // EXAMPLE quotas (BILLING-5): tune these per app, or set null/omit for unlimited. Seats = tenant
        // members + pending invites; usage keys are monthly, enforced where you call TryConsumeAsync.
        [PlanKeys.Free] = new(PlanKeys.Free, new HashSet<string>(),
            SeatLimit: 3,
            UsageLimits: new Dictionary<string, int> { [UsageKeys.Export] = 3 }),
        [PlanKeys.Pro] = new(PlanKeys.Pro, new HashSet<string> { Entitlements.ProFeature },
            SeatLimit: 10,
            UsageLimits: new Dictionary<string, int> { [UsageKeys.Export] = 100 }),
    };

    /// <summary>Resolves a plan by key; unknown keys fall back to <see cref="PlanKeys.Free"/> (fail-closed).</summary>
    public static Plan Get(string planKey) => Plans.TryGetValue(planKey, out var plan) ? plan : Plans[PlanKeys.Free];
}
