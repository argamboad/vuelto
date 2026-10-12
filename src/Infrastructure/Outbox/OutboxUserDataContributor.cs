using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Infrastructure.Email;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Infrastructure.Outbox;

/// <summary>
/// Account erasure removes the mail still waiting to go to the erased user (v4 audit H7 / TB-TEN-22, GDPR-2):
/// the payload holds their address, the body and any attachment, and a notification or a platform broadcast is
/// queued with no tenant — so a dissolve would never find it. Finished rows need nothing: their payload is
/// already cleared. Runs in the erasure transaction before the user row goes, which is how it learns the address.
/// <para>
/// Matches the recipient field only. The payload is the <see cref="EmailOutboxPayload"/> JSON, so the address is
/// looked for as <c>"To":"…"</c>, encoded exactly as the serializer encodes it (it escapes <c>+</c>, for one) and
/// compared case-insensitively. Another field that happens to hold the address (a subject) is not the recipient
/// and does not match. No JSON parsing in SQL, so a row that isn't JSON can't fail the erasure.
/// </para>
/// </summary>
public sealed class OutboxUserDataContributor(AppDbContext db) : IUserDataContributor
{
    public async Task WipeAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var email = await db.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrEmpty(email))
            return;

        var recipient = ("\"To\":" + JsonSerializer.Serialize(email)).ToLowerInvariant();
        await db.OutboxMessages
            .Where(m => m.Type == OutboxEmailSender.MessageType && m.Payload.ToLower().Contains(recipient))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
