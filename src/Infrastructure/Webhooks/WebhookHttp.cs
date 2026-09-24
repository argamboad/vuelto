using System.Net.Sockets;
using Vuelto.Core.Abstractions;

namespace Vuelto.Infrastructure.Webhooks;

/// <summary>
/// The webhook client's transport (v4 audit H8, decision #7). A webhook is a signed POST to a URL a tenant typed,
/// so the client must reach exactly the host the SSRF guard approved and nothing else:
/// <list type="bullet">
/// <item><b>No redirects.</b> Following one turned a 301/302/303 into a body-less GET to a host nobody checked —
/// whose 200 was then recorded as a delivered event — and a 307 re-sent the signed body to it. A 3xx is returned
/// as the status, and the delivery records it as a failure that says why.</item>
/// <item><b>Connect to what was checked.</b> The guard resolved the name once and the socket resolved it again, so a
/// name could answer "public" to the guard and "loopback" to the socket. The connection now asks the guard for the
/// addresses it may dial and dials only those; an empty answer is a <see cref="WebhookUrlRefusedException"/>.</item>
/// <item><b>No proxy.</b> A proxy would resolve the host itself, past the connect-time check.</item>
/// </list>
/// </summary>
public static class WebhookHttp
{
    public static SocketsHttpHandler CreatePrimaryHandler(IOutboundUrlGuard guard) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = await guard.ResolveAllowedAsync(host, cancellationToken);
            if (addresses.Length == 0)
                throw new WebhookUrlRefusedException($"url_refused: {host} does not resolve to an address webhooks may reach.");

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}

/// <summary>The SSRF guard refused the webhook's URL — when it was checked, or when the connection was made. A
/// refusal won't change on retry, so the delivery dead-letters at once (<see cref="OutboxPermanentFailureException"/>).
/// The message starts with <c>url_refused</c>, the coded reason the delivery log shows.</summary>
public sealed class WebhookUrlRefusedException(string message) : Exception(message);
