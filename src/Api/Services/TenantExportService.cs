using System.Text.Json;
using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;

namespace Perezosoft.Api.Services;

/// <summary>
/// Assembles a tenant's data export (GDPR-1, ADR-011): the core tenant data (tenant, members,
/// invitations) plus every <see cref="ITenantDataContributor"/>'s section, into one JSON bundle stored
/// via <see cref="IFileStorage"/> and returned as a signed, time-limited download URL. Owner-gated at
/// the endpoint. <b>Secret-free</b> — invitation token hashes and the like are never included.
/// </summary>
public interface ITenantExportService
{
    /// <summary>Produces the export bundle and returns a signed URL to download it.</summary>
    Task<Uri> ExportAsync(Guid tenantId, Guid actorUserId, CancellationToken cancellationToken = default);
}

public sealed class TenantExportService(
    ITenantRepository tenants,
    ITenantInvitationService invitations,
    IEnumerable<ITenantDataContributor> contributors,
    IFileStorage files,
    IAuditLog audit,
    IRepository<AuditEvent> auditEvents,
    TimeProvider clock) : ITenantExportService
{
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public async Task<Uri> ExportAsync(Guid tenantId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        var tenant = await tenants.GetByIdAsync(tenantId, cancellationToken);
        var members = await tenants.GetMemberDetailsAsync(tenantId, cancellationToken);
        var pending = await invitations.GetPendingAsync(tenantId, cancellationToken);

        var bundle = new Dictionary<string, object?>
        {
            ["exported_at"] = clock.GetUtcNow(),
            ["tenant"] = tenant is null ? null : new { tenant.Id, tenant.Name, tenant.CreatedAt },
            ["members"] = members.Select(m => new { m.UserId, m.Email, m.DisplayName, m.Role, m.JoinedAt }),
            // Token hashes are deliberately omitted — never export secrets (ADR-011).
            ["invitations"] = pending.Select(i => new { i.InvitedEmail, i.Status, i.CreatedAt, i.ExpiresAt }),
        };

        // Each feature contributes its section the same way it contributes to wipe.
        foreach (var contributor in contributors)
            bundle[contributor.ExportKey] = await contributor.ExportAsync(tenantId, cancellationToken);

        var payload = JsonSerializer.SerializeToUtf8Bytes(bundle, Json);
        var key = $"exports/{clock.GetUtcNow():yyyyMMddTHHmmssZ}-{Guid.CreateVersion7():N}.json";
        using (var stream = new MemoryStream(payload))
            await files.PutAsync(key, stream, "application/json", cancellationToken);

        var url = await files.GetDownloadUrlAsync(key, LinkLifetime, cancellationToken);

        await audit.RecordAsync("tenant.exported", actorUserId, nameof(Tenant), tenantId.ToString(),
            new { key }, cancellationToken);
        await auditEvents.SaveChangesAsync(cancellationToken); // flush the staged audit event

        return url;
    }
}
