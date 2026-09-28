using System.Net.Sockets;

namespace Vuelto.Api.Observability;

/// <summary>
/// Tells the operator when telemetry is going nowhere (v4 T42, OBS-2). The OpenTelemetry SDK drops what it
/// cannot export — silently, with bounded memory, and without ever failing startup — so a collector that is
/// down, misaddressed or firewalled loses every span, metric and log line with no sign in the app's own log.
/// This probe opens a TCP connection to the configured endpoint's host and port at startup and then every
/// <see cref="Interval"/>, and logs ONE Warning when it cannot, and one Information line when it can again.
/// It proves reachability only (a TLS or auth problem still needs the collector's side of the story).
/// </summary>
public sealed class OtlpCollectorProbe(OtlpCollectorProbe.Target target, ILogger<OtlpCollectorProbe> logger, TimeProvider? clock = null) : BackgroundService
{
    /// <summary>The configured collector base URL (<c>OpenTelemetry:Otlp:Endpoint</c>).</summary>
    public sealed record Target(string Endpoint);

    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private bool? _reachable;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProbeAsync(stoppingToken);
            try { await Task.Delay(Interval, clock ?? TimeProvider.System, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One check; logs only on a change of state (the first check counts as a change).</summary>
    public async Task<bool> ProbeAsync(CancellationToken cancellationToken = default)
    {
        string? reason = null;
        var reachable = false;
        try
        {
            var uri = new Uri(target.Endpoint, UriKind.Absolute);
            using var socket = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(uri.Host, uri.Port, timeout.Token);
            reachable = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { reason = ex.Message; }

        if (_reachable != reachable)
        {
            if (reachable)
                logger.LogInformation("Telemetry collector {Endpoint} is reachable", target.Endpoint);
            else
                logger.LogWarning("Telemetry collector {Endpoint} cannot be reached ({Reason}): traces, metrics and logs are being dropped silently until it is", target.Endpoint, reason);
        }
        _reachable = reachable;
        return reachable;
    }
}
