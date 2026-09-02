using Microsoft.AspNetCore.DataProtection;
using Perezosoft.Infrastructure.Files;

namespace Perezosoft.Api.Tests.Files;

/// <summary>
/// FILES-2 (ADR-010): the download token binds tenant + key + expiry and fails closed for expired,
/// tampered, malformed, or foreign-key tokens. Uses an ephemeral Data Protection provider per
/// tokenizer so "different signer can't read it" is exercised.
/// </summary>
public class FileDownloadTokenizerTests
{
    private static FileDownloadTokenizer NewTokenizer() =>
        new(new EphemeralDataProtectionProvider());

    [Fact]
    public void MintThenRead_RoundTripsTenantAndKey()
    {
        var tokenizer = NewTokenizer();
        var tenant = Guid.NewGuid();

        var token = tokenizer.Mint(tenant, "avatars/me.png", TimeSpan.FromMinutes(5));

        Assert.True(tokenizer.TryRead(token, out var readTenant, out var readKey));
        Assert.Equal(tenant, readTenant);
        Assert.Equal("avatars/me.png", readKey);
    }

    [Fact]
    public void ExpiredToken_IsRejected()
    {
        var tokenizer = NewTokenizer();
        var token = tokenizer.Mint(Guid.NewGuid(), "k", TimeSpan.FromSeconds(-1));
        Assert.False(tokenizer.TryRead(token, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-real-token")]
    public void MalformedToken_IsRejected(string token)
    {
        Assert.False(NewTokenizer().TryRead(token, out _, out _));
    }

    [Fact]
    public void TamperedToken_IsRejected()
    {
        var tokenizer = NewTokenizer();
        var token = tokenizer.Mint(Guid.NewGuid(), "k", TimeSpan.FromMinutes(5));
        var tampered = "AA" + token[2..]; // corrupt the leading bytes
        Assert.False(tokenizer.TryRead(tampered, out _, out _));
    }

    [Fact]
    public void TokenFromAnotherSigner_IsRejected()
    {
        var token = NewTokenizer().Mint(Guid.NewGuid(), "k", TimeSpan.FromMinutes(5));
        Assert.False(NewTokenizer().TryRead(token, out _, out _)); // different ephemeral keys
    }
}
