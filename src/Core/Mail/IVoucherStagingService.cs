using Vuelto.Core.Entities;

namespace Vuelto.Core.Mail;

/// <summary>
/// Reads one connection's filter-matching mail, parses it, and stages each recognized voucher as an inert
/// <c>PendingVoucher</c> in the owner's <i>current</i> household (EMAIL-4) — deduped, creating no budget
/// data. Used by the background poll job and by "Sync now".
/// </summary>
public interface IVoucherStagingService
{
    Task<StagingResult> StageConnectionAsync(EmailConnection connection, CancellationToken cancellationToken = default);
}

/// <summary>Per-connection staging summary (surfaced by "Sync now").</summary>
public record StagingResult(int Staged, int Duplicates, int Unrecognized, bool NeedsReconsent)
{
    public static readonly StagingResult Reconsent = new(0, 0, 0, true);
    public static readonly StagingResult Empty = new(0, 0, 0, false);
}
