using Perezosoft.Core.Webhooks;

namespace Perezosoft.Core.Tests;

/// <summary>
/// Boundary tests for the webhook HMAC signature (HOOKS, ADR-016): deterministic for a given secret+body,
/// changes when either changes, and uses the <c>sha256=&lt;hex&gt;</c> wire format so a receiver can verify.
/// </summary>
public class WebhookSignatureTests
{
    [Fact]
    public void Compute_IsDeterministic()
    {
        var a = WebhookSignature.Compute("whsec_x", "{\"hello\":\"world\"}");
        var b = WebhookSignature.Compute("whsec_x", "{\"hello\":\"world\"}");
        Assert.Equal(a, b);
        Assert.StartsWith("sha256=", a);
    }

    [Fact]
    public void Compute_ChangesWithSecret()
    {
        var body = "{\"a\":1}";
        Assert.NotEqual(WebhookSignature.Compute("secret-1", body), WebhookSignature.Compute("secret-2", body));
    }

    [Fact]
    public void Compute_ChangesWithBody()
    {
        const string secret = "whsec_x";
        Assert.NotEqual(WebhookSignature.Compute(secret, "{\"a\":1}"), WebhookSignature.Compute(secret, "{\"a\":2}"));
    }
}
