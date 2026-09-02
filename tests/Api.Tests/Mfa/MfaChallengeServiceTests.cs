using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Vuelto.Api.Services;

namespace Vuelto.Api.Tests.Mfa;

/// <summary>
/// MFA-2 (ADR-012): the login challenge binds user + provider + native, signed and time-limited, and
/// fails closed for tampered/malformed/foreign-signer tokens. It is also <b>single-use</b> (v2 audit
/// LOGIC-S1): a challenge id consumes exactly once.
/// </summary>
public class MfaChallengeServiceTests
{
    private static MfaChallengeService NewService() =>
        new(new EphemeralDataProtectionProvider(), new MemoryCache(new MemoryCacheOptions()));

    [Theory]
    [InlineData("google", true)]
    [InlineData("otp", false)]
    public void MintThenRead_RoundTrips(string provider, bool native)
    {
        var service = NewService();
        var userId = Guid.NewGuid();

        var challenge = service.Mint(userId, provider, native);

        Assert.True(service.TryRead(challenge, out var readUser, out var readProvider, out var readNative, out var id));
        Assert.Equal(userId, readUser);
        Assert.Equal(provider, readProvider);
        Assert.Equal(native, readNative);
        Assert.NotEmpty(id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    public void Malformed_IsRejected(string token) =>
        Assert.False(NewService().TryRead(token, out _, out _, out _, out _));

    [Fact]
    public void Tampered_IsRejected()
    {
        var service = NewService();
        var challenge = service.Mint(Guid.NewGuid(), "otp", false);
        Assert.False(service.TryRead("AA" + challenge[2..], out _, out _, out _, out _));
    }

    [Fact]
    public void ForeignSigner_IsRejected()
    {
        var challenge = NewService().Mint(Guid.NewGuid(), "otp", false);
        Assert.False(NewService().TryRead(challenge, out _, out _, out _, out _)); // different ephemeral keys
    }

    [Fact]
    public void Consume_Once_ThenRejectsReplay()
    {
        var service = NewService();
        var challenge = service.Mint(Guid.NewGuid(), "otp", false);
        Assert.True(service.TryRead(challenge, out _, out _, out _, out var id));

        Assert.True(service.Consume(id));   // first redemption
        Assert.False(service.Consume(id));  // replay refused
    }

    [Fact]
    public void Consume_UnknownId_ReturnsFalse() =>
        Assert.False(NewService().Consume(Guid.NewGuid().ToString("N")));
}
