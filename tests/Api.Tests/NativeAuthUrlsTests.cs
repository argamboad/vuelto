using Perezosoft.Api.Services;

namespace Perezosoft.Api.Tests;

/// <summary>
/// The native OAuth flow round-trips a client-generated CSRF <c>state</c> nonce (v3 NAT-9): the loopback
/// listener would otherwise accept ANY <c>?code=</c> that hits its port, so a local process or a malicious
/// page could inject an attacker's code and sign the user into the attacker's account. These pin that the
/// state survives the provider round-trip (login → callback) and is echoed back for the client to validate,
/// while staying back-compatible with a client that sends none.
/// </summary>
public class NativeAuthUrlsTests
{
    [Fact]
    public void Callback_CarriesStateThroughTheProviderRoundTrip()
    {
        var url = NativeAuthUrls.Callback("google", "http://127.0.0.1:5000/", linkToken: null, state: "abc123");

        Assert.Contains("redirect=", url);
        Assert.Contains("state=abc123", url);
    }

    [Fact]
    public void Callback_ThreadsLinkTokenAndStateTogether()
    {
        var url = NativeAuthUrls.Callback("google", "http://127.0.0.1:5000/", linkToken: "lt", state: "abc123");

        Assert.Contains("link_token=lt", url);
        Assert.Contains("state=abc123", url);
    }

    [Fact]
    public void Callback_OmitsStateWhenAbsent()
    {
        var url = NativeAuthUrls.Callback("google", "http://127.0.0.1:5000/", linkToken: null, state: null);

        Assert.DoesNotContain("state=", url);
    }

    [Fact]
    public void ClientRedirect_EchoesStateSoTheClientCanValidateIt()
    {
        var url = NativeAuthUrls.ClientRedirect("http://127.0.0.1:5000/", "code", "the-code", state: "abc123");

        Assert.Contains("code=the-code", url);
        Assert.Contains("state=abc123", url);
    }

    [Fact]
    public void ClientRedirect_EchoesStateOnErrorOutcomesToo()
    {
        var url = NativeAuthUrls.ClientRedirect("http://127.0.0.1:5000/", "error", "auth_failed", state: "abc123");

        Assert.Contains("error=auth_failed", url);
        Assert.Contains("state=abc123", url);
    }

    [Fact]
    public void ClientRedirect_OmitsStateWhenAbsent_BackCompatWithOldClients()
    {
        // A client that sends no state (a build predating NAT-9) gets none echoed — no breakage.
        var url = NativeAuthUrls.ClientRedirect("http://127.0.0.1:5000/", "code", "the-code", state: null);

        Assert.Contains("code=the-code", url);
        Assert.DoesNotContain("state=", url);
    }

    [Fact]
    public void ClientRedirect_EscapesTheStateValue()
    {
        var url = NativeAuthUrls.ClientRedirect("http://127.0.0.1:5000/", "code", "c", state: "a b&c");

        Assert.DoesNotContain("a b&c", url);        // raw, unescaped value must not appear
        Assert.Contains("state=a%20b%26c", url);    // escaped
    }
}
