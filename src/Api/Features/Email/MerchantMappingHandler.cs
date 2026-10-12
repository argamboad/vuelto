using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;
using Vuelto.Core.Vouchers;

namespace Vuelto.Api.Features.Email;

/// <summary>
/// EMAIL-5: the household's merchant → category rules (donor US-029). They only ever <b>suggest</b> on a
/// staged draft — the user always confirms (EMAIL-6). Validation: a non-blank pattern, a class from
/// <see cref="SuggestibleClasses"/> (or none), a category that exists and is active in this household.
/// One rule per merchant text per household regardless of casing: a pre-check answers <c>mapping_exists</c>
/// and the unique index catches the concurrent race (same 409). Plain delete — a rule is copied onto the
/// draft at staging, never referenced afterwards. Reads are tenant-filtered, so a foreign id is a 404.
/// </summary>
public sealed class MerchantMappingHandler(
    IRepository<MerchantCategoryMapping> mappings,
    IRepository<Category> categories,
    ICurrentTenant tenant,
    TimeProvider clock,
    ILogger<MerchantMappingHandler> logger)
{
    public async Task<IReadOnlyList<MerchantMappingResponse>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await mappings.Query().OrderBy(m => m.PatternKey).ToListAsync(cancellationToken);
        var names = await categories.Query().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken); // all states — an inactive name still labels the rule
        return rows.Select(m => MerchantMappingResponse.From(m, names.GetValueOrDefault(m.CategoryId))).ToList();
    }

    public async Task<(MerchantMappingResponse? Mapping, ErrorResponse? Error)> CreateAsync(CreateMerchantMappingRequest r, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId) return (null, NoTenant());
        var (valid, error) = await ValidateAsync(r.MerchantPattern, r.CategoryId, r.SuggestedClass, existingId: null, cancellationToken);
        if (error is not null) return (null, error);

        var now = clock.GetUtcNow();
        var mapping = new MerchantCategoryMapping
        {
            TenantId = tenantId, MerchantPattern = valid!.Pattern, PatternKey = MerchantCategoryMapping.KeyFor(valid.Pattern),
            CategoryId = valid.CategoryId, SuggestedClass = valid.Class, CreatedAt = now, UpdatedAt = now,
        };
        await mappings.AddAsync(mapping, cancellationToken);
        try
        {
            await mappings.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            mappings.Remove(mapping); // detach the Added row so the shared context stays clean
            return (null, Exists(valid.Pattern)); // lost the unique (TenantId, PatternKey) race — same outcome as the pre-check
        }
        logger.LogInformation("Merchant rule '{Pattern}' created", mapping.MerchantPattern);
        return (MerchantMappingResponse.From(mapping, await NameAsync(mapping.CategoryId, cancellationToken)), null);
    }

    public async Task<(MerchantMappingResponse? Mapping, ErrorResponse? Error)> UpdateAsync(Guid id, UpdateMerchantMappingRequest r, CancellationToken cancellationToken)
    {
        var mapping = await mappings.Query().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (mapping is null) return (null, NotFound());
        var (valid, error) = await ValidateAsync(r.MerchantPattern, r.CategoryId, r.SuggestedClass, existingId: id, cancellationToken);
        if (error is not null) return (null, error);

        mapping.MerchantPattern = valid!.Pattern; mapping.PatternKey = MerchantCategoryMapping.KeyFor(valid.Pattern);
        mapping.CategoryId = valid.CategoryId; mapping.SuggestedClass = valid.Class; mapping.UpdatedAt = clock.GetUtcNow();
        mappings.Update(mapping);
        try
        {
            await mappings.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return (null, Exists(valid.Pattern));
        }
        return (MerchantMappingResponse.From(mapping, await NameAsync(mapping.CategoryId, cancellationToken)), null);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var mapping = await mappings.Query().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (mapping is null) return false;
        mappings.Remove(mapping);
        await mappings.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Learn-on-confirm (EMAIL-6, donor US-029 AC4): remember this merchant → category (+ class) unless a rule for
    /// the exact merchant text already exists — never overwrites. Returns whether a rule was created.
    /// </summary>
    public async Task<bool> RememberAsync(string? merchant, Guid categoryId, string? transactionClass, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(merchant)) return false;
        var (created, _) = await CreateAsync(new CreateMerchantMappingRequest(merchant.Trim(), categoryId, transactionClass), cancellationToken);
        return created is not null;
    }

    private sealed record ValidRule(string Pattern, Guid CategoryId, string? Class);

    private async Task<(ValidRule? Valid, ErrorResponse? Error)> ValidateAsync(string? pattern, Guid? categoryId, string? suggestedClass, Guid? existingId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return (null, Invalid("merchant_pattern is required"));
        var trimmed = pattern.Trim();
        if (trimmed.Length > 200) return (null, Invalid("merchant_pattern must be 200 characters or fewer"));
        if (!SuggestibleClasses.TryNormalize(suggestedClass, out var cls))
            return (null, Invalid($"suggested_class must be one of: {string.Join(", ", SuggestibleClasses.All)}"));
        if (categoryId is not { } category) return (null, Invalid("category_id is required"));
        if (!await categories.Query().AnyAsync(c => c.Id == category && c.IsActive, cancellationToken)) return (null, Invalid("unknown or inactive category"));

        var key = MerchantCategoryMapping.KeyFor(trimmed);
        var clash = await mappings.Query().FirstOrDefaultAsync(m => m.PatternKey == key, cancellationToken);
        if (clash is not null && clash.Id != existingId) return (null, Exists(clash.MerchantPattern));
        return (new ValidRule(trimmed, category, cls), null);
    }

    private Task<string?> NameAsync(Guid categoryId, CancellationToken cancellationToken) =>
        categories.Query().Where(c => c.Id == categoryId).Select(c => c.Name).FirstOrDefaultAsync(cancellationToken);

    private static ErrorResponse Invalid(string message) => new("invalid_request", message);
    private static ErrorResponse Exists(string pattern) => new("mapping_exists", $"A rule for '{pattern}' already exists");
    private static ErrorResponse NotFound() => new("not_found", "merchant mapping not found");
    private static ErrorResponse NoTenant() => new("invalid_token", "No household on the token");
}
