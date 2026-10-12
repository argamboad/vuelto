using Vuelto.Core.Entities;

namespace Vuelto.Core.Tests;

/// <summary>
/// Boundary tests for <see cref="LoginToken"/>'s derived rules, evaluated against an explicit
/// instant so they're deterministic. Expiry is inclusive (<c>now &gt;= ExpiresAt</c>): a token is
/// expired exactly at its expiry.
/// </summary>
public class LoginTokenTests
{
    private static readonly DateTimeOffset Expiry = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private static LoginToken Token(DateTimeOffset expiresAt, DateTimeOffset? consumedAt = null) => new()
    {
        Email = "u@example.com",
        CodeHash = "h",
        Purpose = LoginTokenPurpose.Otp,
        ExpiresAt = expiresAt,
        ConsumedAt = consumedAt,
    };

    [Fact]
    public void IsExpiredAt_IsInclusiveOfTheBoundary()
    {
        var token = Token(Expiry);
        Assert.False(token.IsExpiredAt(Expiry.AddTicks(-1))); // just before
        Assert.True(token.IsExpiredAt(Expiry));               // exactly at expiry → expired
        Assert.True(token.IsExpiredAt(Expiry.AddTicks(1)));   // just after
    }

    [Fact]
    public void IsConsumed_TracksConsumedAt()
    {
        Assert.False(Token(Expiry).IsConsumed);
        Assert.True(Token(Expiry, consumedAt: Expiry.AddMinutes(-1)).IsConsumed);
    }

    [Fact]
    public void IsValidAt_RequiresUnconsumedAndUnexpired()
    {
        var before = Expiry.AddMinutes(-1);

        Assert.True(Token(Expiry).IsValidAt(before));                                  // unconsumed + unexpired
        Assert.False(Token(Expiry).IsValidAt(Expiry));                                 // expired
        Assert.False(Token(Expiry, consumedAt: before).IsValidAt(before));             // consumed
        Assert.False(Token(Expiry, consumedAt: before).IsValidAt(Expiry));             // consumed AND expired
    }
}
