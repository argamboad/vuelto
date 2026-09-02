using Microsoft.Extensions.Caching.Memory;
using Perezosoft.Api.Services;

namespace Perezosoft.Api.Tests;

/// <summary>
/// The single-use guarantee for cache-backed tokens, tested once on the shared
/// <see cref="SingleUseCacheToken{T}"/> primitive that both <c>LinkTokenService</c> and
/// <c>NativeAuthCodeService</c> delegate to (CONF-10 / B4-3).
/// </summary>
public class SingleUseCacheTokenTests
{
    private static SingleUseCacheToken<Guid> NewStore() =>
        new(new MemoryCache(new MemoryCacheOptions()), "test", TimeSpan.FromMinutes(5));

    [Fact]
    public void Consume_ReturnsValueOnce_ThenRejectsReplay()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        var token = store.Issue(id);

        Assert.True(store.TryConsume(token, out var first));
        Assert.Equal(id, first);

        Assert.False(store.TryConsume(token, out _)); // single-use: the entry is gone
    }

    [Theory]
    [InlineData("unknown-token")]
    [InlineData("")]
    [InlineData("   ")]
    public void Consume_BlankOrUnknown_ReturnsFalse(string token)
    {
        Assert.False(NewStore().TryConsume(token, out _));
    }

    [Fact]
    public void Issue_ProducesDistinctTokens()
    {
        var store = NewStore();
        Assert.NotEqual(store.Issue(Guid.NewGuid()), store.Issue(Guid.NewGuid()));
    }
}
