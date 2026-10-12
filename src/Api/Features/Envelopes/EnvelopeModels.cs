using System.Text.Json.Serialization;
using Vuelto.Api.Services;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Features.Envelopes;

// ENV-1 DTOs (ADR-V007/V008). Wire format: snake_case (ADR-V012).

public record CreateEnvelopeRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("annual_target_crc")] decimal AnnualTargetCrc,
    [property: JsonPropertyName("annual_target_usd")] decimal AnnualTargetUsd,
    [property: JsonPropertyName("reminder_cadence")] string? ReminderCadence);

public record UpdateEnvelopeRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("annual_target_crc")] decimal AnnualTargetCrc,
    [property: JsonPropertyName("annual_target_usd")] decimal AnnualTargetUsd,
    [property: JsonPropertyName("reminder_cadence")] string? ReminderCadence,
    [property: JsonPropertyName("is_active")] bool IsActive);

public record EnvelopeResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("annual_target_crc")] decimal AnnualTargetCrc,
    [property: JsonPropertyName("annual_target_usd")] decimal AnnualTargetUsd,
    [property: JsonPropertyName("reminder_cadence")] string ReminderCadence,
    [property: JsonPropertyName("is_active")] bool IsActive)
{
    public static EnvelopeResponse From(Envelope e) =>
        new(e.Id, e.Name, e.AnnualTargetCrc, e.AnnualTargetUsd, e.ReminderCadence, e.IsActive);
}

/// <summary>
/// The 409 body for a name clash — the shared <see cref="ErrorResponse"/> plus, for an
/// <c>envelope_exists_inactive</c> clash, the existing entry's id and stored name so the page can offer
/// one-click reactivation that restores the entry as it was (same contract as the catalogs).
/// </summary>
public record EnvelopeConflictResponse(
    string Error,
    string Message,
    [property: JsonPropertyName("existing_id")] Guid? ExistingId,
    [property: JsonPropertyName("existing_name")] string? ExistingName) : ErrorResponse(Error, Message);
