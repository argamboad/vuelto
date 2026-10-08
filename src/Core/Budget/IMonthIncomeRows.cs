namespace Vuelto.Core.Budget;

/// <summary>
/// The Ledger slice's face for the month income rows (Arch A8, R162). A month's income rows are written with the
/// month — staged when it is created, edited on it, removed with it — so the Ledger owns them; the Income slice, whose
/// lines the rows were copied from, writes through here. Writes are scoped to the current household.
/// </summary>
public interface IMonthIncomeRows
{
    /// <summary>
    /// The rows copied from <paramref name="incomeLineId"/> that still carry <paramref name="from"/> take <paramref name="to"/>.
    /// Staged, not saved: the caller's save lands them with its own change (one context).
    /// </summary>
    Task ReassignMemberAsync(Guid incomeLineId, Guid? from, Guid? to, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Account erasure: the person's name comes off every row of the current household. Set-based, immediate.</summary>
    Task ClearMemberAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Household dissolution: every row of <paramref name="tenantId"/> goes. Set-based, immediate.</summary>
    Task WipeAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
