using Perezosoft.Core.Entities;

namespace Perezosoft.Core.Tests;

/// <summary>
/// Boundary tests for <see cref="TenantInvitation"/>'s derived rules, evaluated against an explicit
/// instant. Expiry is exclusive (<c>now &gt; ExpiresAt</c>): an invite is still valid exactly at its
/// expiry, and validity also requires the status to be Pending.
/// </summary>
public class TenantInvitationTests
{
    private static readonly DateTimeOffset Expiry = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private static TenantInvitation Invite(string status, DateTimeOffset expiresAt) => new()
    {
        InvitedEmail = "u@example.com",
        TokenHash = "h",
        Status = status,
        ExpiresAt = expiresAt,
    };

    [Fact]
    public void IsExpiredAt_IsExclusiveOfTheBoundary()
    {
        var invite = Invite(InvitationStatuses.Pending, Expiry);
        Assert.False(invite.IsExpiredAt(Expiry));             // exactly at expiry → still NOT expired
        Assert.True(invite.IsExpiredAt(Expiry.AddTicks(1)));  // just after → expired
    }

    [Fact]
    public void IsValidAt_RequiresPendingAndUnexpired()
    {
        var before = Expiry.AddMinutes(-1);

        Assert.True(Invite(InvitationStatuses.Pending, Expiry).IsValidAt(before));
        Assert.True(Invite(InvitationStatuses.Pending, Expiry).IsValidAt(Expiry));        // boundary still valid
        Assert.False(Invite(InvitationStatuses.Pending, Expiry).IsValidAt(Expiry.AddTicks(1))); // expired
    }

    [Theory]
    [InlineData(InvitationStatuses.Accepted)]
    [InlineData(InvitationStatuses.Revoked)]
    [InlineData(InvitationStatuses.Expired)]
    public void IsValidAt_NonPendingIsNeverValid(string status)
    {
        var before = Expiry.AddMinutes(-1);
        Assert.False(Invite(status, Expiry).IsValidAt(before)); // unexpired but not Pending
    }
}
