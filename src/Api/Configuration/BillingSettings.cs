namespace Vuelto.Api.Configuration;

/// <summary>
/// Toggles the billing surface (GATES-1, ADR-027). **Default off** — a deployment opts in deliberately,
/// exactly like PUBAPI/HOOKS (ADR-015/016). With the gate off the deployment runs private and free:
/// no tenant can be sold anything, and because <c>PlanCatalog.Get</c> already falls back to Free for an
/// absent plan key, every tenant simply resolves to Free with no economics code of its own.
/// <para>
/// Gating is <b>structural, not a runtime check</b>: <see cref="BillingGateConvention"/> removes the
/// billing controllers from the MVC application model at startup, so the routes do not exist (404)
/// rather than existing and refusing. That is what stops a future billing endpoint from quietly
/// shipping ungated — there is nothing per-endpoint to remember.
/// </para>
/// Bound from the <c>Billing</c> config section (env <c>Billing__Enabled</c>), which also carries the
/// provider settings (<c>Billing__Stripe__*</c>) — those stay inert while this is off.
/// </summary>
public sealed class BillingSettings
{
    public const string SectionName = "Billing";

    public bool Enabled { get; set; }
}
