namespace Vuelto.Core.Vouchers;

/// <summary>Pending-voucher lifecycle states (EMAIL-4/6). Only <c>pending</c> drafts can be confirmed or discarded, and each flip is conditional.</summary>
public static class PendingVoucherStatuses
{
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    public const string Discarded = "discarded";
}
