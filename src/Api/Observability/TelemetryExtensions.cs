using System.Diagnostics;
using System.Security.Claims;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Vuelto.Core.Abstractions;

namespace Vuelto.Api.Observability;

/// <summary>
/// OpenTelemetry traces + metrics (OBS-2, ADR-008). Instruments ASP.NET Core, outbound HttpClient, and
/// Npgsql (the stable built-in Npgsql tracing — deliberately NOT the perpetually-beta EF Core
/// instrumentation, per ADR-C10). Request spans are tagged with <c>tenant_id</c>/<c>user_id</c> so
/// traces filter per tenant. The exporter is config-gated: <b>OTLP</b> when
/// <c>OpenTelemetry:Otlp:Endpoint</c> is set; otherwise nothing is exported (spans are still produced),
/// unless <c>OpenTelemetry:ConsoleExporter=true</c> turns on the noisy console exporter for local
/// debugging — so the app runs with no external dependency and a clean dev console by default.
/// </summary>
public static class TelemetryExtensions
{
    public static IServiceCollection AddAppTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var otlpEndpoint = configuration["OpenTelemetry:Otlp:Endpoint"];
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
                ApplyExporter(tracing, otlpEndpoint, useConsole);
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();
                ApplyExporter(metrics, otlpEndpoint, useConsole);
            });

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

    private static void ApplyExporter(TracerProviderBuilder builder, string? otlpEndpoint, bool useConsole)
    {
        if (!string.IsNullOrEmpty(otlpEndpoint))
            builder.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
        else if (useConsole)
            builder.AddConsoleExporter();
    }

    private static void ApplyExporter(MeterProviderBuilder builder, string? otlpEndpoint, bool useConsole)
    {
        if (!string.IsNullOrEmpty(otlpEndpoint))
            builder.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
        else if (useConsole)
            builder.AddConsoleExporter();
    }
}
