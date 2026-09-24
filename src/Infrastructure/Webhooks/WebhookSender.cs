using System.Runtime.ExceptionServices;
using System.Text;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Webhooks;

namespace Vuelto.Infrastructure.Webhooks;

/// <summary>
/// Performs a single signed webhook HTTP POST (HOOKS, ADR-016) and returns the endpoint's status code.
/// Shared by the async outbox handler (which throws on a non-2xx to trigger the outbox's retry/backoff)
/// and the synchronous "send test" endpoint (which surfaces the status to the owner). Signs the raw body
/// with HMAC-SHA256 and stamps the id/event/signature headers.
/// </summary>
public interface IWebhookSender
{
    /// <summary>POSTs <paramref name="body"/> to <paramref name="url"/>, signed with <paramref name="secret"/>; returns the
    /// HTTP status code — a redirect included, never followed. Throws <see cref="WebhookUrlRefusedException"/> when the
    /// SSRF guard refuses the URL.</summary>
    Task<int> SendAsync(string url, string secret, string eventType, string eventId, string body, CancellationToken cancellationToken = default);
}

public sealed class WebhookSender(HttpClient httpClient, IOutboundUrlGuard urlGuard) : IWebhookSender
{
    public async Task<int> SendAsync(string url, string secret, string eventType, string eventId, string body, CancellationToken cancellationToken = default)
    {
        // Re-check at send time so DNS rebinding can't point a previously-valid URL at an internal host (GAP-2).
        if (!await urlGuard.IsAllowedAsync(url, cancellationToken))
            throw new WebhookUrlRefusedException("url_refused: the URL is not one webhooks may reach.");

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(WebhookSignature.IdHeaderName, eventId);
        request.Headers.TryAddWithoutValidation(WebhookSignature.EventHeaderName, eventType);
        request.Headers.TryAddWithoutValidation(WebhookSignature.HeaderName, WebhookSignature.Compute(secret, body));

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            return (int)response.StatusCode;
        }
        catch (HttpRequestException ex) when (ex.InnerException is WebhookUrlRefusedException refused)
        {
            ExceptionDispatchInfo.Throw(refused); // refused at connect time (WebhookHttp) — the same refusal
            throw;
        }
    }

    /// <summary>What a failed delivery records as its error (never a secret, safe to show the tenant): the transport
    /// error or refusal when there was one, else the status — and for a 3xx, why it wasn't followed (decision #7).</summary>
    public static string DescribeFailure(int? status, string? transportError) =>
        transportError
        ?? (status is >= 300 and < 400
            ? $"HTTP {status}: redirects are not followed; register the endpoint's final URL"
            : $"HTTP {status}");
}
