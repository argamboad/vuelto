namespace Vuelto.Api.Observability;

/// <summary>
/// Resolves the per-signal OTLP endpoint from the single configured base URL (OBS-2).
/// <para>
/// The OpenTelemetry SDK appends <c>/v1/{signal}</c> only when the endpoint comes from its own
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> variable. An endpoint set in code — which is how the platform's
/// config gate (<c>OpenTelemetry:Otlp:Endpoint</c>) hands it over — is used verbatim. Over gRPC that is
/// fine (one port, no path). Over <c>http/protobuf</c> (Grafana Cloud, Honeycomb, most managed gateways) a
/// base URL such as <c>https://otlp-gateway.example/otlp</c> then receives every signal at the bare path,
/// the gateway answers 404, and the exporter drops the data silently. Operators kept configuring the base
/// URL — as every vendor's docs tell them to — and saw nothing. This helper closes that gap: with the
/// HTTP protocol selected it appends the signal path unless the operator already spelled one out.
/// </para>
/// </summary>
internal static class OtlpEndpoints
{
    internal const string HttpProtobuf = "http/protobuf";
    internal const string Grpc = "grpc";
    private static readonly string[] KnownSignals = ["traces", "metrics", "logs"];

    /// <summary>
    /// The exporter protocol for the configured <c>OTEL_EXPORTER_OTLP_PROTOCOL</c> value (v4 T42, OBS-3): the
    /// SAME value the signal paths are derived from, so a protocol set in appsettings or user-secrets — where the
    /// SDK, which reads only process environment variables, never sees it — can no longer leave the app building
    /// http/protobuf paths while the SDK speaks gRPC. Null (unset/unknown) leaves the SDK's own default.
    /// </summary>
    internal static OpenTelemetry.Exporter.OtlpExportProtocol? ProtocolFor(string? protocol)
    {
        var value = protocol?.Trim();
        if (string.Equals(value, HttpProtobuf, StringComparison.OrdinalIgnoreCase)) return OpenTelemetry.Exporter.OtlpExportProtocol.HttpProtobuf;
        if (string.Equals(value, Grpc, StringComparison.OrdinalIgnoreCase)) return OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
        return null;
    }

    /// <summary>
    /// The endpoint the <paramref name="signal"/> exporter should post to. <paramref name="protocol"/> is the
    /// value of <c>OTEL_EXPORTER_OTLP_PROTOCOL</c> (null/empty ⇒ the SDK default, gRPC ⇒ base URL as-is).
    /// </summary>
    internal static Uri ForSignal(string baseEndpoint, string? protocol, string signal)
    {
        var uri = new Uri(baseEndpoint, UriKind.Absolute);
        if (!string.Equals(protocol?.Trim(), HttpProtobuf, StringComparison.OrdinalIgnoreCase))
            return uri;

        var path = uri.AbsolutePath.TrimEnd('/');
        // The operator already named a signal path (the pre-fix contract) — honour it verbatim, even if it
        // names another signal: they asked for exactly that URL.
        if (KnownSignals.Any(s => path.EndsWith("/v1/" + s, StringComparison.OrdinalIgnoreCase)))
            return uri;

        var builder = new UriBuilder(uri) { Path = path + "/v1/" + signal };
        return builder.Uri;
    }
}
