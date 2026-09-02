namespace Perezosoft.Core.Entities;

/// <summary>
/// A tenant's billing subscription — a local **projection** of the payment provider's state (Stripe is
/// the source of truth for money; ADR-006). A tenant has at most one (unique on <c>TenantId</c>).
/// **Absence ⇒ Free**, and any non-active status fails closed to Free — entitlements are never granted
/// on stale/uncertain state. The Stripe ids are null until BILLING-2 attaches a paid plan.
/// </summary>
public class Subscription : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }

    /// <summary>The plan this subscription grants while active — a <c>PlanKeys</c> value.</summary>
    public required string PlanKey { get; set; }

    /// <summary>One of <see cref="SubscriptionStatus"/>. Only active/trialing grant the plan.</summary>
    public required string Status { get; set; }

    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }

    /// <summary>End of the paid period; past = lapsed (fails closed to Free even if Status is active).</summary>
    public DateTimeOffset? CurrentPeriodEnd { get; set; }

    /// <summary>
    /// When the tenant was last notified that this subscription lapsed (BILLING-6). Set by the lapse
    /// sweep so it nudges once per lapse, not every run; cleared implicitly when a new period starts
    /// (the sweep re-notifies only if this predates <see cref="CurrentPeriodEnd"/>).
    /// </summary>
    public DateTimeOffset? LapseNotifiedAt { get; set; }

    /// <summary>
    /// When the provider emitted the most recently <em>applied</em> webhook event. The webhook handler
    /// rejects only events strictly OLDER than this (v3 audit LB-BILL-1 relaxed v2 LOGIC-B1's
    /// strictly-newer rule: provider timestamps are whole-second, so two distinct same-second events
    /// must both apply; exact redeliveries are deduped upstream by the inbox on EventId). Null until
    /// the first event is applied.
    /// </summary>
    public DateTimeOffset? LastEventAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Whether a live provider (Stripe) subscription owns this projection — the guard that stops staff
    /// comp/revert from fighting real money (ADR-006). Keyed on LIVENESS, not id-presence: a canceled
    /// subscription keeps its <see cref="StripeSubscriptionId"/> forever, so an id alone would permanently
    /// lock a churned tenant out of a goodwill comp / cleanup (v3 audit ADM-5). A terminal
    /// <see cref="SubscriptionStatus.Canceled"/> is no longer provider-managed. Computed, never stored.
    /// </summary>
    public bool IsProviderManaged =>
        StripeSubscriptionId is not null && Status != SubscriptionStatus.Canceled;
}

/// <summary>Status constants for <see cref="Subscription.Status"/> (mirrors the Stripe lifecycle).</summary>
public static class SubscriptionStatus
{
    public const string Active = "active";
    public const string Trialing = "trialing";
    public const string PastDue = "past_due";
    public const string Canceled = "canceled";

    /// <summary>Statuses under which the plan is actually granted (live). Dunning is a transition OUT of one
    /// of these, so this is also what tells a cold-start `past_due`/`canceled` from a real lapse (LB-BILL-4).</summary>
    public static bool IsGranting(string? status) => status is Active or Trialing;
}
