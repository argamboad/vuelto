using Vuelto.Core.Vouchers;

namespace Vuelto.Core.Tests.Vouchers;

/// <summary>EMAIL-4 (donor US-028 D2 + US-034 AC1): the dedup key is stable for the same transaction, distinct across transactions, and never collapses misparses.</summary>
public class VoucherFingerprintTests
{
    private static ParsedVoucher V(VoucherBank bank = VoucherBank.Bac, string? auth = "595142", string? reference = "616420435873", decimal? amount = 83.99m, DateOnly? date = null) =>
        new() { Bank = bank, Authorization = auth, Reference = reference, Amount = amount, Date = date ?? new DateOnly(2026, 6, 13) };

    [Fact]
    public void SameTransaction_YieldsTheSameFingerprint() => Assert.Equal(VoucherFingerprint.Compute(V()), VoucherFingerprint.Compute(V()));

    [Fact]
    public void DifferentAmountBankOrDate_ChangesTheFingerprint()
    {
        Assert.NotEqual(VoucherFingerprint.Compute(V(amount: 83.99m)), VoucherFingerprint.Compute(V(amount: 84.00m)));
        Assert.NotEqual(VoucherFingerprint.Compute(V(bank: VoucherBank.Bac)), VoucherFingerprint.Compute(V(bank: VoucherBank.BN)));
        Assert.NotEqual(VoucherFingerprint.Compute(V(date: new DateOnly(2026, 6, 13))), VoucherFingerprint.Compute(V(date: new DateOnly(2026, 6, 14))));
    }

    [Fact]
    public void Authorization_IsPreferredOverReference()
    {
        Assert.Equal(VoucherFingerprint.Compute(V(reference: "A")), VoucherFingerprint.Compute(V(reference: "B")));
        Assert.NotEqual(VoucherFingerprint.Compute(V(auth: null, reference: "A")), VoucherFingerprint.Compute(V(auth: null, reference: "B")));
    }

    [Fact]
    public void Fingerprint_IsFixedWidthHex()
    {
        var fp = VoucherFingerprint.Compute(V())!;
        Assert.Equal(64, fp.Length);
        Assert.Matches("^[0-9A-F]+$", fp);
    }

    [Fact]
    public void BothIdsBlank_FallsBackToTheMessageId_OrNullWithoutOne()
    {
        var v = V(auth: null, reference: " ");
        Assert.Null(VoucherFingerprint.Compute(v, messageId: null));
        var fp1 = VoucherFingerprint.Compute(v, messageId: "AAA-111");
        var fp2 = VoucherFingerprint.Compute(v, messageId: "BBB-222");
        Assert.NotNull(fp1); Assert.NotNull(fp2);
        Assert.NotEqual(fp1, fp2);
        Assert.Equal(fp1, VoucherFingerprint.Compute(v, messageId: "AAA-111"));
    }

    [Fact]
    public void WithAnAuthorization_TheMessageIdIsIgnored() =>
        Assert.Equal(VoucherFingerprint.Compute(V(), messageId: "msg-A"), VoucherFingerprint.Compute(V(), messageId: "msg-B"));
}
