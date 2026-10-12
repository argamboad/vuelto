using System.Diagnostics;
using System.Security.Claims;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Vuelto.Core.Abstractions;

namespace Vuelto.Api.Observability;

/// <summary>
/// OpenTelemetry traces + metrics + logs (OBS-2, ADR-008). Instruments ASP.NET Core, outbound HttpClient, the
/// .NET runtime (GC/CPU/thread pool) and the Npgsql connection pool, and
/// Npgsql (the stable built-in Npgsql tracing — deliberately NOT the perpetually-beta EF Core
/// instrumentation, per ADR-C10). Request spans are tagged with <c>tenant_id</c>/<c>user_id</c> so
/// traces filter per tenant. Log records are exported too — with the formatted message, the request scope
/// (<c>tenant_id</c>/<c>user_id</c>) and the trace/span ids the SDK stamps — so an error line and the trace it
/// belongs to sit side by side in the collector instead of the text living only on the host's stdout.
/// The exporter is config-gated: <b>OTLP</b> when
/// <c>OpenTelemetry:Otlp:Endpoint</c> is set; otherwise nothing is exported (spans are still produced),
/// unless <c>OpenTelemetry:ConsoleExporter=true</c> turns on the noisy console exporter for local
/// debugging — so the app runs with no external dependency and a clean dev console by default.
/// The endpoint is the collector's <b>base</b> URL; over <c>http/protobuf</c> the per-signal path is
/// appended for the operator (<see cref="OtlpEndpoints"/>). Protocol and auth headers are the SDK's own
/// <c>OTEL_EXPORTER_OTLP_PROTOCOL</c> / <c>OTEL_EXPORTER_OTLP_HEADERS</c> variables.
/// </summary>
public static class TelemetryExtensions
{
    public static IServiceCollection AddAppTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var otlpEndpoint = configuration["OpenTelemetry:Otlp:Endpoint"];
        var otlpProtocol = configuration["OTEL_EXPORTER_OTLP_PROTOCOL"];
        var useConsole = configuration.GetValue<bool>("OpenTelemetry:ConsoleExporter");

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("Vuelto.Api"))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation(o =>
                        o.EnrichWithHttpResponse = (activity, response) => EnrichSpan(activity, response.HttpContext))
                    .AddHttpClientInstrumentation()
                    // Npgsql emits its own "Npgsql" ActivitySource — subscribe to it for DB spans
                    // (the stable built-in path; not the beta EF Core instrumentation, per ADR-C10).
                    .AddSource("Npgsql");
                ApplyExporter(tracing, otlpEndpoint, otlpProtocol, useConsole);
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    // The process itself (GC, heap, CPU, thread pool) — what says "the instance is too small" or
                    // "something leaks" long before any request errors; and Npgsql's own meter (connections in
                    // use / idle / waiting) — the pool is the usual first bottleneck of a small host.
                    .AddRuntimeInstrumentation()
                    .AddMeter("Npgsql");
                ApplyExporter(metrics, otlpEndpoint, otlpProtocol, useConsole);
            })
            .WithLogging(logging => ApplyExporter(logging, otlpEndpoint, otlpProtocol, useConsole));

        // What each exported record carries: the rendered message (not just the template), the logging scopes
        // (the per-request tenant_id/user_id scope from OBS-1) and the state values as attributes.
        services.Configure<OpenTelemetryLoggerOptions>(o =>
        {
            o.IncludeFormattedMessage = true;
            o.IncludeScopes = true;
            o.ParseStateValues = true;
        });

        // The SDK drops what it cannot export, silently, forever: one Warning in the app log when the collector
        // cannot be reached is the only sign an operator gets (v4 T42, OBS-2).
        if (!string.IsNullOrEmpty(otlpEndpoint))
        {
            services.AddSingleton(new OtlpCollectorProbe.Target(otlpEndpoint));
            services.AddHostedService<OtlpCollectorProbe>();
        }

        return services;
    }

    /// <summary>Tags the request span with <c>tenant_id</c>/<c>user_id</c> — identifiers only, never secrets.</summary>
    internal static void EnrichSpan(Activity activity, HttpContext context)
    {
        // RequestServices is null (annotation notwithstanding) for requests rejected before the
        // pipeline runs — Kestrel bad requests, early aborts. Enrichment must never fail a request.
        if (context.RequestServices?.GetService<ICurrentTenant>()?.TenantId is { } tenantId)
            activity.SetTag("tenant_id", tenantId.ToString());

        if (context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value is { } userId)
            activity.SetTag("user_id", userId);
    }

    private static void ApplyExporter(TracerProviderBuilder builder, string? otlpEndpoint, string? otlpProtocol, bool useConsole)
    {
        if (!string.IsNullOrEmpty(otlpEndpoint))
            builder.AddOtlpExporter("traces", o => Configure(o, otlpEndpoint, otlpProtocol, "traces"));
        else if (useConsole)
            builder.AddConsoleExporter();
    }

    private static void ApplyExporter(MeterProviderBuilder builder, string? otlpEndpoint, string? otlpProtocol, bool useConsole)
    {
        if (!string.IsNullOrEmpty(otlpEndpoint))
            builder.AddOtlpExporter("metrics", o => Configure(o, otlpEndpoint, otlpProtocol, "metrics"));
        else if (useConsole)
            builder.AddConsoleExporter();
    }

    private static void ApplyExporter(LoggerProviderBuilder builder, string? otlpEndpoint, string? otlpProtocol, bool useConsole)
    {
        if (!string.IsNullOrEmpty(otlpEndpoint))
            builder.AddOtlpExporter("logs", o => Configure(o, otlpEndpoint, otlpProtocol, "logs"));
        else if (useConsole)
            builder.AddConsoleExporter();
    }

    // Endpoint AND protocol from the same two configuration values (v4 T42): the signal path was derived from
    // OTEL_EXPORTER_OTLP_PROTOCOL while the exporter's protocol came from the SDK's own environment read, so a
    // protocol set anywhere but the process environment made the two disagree — http paths, gRPC on the wire, 404.
    // The options are NAMED per signal: an unnamed AddOtlpExporter applies its delegate to a private instance that
    // no IOptionsMonitor ever sees, so nothing could assert what the exporter was actually told.
    private static void Configure(OpenTelemetry.Exporter.OtlpExporterOptions o, string otlpEndpoint, string? otlpProtocol, string signal)
    {
        o.Endpoint = OtlpEndpoints.ForSignal(otlpEndpoint, otlpProtocol, signal);
        if (OtlpEndpoints.ProtocolFor(otlpProtocol) is { } protocol)
            o.Protocol = protocol;
    }
}
