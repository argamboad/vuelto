namespace Vuelto.Core.Entities;

/// <summary>
/// How a person wants amounts shown — colones, dollars, or both sides of every pair (DISPLAY-1, ADR-V020).
/// <b>User-keyed, not tenant-scoped</b> (like <see cref="EmailConnection"/>, ADR-V002): a display taste belongs to
/// the person and follows them to every device and household, like the platform's theme and language. The
/// platform's <see cref="User"/> row is not the app's to extend, so the preference lives in its own table and is
/// wiped by account erasure through an <c>IUserDataContributor</c>. One row per user; absent = never chose.
/// </summary>
public class UserDisplaySettings
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }

    /// <summary>One of <see cref="Budget.DisplayCurrencies"/>: <c>CRC</c>, <c>USD</c> or <c>both</c>.</summary>
    public string DisplayCurrency { get; set; } = Budget.DisplayCurrencies.Both;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
