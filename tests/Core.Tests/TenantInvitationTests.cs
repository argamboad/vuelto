using Vuelto.Core.Entities;

namespace Vuelto.Core.Tests;

/// <summary>
/// Boundary tests for <see cref="TenantInvitation"/>'s ONE validity rule (v4 T34, R127), evaluated against an
/// explicit instant: valid = Pending and <c>ExpiresAt &gt; now</c> — at the expiry instant it is already
/// invalid, so the signup gate and the accept can never disagree about the same second. The rule is written
/// once as an expression (so EF translates it for the gate's and the seat count's queries) and run in memory
/// for the accept; a theory below holds the two readings together.
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
    public void IsExpiredAt_IncludesTheBoundary()
    {
        var invite = Invite(InvitationStatuses.Pending, Expiry);
        Assert.False(invite.IsExpiredAt(Expiry.AddTicks(-1))); // just before → not expired
        Assert.True(invite.IsExpiredAt(Expiry));               // exactly at expiry → expired
        Assert.True(invite.IsExpiredAt(Expiry.AddTicks(1)));
    }

    [Fact]
    public void IsValidAt_RequiresPendingAndNotYetExpired()
    {
        Assert.True(Invite(InvitationStatuses.Pending, Expiry).IsValidAt(Expiry.AddMinutes(-1)));
        Assert.False(Invite(InvitationStatuses.Pending, Expiry).IsValidAt(Expiry));            // the instant: invalid
        Assert.False(Invite(InvitationStatuses.Pending, Expiry).IsValidAt(Expiry.AddTicks(1)));
    }

    [Theory]
    [InlineData(InvitationStatuses.Accepted)]
    [InlineData(InvitationStatuses.Revoked)]
    [InlineData(InvitationStatuses.Expired)]
    public void IsValidAt_NonPendingIsNeverValid(string status)
    {
        Assert.False(Invite(status, Expiry).IsValidAt(Expiry.AddMinutes(-1))); // unexpired but not Pending
    }

    [Theory]
    [InlineData(InvitationStatuses.Pending, -60, true)]
    [InlineData(InvitationStatuses.Pending, 0, false)]
    [InlineData(InvitationStatuses.Pending, 60, false)]
    [InlineData(InvitationStatuses.Accepted, -60, false)]
    [InlineData(InvitationStatuses.Revoked, -60, false)]
    public void ValidAt_TheQueryExpression_ReadsTheSameAsTheInMemoryRule(string status, int nowMinusExpirySeconds, bool expected)
    {
        var now = Expiry.AddSeconds(nowMinusExpirySeconds);
        var invite = Invite(status, Expiry);

        Assert.Equal(expected, invite.IsValidAt(now));
        Assert.Equal(expected, TenantInvitation.ValidAt(now).Compile()(invite));
    }
}
