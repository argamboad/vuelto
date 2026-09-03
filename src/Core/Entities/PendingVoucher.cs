using Vuelto.Core.Vouchers;

namespace Vuelto.Core.Entities;

/// <summary>
/// A parsed voucher staged for review (EMAIL-4). <b>Inert</b> — it creates no budget data (no month, no
/// transaction, nothing on the dashboard) until the user confirms it (EMAIL-6) or discards it. Household-
/// scoped (ADR-V002/V010): routed to the connection owner's <i>current</i> household at staging time; the
/// user-keyed <see cref="EmailConnectionId"/> is a soft reference across that axis (no FK).
/// </summary>
public class PendingVoucher : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid EmailConnectionId { get; set; }

    /// <summary>The provider message id this was parsed from (diagnostics, not dedup).</summary>
    public required string ProviderMessageId { get; set; }

    /// <summary>Household-scoped dedup key — see <see cref="VoucherFingerprint"/>.</summary>
    public required string Fingerprint { get; set; }

    // --- parsed fields (EMAIL-1) ---
    public required string ParsedBank { get; set; }   // VoucherBank name: "Bac" | "BN" | "Unknown"
    public Guid? BankId { get; set; }                  // resolved against the household catalog, Cash fallback
    public string? Merchant { get; set; }
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public DateOnly? Date { get; set; }
    public string? CardNumber { get; set; }
    public string? Authorization { get; set; }
    public string? Reference { get; set; }
    public string? TransactionType { get; set; }
    public string[] MissingFields { get; set; } = [];

    // --- suggestions (EMAIL-5; staging tolerates "no suggestion") ---
    public Guid? SuggestedCategoryId { get; set; }
    public string? SuggestedClass { get; set; }

    // --- lifecycle ---
    public string Status { get; set; } = PendingVoucherStatuses.Pending;

    /// <summary>Set when confirmed into a transaction (EMAIL-6).</summary>
    public Guid? ConfirmedTransactionId { get; set; }

    public DateTimeOffset? ReceivedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
