using Microsoft.EntityFrameworkCore;
using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;

namespace Perezosoft.Api.Services;

/// <summary>
/// Makes HOOKS (ADR-016) participate in tenant dissolve + export (LB-TEN-1). Covers <b>two</b> tables that
/// otherwise orphan on dissolve: <see cref="WebhookSubscription"/> (<c>ITenantScoped</c>, carrying an
/// encrypted signing secret + URL) and <see cref="WebhookDelivery"/> (a plain <c>TenantId</c> column, not
/// <c>ITenantScoped</c> — like <c>OutboxMessage</c>), neither of which has an FK cascade. Wipe deletes both;
/// export lists subscription + delivery metadata only — <b>never the <see cref="WebhookSubscription.EncryptedSecret"/>
/// nor the delivery <see cref="WebhookDelivery.Body"/></b>.
/// <para>
/// <see cref="HasDataAsync"/> is <c>false</c>: a webhook subscription is operational plumbing, not tenant
/// content, so it must not block a solo owner from leaving — it is cleaned up automatically instead.
/// </para>
/// </summary>
public sealed class WebhookDataContributor(
    IRepository<WebhookSubscription> subscriptions,
    IRepository<WebhookDelivery> deliveries) : ITenantDataContributor
{
    public Task<bool> HasDataAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public async Task WipeAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        // Query() (not QueryAllTenants): the dissolve enters the target tenant (RLS-2/T6), so the
        // subscription filter scopes this to it; the explicit TenantId predicate also scopes the
        // non-ITenantScoped delivery rows and is fail-safe if ever called un-entered. Composing
        // QueryAllTenants() with a set-based write is banned (RLS-4/T7) — the tag can't sanction it.
        await deliveries.Query()
            .Where(d => d.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
        await subscriptions.Query()
            .Where(s => s.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public string ExportKey => "webhooks";

    // Subscription + delivery metadata only — never the encrypted secret and never the delivery body
    // (which may echo tenant payloads): same secret-free rule as billing / token hashes (ADR-011).
    public async Task<object?> ExportAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var subs = await subscriptions.QueryAllTenants()
            .Where(s => s.TenantId == tenantId)
            .OrderBy(s => s.CreatedAt)
            .Select(s => new { s.Id, s.Url, s.EventTypes, s.CreatedAt, s.DisabledAt })
            .ToListAsync(cancellationToken);

        var recent = await deliveries.QueryAllTenants()
            .Where(d => d.TenantId == tenantId)
            .OrderBy(d => d.CreatedAt)
            .Select(d => new { d.Id, d.SubscriptionId, d.EventType, d.Success, d.StatusCode, d.CreatedAt })
            .ToListAsync(cancellationToken);

        return subs.Count == 0 && recent.Count == 0
            ? null
            : new { subscriptions = subs, deliveries = recent };
    }
}
