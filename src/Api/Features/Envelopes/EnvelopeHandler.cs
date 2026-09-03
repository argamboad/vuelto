using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Envelopes;

/// <summary>
/// ENV-1: the household's envelopes — list, create, update (rename / retarget / recadence / activate /
/// deactivate) with the catalog rules (ADR-V008): case-insensitive uniqueness, the 409 reactivation
/// offer, and uniform 404 for ids the tenant filter hides. Never seeded: targets are personal amounts.
/// </summary>
public sealed class EnvelopeHandler(IRepository<Envelope> envelopes, ICurrentTenant tenant, TimeProvider clock)
{
    public async Task<IReadOnlyList<EnvelopeResponse>?> ListAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is null) return null;

        var query = includeInactive ? envelopes.Query() : envelopes.Query().Where(e => e.IsActive);
        var rows = await query.OrderBy(e => e.Name).ToListAsync(cancellationToken);
        return rows.Select(EnvelopeResponse.From).ToList();
    }

    public async Task<(EnvelopeResponse? Envelope, ErrorResponse? Error)> CreateAsync(CreateEnvelopeRequest request, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId) return (null, NoTenant());
        if (Validate(request.Name, request.ReminderCadence, request.AnnualTargetCrc, request.AnnualTargetUsd) is { } invalid)
            return (null, invalid);

        var name = request.Name!.Trim();
        if (await FindByNameAsync(name, cancellationToken) is { } existing)
            return (null, existing.IsActive
                ? new EnvelopeConflictResponse("envelope_exists", $"An envelope named '{existing.Name}' already exists", null, null)
                : new EnvelopeConflictResponse("envelope_exists_inactive", $"'{existing.Name}' already exists but is inactive — reactivate it?", existing.Id, existing.Name));

        var now = clock.GetUtcNow();
        var envelope = new Envelope
        {
            TenantId = tenantId,
            Name = name,
            AnnualTargetCrc = Money(request.AnnualTargetCrc),
            AnnualTargetUsd = Money(request.AnnualTargetUsd),
            ReminderCadence = EnvelopeReminderCadences.Normalize(request.ReminderCadence)!,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await envelopes.AddAsync(envelope, cancellationToken);
        await envelopes.SaveChangesAsync(cancellationToken);
        return (EnvelopeResponse.From(envelope), null);
    }

    public async Task<(EnvelopeResponse? Envelope, ErrorResponse? Error)> UpdateAsync(Guid id, UpdateEnvelopeRequest request, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is null) return (null, NoTenant());
        if (Validate(request.Name, request.ReminderCadence, request.AnnualTargetCrc, request.AnnualTargetUsd) is { } invalid)
            return (null, invalid);

        // Tenant-scoped lookup: another household's id is not found, never 403 (no existence oracle).
        var envelope = await envelopes.Query().FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (envelope is null) return (null, new ErrorResponse("not_found", "envelope not found"));

        var name = request.Name!.Trim();
        if (await FindByNameAsync(name, cancellationToken) is { } clash && clash.Id != id)
            return (null, new EnvelopeConflictResponse("envelope_exists", $"An envelope named '{clash.Name}' already exists", null, null));

        envelope.Name = name;
        envelope.AnnualTargetCrc = Money(request.AnnualTargetCrc);
        envelope.AnnualTargetUsd = Money(request.AnnualTargetUsd);
        envelope.ReminderCadence = EnvelopeReminderCadences.Normalize(request.ReminderCadence)!;
        envelope.IsActive = request.IsActive;
        envelope.UpdatedAt = clock.GetUtcNow();
        envelopes.Update(envelope);
        await envelopes.SaveChangesAsync(cancellationToken);
        return (EnvelopeResponse.From(envelope), null);
    }

    private static ErrorResponse? Validate(string? name, string? cadence, decimal targetCrc, decimal targetUsd)
    {
        if (string.IsNullOrWhiteSpace(name)) return Invalid("name is required");
        if (name.Trim().Length > 100) return Invalid("name must be 100 characters or fewer");
        if (EnvelopeReminderCadences.Normalize(cadence) is null) return Invalid("reminder_cadence must be monthly or five_week_months");
        if (targetCrc < 0 || targetUsd < 0) return Invalid("annual_target_crc and annual_target_usd cannot be negative");
        return null;
    }

    /// <summary>Targets are money: 2 dp, half away from zero — the same rounding the NUMERIC(12,2) column applies, so the response never disagrees with what was stored.</summary>
    private static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static ErrorResponse Invalid(string message) => new("invalid_request", message);
    private static ErrorResponse NoTenant() => new("invalid_token", "No household on the token");

    /// <summary>Case-insensitive name match within the household (Postgres <c>lower()</c> on both sides).</summary>
    private Task<Envelope?> FindByNameAsync(string name, CancellationToken cancellationToken)
    {
        var lowered = name.ToLowerInvariant();
        return envelopes.Query().FirstOrDefaultAsync(e => e.Name.ToLower() == lowered, cancellationToken);
    }
}
