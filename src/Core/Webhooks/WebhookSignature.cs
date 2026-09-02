using System.Security.Cryptography;
using System.Text;

namespace Perezosoft.Core.Webhooks;

/// <summary>
/// Computes the HMAC-SHA256 signature a webhook delivery is signed with (HOOKS, ADR-016). The receiver
/// recomputes it over the raw request body with the shared secret and compares — proving the delivery is
/// authentic and untampered. Pure/deterministic, so it's unit-testable and usable from both the delivery
/// path and any docs/examples. The wire format is <c>sha256=&lt;hex&gt;</c> (like GitHub/Stripe).
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "X-Webhook-Signature";
    public const string EventHeaderName = "X-Webhook-Event";
    public const string IdHeaderName = "X-Webhook-Id";

    /// <summary>The signature header value for <paramref name="body"/> signed with <paramref name="secret"/>.</summary>
    public static string Compute(string secret, string body)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexStringLower(hash);
    }
}
