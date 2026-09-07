using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.DisplaySettings;

/// <summary>
/// DISPLAY-1: read and upsert the caller's single <see cref="UserDisplaySettings"/> row. A read never writes — a
/// user who never chose gets <c>both</c> flagged <c>is_default</c>. Rows are keyed by user, so every path is
/// constrained by the caller's id, never by the household.
/// </summary>
public sealed class DisplaySettingsHandler(IRepository<UserDisplaySettings> settings, TimeProvider clock)
{
    public async Task<DisplaySettingsResponse> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var row = await settings.Query().FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);
        return row is null
            ? new DisplaySettingsResponse(DisplayCurrencies.Both, IsDefault: true)
            : new DisplaySettingsResponse(row.DisplayCurrency, IsDefault: false);
    }

    public async Task<(DisplaySettingsResponse? Settings, ErrorResponse? Error)> UpdateAsync(Guid userId, UpdateDisplaySettingsRequest request, CancellationToken cancellationToken)
    {
        if (DisplayCurrencies.Normalize(request.DisplayCurrency) is not { } value)
            return (null, new ErrorResponse("invalid_request", $"display_currency must be one of: {string.Join(", ", DisplayCurrencies.All)}"));

        var now = clock.GetUtcNow();
        var row = await settings.Query().FirstOrDefaultAsync(s => s.UserId == userId, cancellationToken);
        var created = row is null;
        row ??= new UserDisplaySettings { UserId = userId, CreatedAt = now };
        row.DisplayCurrency = value;
        row.UpdatedAt = now;
        if (created) await settings.AddAsync(row, cancellationToken);
        else settings.Update(row);

        try
        {
            await settings.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (created)
        {
            // Two first saves raced; the unique (UserId) index let exactly one insert win — apply on top of it.
            settings.Remove(row);
            var winner = await settings.Query().FirstAsync(s => s.UserId == userId, cancellationToken);
            winner.DisplayCurrency = value;
            winner.UpdatedAt = now;
            settings.Update(winner);
            await settings.SaveChangesAsync(cancellationToken);
        }

        return (new DisplaySettingsResponse(value, IsDefault: false), null);
    }
}
