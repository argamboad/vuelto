using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;

namespace Vuelto.Api.Tests.Mfa;

/// <summary>
/// v3 audit ADM-4: recovery codes are low-entropy (~49.5 bits), so a leaked hash of a plain unsalted digest
/// is offline-crackable → a second factor. <see cref="RecoveryCodeHasher"/> keys the hash with a server
/// pepper (derived from <c>Jwt:Secret</c> via HKDF), so a DB leak alone is useless. These pin that the
/// pepper is actually applied and is secret-dependent.
/// </summary>
public class RecoveryCodeHasherTests
{
    private static RecoveryCodeHasher Hasher(string secret) =>
        new(new TestJwtSettings { SecretKey = secret });

    [Fact]
    public void Hash_IsDeterministic_ForTheSameCodeAndSecret()
    {
        var h = Hasher("secret-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        Assert.Equal(h.Hash("k7m2q9xr4t"), h.Hash("k7m2q9xr4t")); // deterministic → by-hash lookup works
        Assert.True(h.Verify("k7m2q9xr4t", h.Hash("k7m2q9xr4t")));
    }

    [Fact]
    public void Hash_IsNotAPlainSha256_OfTheCode()
    {
        // The whole point: the stored hash must NOT be a bare digest an attacker can precompute/crack.
        var code = "k7m2q9xr4t";
        var peppered = Hasher("secret-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa").Hash(code);
        Assert.NotEqual(new TokenHasher().HashToken(code), peppered);
    }

    [Fact]
    public void Hash_DiffersByServerSecret_AndDoesNotCrossVerify()
    {
        var code = "k7m2q9xr4t";
        var a = Hasher("secret-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var b = Hasher("secret-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        Assert.NotEqual(a.Hash(code), b.Hash(code));      // different pepper → different hash
        Assert.False(b.Verify(code, a.Hash(code)));       // a hash made under A can't be verified under B
    }

    [Fact]
    public void Verify_MalformedStoredHash_ReturnsFalse_DoesNotThrow()
    {
        Assert.False(Hasher("secret-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa").Verify("code", "not-base64!!"));
    }
}
