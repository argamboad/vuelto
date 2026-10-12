using Microsoft.Extensions.Caching.Memory;
using Vuelto.Api.Services;

namespace Vuelto.Api.Tests;

/// <summary>
/// The native OAuth flow can't return tokens directly in the loopback redirect URL
/// (they'd leak via browser history/logs), so the callback mints a short-lived,
/// single-use code that the app exchanges for tokens. These tests pin that contract.
/// </summary>
public class NativeAuthCodeServiceTests
{
    private static NativeAuthCodeService CreateService() =>
        new(new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public void Redeem_ReturnsGrant_ForFreshCode()
    {
        var service = CreateService();
        var userId = Guid.NewGuid();

        var code = service.Issue(userId, "google");
        var grant = service.Redeem(code);

        Assert.NotNull(grant);
        Assert.Equal(userId, grant!.Value.UserId);
        Assert.Equal("google", grant.Value.Provider);
    }

    [Fact]
    public void Redeem_IsSingleUse()
    {
        var service = CreateService();
        var code = service.Issue(Guid.NewGuid(), "microsoft");

        Assert.NotNull(service.Redeem(code));   // first use succeeds
        Assert.Null(service.Redeem(code));      // replay is rejected
    }

    [Fact]
    public void Redeem_ReturnsNull_ForUnknownCode()
    {
        var service = CreateService();
        Assert.Null(service.Redeem("not-a-real-code"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Redeem_ReturnsNull_ForBlankCode(string? code)
    {
        var service = CreateService();
        Assert.Null(service.Redeem(code!));
    }

    [Fact]
    public void Issue_ProducesDistinctCodes()
    {
        var service = CreateService();
        var a = service.Issue(Guid.NewGuid(), "google");
        var b = service.Issue(Guid.NewGuid(), "google");
        Assert.NotEqual(a, b);
    }
}
