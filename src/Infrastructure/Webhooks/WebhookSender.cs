using System.Text;
using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Webhooks;

namespace Perezosoft.Infrastructure.Webhooks;

/// <summary>
/// Performs a single signed webhook HTTP POST (HOOKS, ADR-016) and returns the endpoint's status code.
/// Shared by the async outbox handler (which throws on a non-2xx to trigger the outbox's retry/backoff)
/// and the synchronous "send test" endpoint (which surfaces the status to the owner). Signs the raw body
/// with HMAC-SHA256 and stamps the id/event/signature headers.
/// </summary>
public interface IWebhookSender
{
    /// <summary>POSTs <paramref name="body"/> to <paramref name="url"/>, signed with <paramref name="secret"/>; returns the HTTP status code.</summary>
    Task<int> SendAsync(string url, string secret, string eventType, string eventId, string body, CancellationToken cancellationToken = default);
}

public sealed class WebhookSender(HttpClient httpClient, IOutboundUrlGuard urlGuard) : IWebhookSender
{
    public async Task<int> SendAsync(string url, string secret, string eventType, string eventId, string body, CancellationToken cancellationToken = default)
    {
        // Re-check at send time so DNS rebinding can't point a previously-valid URL at an internal host (GAP-2).
        if (!await urlGuard.IsAllowedAsync(url, cancellationToken))
            throw new InvalidOperationException("Refusing to send a webhook to a disallowed URL.");

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(WebhookSignature.IdHeaderName, eventId);
        request.Headers.TryAddWithoutValidation(WebhookSignature.EventHeaderName, eventType);
        request.Headers.TryAddWithoutValidation(WebhookSignature.HeaderName, WebhookSignature.Compute(secret, body));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        return (int)response.StatusCode;
    }
}
