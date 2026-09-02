using Vuelto.Api.Services;

namespace Vuelto.Api.Tests;

/// <summary>
/// The native OAuth callback redirects to a client-supplied URL, so this guard is the
/// open-redirect defense. These tests pin exactly what's allowed.
/// </summary>
public class NativeRedirectPolicyTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5000/")]
    [InlineData("http://127.0.0.1:53123/")]
    [InlineData("http://localhost:8080/")]
    [InlineData("http://[::1]:9000/")]
    public void Allows_LoopbackHttp(string redirect)
    {
        Assert.True(NativeRedirectPolicy.IsAllowed(redirect, customScheme: null));
    }

    [Theory]
    [InlineData("http://evil.com/")]
    [InlineData("https://evil.com/")]
    [InlineData("http://192.168.1.50:5000/")]
    [InlineData("https://127.0.0.1:5000/")]   // loopback but HTTPS — not allowed
    [InlineData("https://localhost:5000/")]
    public void Rejects_NonLoopbackOrHttps(string redirect)
    {
        Assert.False(NativeRedirectPolicy.IsAllowed(redirect, customScheme: null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not-a-url")]
    [InlineData("/relative/path")]
    public void Rejects_BlankOrMalformed(string? redirect)
    {
        Assert.False(NativeRedirectPolicy.IsAllowed(redirect, customScheme: null));
    }

    [Fact]
    public void Allows_ConfiguredCustomScheme()
    {
        Assert.True(NativeRedirectPolicy.IsAllowed("vuelto://auth", customScheme: "vuelto"));
    }

    [Fact]
    public void CustomScheme_IsCaseInsensitive()
    {
        Assert.True(NativeRedirectPolicy.IsAllowed("Vuelto://auth", customScheme: "vuelto"));
    }

    [Fact]
    public void Rejects_CustomScheme_WhenNoneConfigured()
    {
        Assert.False(NativeRedirectPolicy.IsAllowed("vuelto://auth", customScheme: null));
    }

    [Fact]
    public void Rejects_CustomScheme_Mismatch()
    {
        Assert.False(NativeRedirectPolicy.IsAllowed("other://auth", customScheme: "vuelto"));
    }

    [Fact]
    public void Loopback_StillAllowed_WhenCustomSchemeConfigured()
    {
        Assert.True(NativeRedirectPolicy.IsAllowed("http://127.0.0.1:5000/", customScheme: "vuelto"));
    }
}
