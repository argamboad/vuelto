namespace Vuelto.Api.Configuration;

/// <summary>
/// Toggles the outbound webhooks surface (HOOKS, ADR-016). **Default off** — like the public API, a
/// deployment opts in deliberately. When disabled, the <c>/api/webhooks</c> management routes aren't
/// mapped (they 404) so tenants can't register endpoints; the outbox delivery handler is dormant with
/// nothing to deliver. Bound from the <c>Webhooks</c> config section (env <c>Webhooks__Enabled</c>).
/// </summary>
public sealed class WebhooksSettings
{
    public const string SectionName = "Webhooks";

    public bool Enabled { get; set; }
}
