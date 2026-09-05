using Vuelto.Api.Observability;

namespace Vuelto.Api.Tests.Observability;

/// <summary>
/// OBS-2: the single configured OTLP base URL becomes a per-signal URL over <c>http/protobuf</c>, where the
/// SDK does not append the path for a programmatically-set endpoint (a base URL would 404 silently at
/// Grafana Cloud and friends). gRPC and an operator-spelled signal path pass through untouched.
/// </summary>
public class OtlpEndpointsTests
{
    [Theory]
    [InlineData("traces")]
    [InlineData("metrics")]
    public void HttpProtobuf_AppendsTheSignalPath_ToABaseUrl(string signal)
    {
        var uri = OtlpEndpoints.ForSignal("https://otlp-gateway-prod-us-west-0.grafana.net/otlp", "http/protobuf", signal);

        Assert.Equal($"https://otlp-gateway-prod-us-west-0.grafana.net/otlp/v1/{signal}", uri.ToString());
    }

    [Theory]
    [InlineData("https://collector.example/otlp/", "https://collector.example/otlp/v1/traces")]  // trailing slash
    [InlineData("https://collector.example", "https://collector.example/v1/traces")]             // bare origin
    [InlineData("http://localhost:4318", "http://localhost:4318/v1/traces")]                     // local collector
    public void HttpProtobuf_NormalisesSlashes(string configured, string expected)
    {
        Assert.Equal(expected, OtlpEndpoints.ForSignal(configured, "http/protobuf", "traces").ToString());
    }

    [Fact]
    public void HttpProtobuf_ProtocolValue_IsCaseAndWhitespaceInsensitive()
    {
        var uri = OtlpEndpoints.ForSignal("https://collector.example/otlp", "  HTTP/Protobuf ", "metrics");

        Assert.Equal("https://collector.example/otlp/v1/metrics", uri.ToString());
    }

    [Theory]
    [InlineData("https://collector.example/otlp/v1/traces", "traces")]
    [InlineData("https://collector.example/otlp/v1/traces", "metrics")] // operator named a path — honoured verbatim
    [InlineData("https://collector.example/otlp/v1/metrics/", "traces")]
    public void HttpProtobuf_LeavesAnExplicitSignalPathAlone(string configured, string signal)
    {
        Assert.Equal(new Uri(configured), OtlpEndpoints.ForSignal(configured, "http/protobuf", signal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("grpc")]
    public void Grpc_OrUnsetProtocol_UsesTheEndpointVerbatim(string? protocol)
    {
        var uri = OtlpEndpoints.ForSignal("http://localhost:4317", protocol, "traces");

        Assert.Equal("http://localhost:4317/", uri.ToString());
    }

    [Fact]
    public void RelativeEndpoint_IsRejected()
    {
        Assert.Throws<UriFormatException>(() => OtlpEndpoints.ForSignal("otlp-gateway/otlp", "http/protobuf", "traces"));
    }
}
