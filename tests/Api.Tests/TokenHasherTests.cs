using Vuelto.Api.Services;

namespace Vuelto.Api.Tests;

/// <summary>
/// v3 audit TB-AUTH-8 (T44): direct coverage for <see cref="TokenHasher"/> — the hash every refresh
/// token and login token round-trips through. Verify must be tolerant of hostile/corrupt stored
/// values (return false, never throw) and compare via <c>CryptographicOperations.FixedTimeEquals</c>
/// (constant-time; behaviourally asserted here via the length-mismatch-returns-false contract —
/// timing itself is not unit-testable).
/// </summary>
public class TokenHasherTests
{
    private readonly TokenHasher _sut = new();

    [Fact]
    public void HashToken_IsDeterministic_AndTokenSensitive()
    {
        Assert.Equal(_sut.HashToken("token-a"), _sut.HashToken("token-a"));
        Assert.NotEqual(_sut.HashToken("token-a"), _sut.HashToken("token-b"));
    }

    [Fact]
    public void HashToken_DoesNotStoreThePlaintext()
    {
        var hash = _sut.HashToken("super-secret-raw-token");
        Assert.DoesNotContain("super-secret-raw-token", hash);
    }

    [Fact]
    public void Verify_MatchingToken_ReturnsTrue() =>
        Assert.True(_sut.Verify("raw-token", _sut.HashToken("raw-token")));

    [Fact]
    public void Verify_WrongToken_ReturnsFalse() =>
        Assert.False(_sut.Verify("wrong-token", _sut.HashToken("raw-token")));

    [Theory]
    [InlineData("not-base64!!!")]        // malformed stored hash → false, not FormatException
    [InlineData("")]                     // empty stored hash
    [InlineData("AAAA")]                 // valid base64 but wrong length (3 bytes vs 32) → length-mismatch false
    public void Verify_MalformedOrTruncatedStoredHash_ReturnsFalse_NotThrows(string storedHash) =>
        Assert.False(_sut.Verify("raw-token", storedHash));

    [Fact]
    public void Verify_NullInputs_ReturnFalse_NotThrow()
    {
        Assert.False(_sut.Verify(null!, _sut.HashToken("raw-token")));
        Assert.False(_sut.Verify("raw-token", null!));
    }
}
