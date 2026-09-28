using System.Net.Sockets;
using System.Security.Authentication;

namespace Vuelto.Infrastructure.Webhooks;

/// <summary>
/// What a failed webhook delivery records in <c>WebhookDelivery.Error</c> — and what the household owner
/// reads back from <c>GET /api/webhooks/{id}/deliveries</c> (v4 T39, JOBS-1/5/6, R89/R96): a fixed reason
/// code from <see cref="Reasons"/>, never the exception's text. The raw message names resolved IPs and
/// ports, DNS errors and the SSRF guard's verdict about the tenant's host; it goes to the server log only.
/// </summary>
public static class WebhookFailure
{
    public const string UrlRefused = "url_refused";
    public const string Timeout = "timeout";
    public const string Dns = "dns";
    public const string Tls = "tls";
    public const string Network = "network";
    public const string Error = "error";
    /// <summary>A 3xx answer: redirects are never followed (v4 H1) — the endpoint's final URL must be registered.</summary>
    public const string RedirectNotFollowed = "redirect_not_followed";
    public const string HttpPrefix = "http_";

    /// <summary>Every reason a delivery can carry, besides <c>http_{status}</c> for a non-2xx, non-3xx answer.</summary>
    public static readonly IReadOnlySet<string> Reasons = new HashSet<string> { UrlRefused, Timeout, Dns, Tls, Network, Error, RedirectNotFollowed };

    /// <summary>The reason for a delivery that got HTTP <paramref name="status"/>, or threw <paramref name="exception"/>.</summary>
    public static string Reason(int? status, Exception? exception)
    {
        if (exception is null)
            return status is >= 300 and < 400 ? RedirectNotFollowed : $"{HttpPrefix}{status ?? 0}";
        return exception switch
        {
            WebhookUrlRefusedException => UrlRefused,
            // HttpClient's own timeout surfaces as a TaskCanceledException whose token was NOT the caller's;
            // a caller's cancellation never gets here (the write sites rethrow it).
            OperationCanceledException => Timeout,
            _ when Has<SocketException>(exception, s => s.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain) => Dns,
            _ when Has<AuthenticationException>(exception, _ => true) => Tls,
            HttpRequestException => Network,
            _ => Error,
        };
    }

    /// <summary>True when the reason is one of <see cref="Reasons"/> or an <c>http_NNN</c> code.</summary>
    public static bool IsReason(string? value) =>
        value is not null && (Reasons.Contains(value) || (value.StartsWith(HttpPrefix, StringComparison.Ordinal) && value.Length <= 8 && int.TryParse(value.AsSpan(HttpPrefix.Length), out _)));

    private static bool Has<T>(Exception e, Func<T, bool> test) where T : Exception
    {
        for (var current = e; current is not null; current = current.InnerException)
            if (current is T t && test(t)) return true;
        return false;
    }
}
