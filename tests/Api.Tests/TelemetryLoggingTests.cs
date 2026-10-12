using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Vuelto.Api.Tests;

/// <summary>
/// Local Dev Alignment L3. The OpenTelemetry exporters ship logs, traces and metrics over <c>HttpClient</c>, whose
/// handlers log every request at Information ("Start processing HTTP request POST …/otlp/v1/logs", "Received HTTP
/// response headers …"). With the default level at Information, each export wrote four log lines about itself, and
/// those lines were exported in the next batch — so on staging the Grafana log stream was mostly the exporter talking
/// about the exporter, burying the app's own logs and spending the free tier's log quota. The HttpClient category
/// stays at Warning, in every environment: a failed call still logs.
/// </summary>
public class TelemetryLoggingTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void HttpClientRequestLogs_StayOutOfTheTelemetryExport(string environment)
    {
        var api = Path.Combine(RepoRoot(), "src", "Api");
        var config = new ConfigurationBuilder()
            .SetBasePath(api)
            .AddJsonFile("appsettings.json")
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .Build();

        var configured = config["Logging:LogLevel:System.Net.Http.HttpClient"];
        Assert.True(Enum.TryParse<LogLevel>(configured, out var level) && level >= LogLevel.Warning,
            $"{environment}: Logging:LogLevel:System.Net.Http.HttpClient is '{configured ?? "(unset — inherits Default)"}'; "
            + "it must be Warning or higher, or every OTLP export logs itself into the next export.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root.");
    }
}
