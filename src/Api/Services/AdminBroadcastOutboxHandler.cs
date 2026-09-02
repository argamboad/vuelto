using System.Text.Json;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Services;

/// <summary>The outbox payload for a platform-wide staff broadcast (ADMIN-3): the announcement copy plus
/// the acting staff id, fanned out to every user by <see cref="AdminBroadcastOutboxHandler"/>. The
/// broadcast spans all tenants so it has no in-tenant audit row; this durable message is the attribution
/// record for the largest-blast-radius admin write (v3 audit ADM-6).</summary>
public sealed record AdminBroadcastPayload(string Title, string Body, Guid StaffUserId);

/// <summary>
/// Fans a staff broadcast out to <b>every</b> user across all tenants (ADMIN-3, platform-wide).
/// Enqueued by <c>POST /api/admin/announce-all</c> and processed out-of-band so the HTTP request
/// returns immediately rather than blocking on a large fan-out.
/// <para>
/// <b>Idempotent by construction:</b> the outbox processor runs this handler and marks the message
/// <c>Sent</c> in the <em>same</em> transaction, so the per-user rows commit atomically with that
/// status flip. An at-least-once retry (crash before commit) re-runs from a clean slate rather than
/// double-notifying. Delivery reuses <see cref="INotificationService.NotifyAsync"/>, so it honors each
/// user's channel preferences exactly like the per-tenant announce.
/// </para>
/// <para>
/// <b>Scale note:</b> this stages a row per user in one transaction — fine for the platform's tier.
/// A very large user base should chunk the fan-out (e.g. enqueue per-tenant/per-batch child messages).
/// </para>
/// </summary>
public sealed class AdminBroadcastOutboxHandler(IUserRepository users, INotificationService notifications) : IOutboxHandler
{
    public const string MessageType = "admin.broadcast";
    public string Type => MessageType;

    public async Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Deserialize<AdminBroadcastPayload>(message.Payload)
            ?? throw new InvalidOperationException($"Outbox message {message.Id} has an unreadable admin-broadcast payload.");

        var userIds = await users.GetAllUserIdsAsync(cancellationToken);
        foreach (var userId in userIds)
            await notifications.NotifyAsync(userId, "announcement", payload.Title, payload.Body, metadata: null, cancellationToken);
    }
}
