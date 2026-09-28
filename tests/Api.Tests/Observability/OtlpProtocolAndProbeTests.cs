using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using Vuelto.Api.Observability;

namespace Vuelto.Api.Tests.Observability;

/// <summary>
/// v4 T42 (OBS-2/OBS-3, R95): the exporter's protocol comes from the same configuration value the signal
/// paths do — an operator who sets <c>OTEL_EXPORTER_OTLP_PROTOCOL</c> in appsettings or user-secrets (where the
/// SDK's own environment read never looks) used to get http paths with gRPC on the wire, and a silent 404.
/// And an unreachable collector, which the SDK drops data for silently, produces one Warning in the app log.
/// </summary>
public class OtlpProtocolAndProbeTests
{
    private static ServiceProvider Build(string? endpoint, string? protocol)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenTelemetry:Otlp:Endpoint"] = endpoint,
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = protocol, // configuration only — NOT the process environment
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAppTelemetry(config);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("http/protobuf", OtlpExportProtocol.HttpProtobuf)]
    [InlineData("HTTP/Protobuf ", OtlpExportProtocol.HttpProtobuf)]
    [InlineData("grpc", OtlpExportProtocol.Grpc)]
    public void ExporterProtocol_ResolvesFromConfigurationAlone(string configured, OtlpExportProtocol expected)
    {
        using var sp = Build("https://collector.example/otlp", configured);

        var monitor = sp.GetRequiredService<IOptionsMonitor<OtlpExporterOptions>>();
        foreach (var signal in new[] { "traces", "metrics", "logs" })
        {
            var options = monitor.Get(signal);
            Assert.Equal(expected, options.Protocol);
            if (expected == OtlpExportProtocol.HttpProtobuf)
                Assert.EndsWith("/v1/" + signal, options.Endpoint.AbsolutePath); // the signal path and the protocol agree
            else
                Assert.Equal("/otlp", options.Endpoint.AbsolutePath);
        }
    }

    [Fact]
    public void ProbeIsRegistered_OnlyWithAnEndpoint()
    {
        using var with = Build("https://collector.example/otlp", null);
        using var without = Build(null, null);

        Assert.Contains(with.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), s => s is OtlpCollectorProbe);
        Assert.DoesNotContain(without.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), s => s is OtlpCollectorProbe);
    }

    [Fact]
    public async Task UnreachableCollector_LogsOneWarning_NotOnePerCheck()
    {
        var log = new CapturingLogger<OtlpCollectorProbe>();
        var probe = new OtlpCollectorProbe(new OtlpCollectorProbe.Target("http://127.0.0.1:1"), log); // a port nothing listens on

        Assert.False(await probe.ProbeAsync());
        Assert.False(await probe.ProbeAsync());

        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("http://127.0.0.1:1", warning.Message);
        Assert.Contains("dropped", warning.Message);
    }

    [Fact]
    public async Task ReachableCollector_LogsNoWarning_AndRecoveryIsNoted()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var log = new CapturingLogger<OtlpCollectorProbe>();
        var probe = new OtlpCollectorProbe(new OtlpCollectorProbe.Target($"http://127.0.0.1:{port}"), log);

        Assert.True(await probe.ProbeAsync());
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Warning);

        listener.Stop();
        Assert.False(await probe.ProbeAsync());
        Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
