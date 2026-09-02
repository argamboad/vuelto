using Microsoft.Extensions.Hosting;
using Perezosoft.Api.Tests.Infrastructure;
using Perezosoft.Infrastructure.Http;

namespace Perezosoft.Api.Tests.Webhooks;

/// <summary>
/// v2 audit GAP-2: the outbound URL guard blocks SSRF. Outside Development it is https-only and rejects
/// any URL whose host is a loopback / link-local / private / metadata address (checked against IP
/// literals here — no DNS). In Development it is permissive so local endpoints and the test suite work.
/// </summary>
public class OutboundUrlGuardTests
{
    private static OutboundUrlGuard Prod() => new(new FakeHostEnvironment(Environments.Production));
    private static OutboundUrlGuard Dev() => new(new FakeHostEnvironment(Environments.Development));

    [Theory]
    [InlineData("https://169.254.169.254/latest/meta-data/")] // cloud metadata
    [InlineData("https://127.0.0.1/hook")]                    // loopback
    [InlineData("https://[::1]/hook")]                        // IPv6 loopback
    [InlineData("https://10.0.0.5/hook")]                     // RFC-1918
    [InlineData("https://172.16.4.4/hook")]                   // RFC-1918
    [InlineData("https://192.168.1.1/hook")]                  // RFC-1918
    [InlineData("https://100.64.0.1/hook")]                   // CGNAT
    [InlineData("https://0.0.0.0/hook")]                      // unspecified
    public async Task Prod_RejectsSsrfTargets(string url) =>
        Assert.False(await Prod().IsAllowedAsync(url));

    [Theory]
    [InlineData("http://93.184.216.34/hook")]  // http not allowed outside Development
    [InlineData("ftp://example.test/hook")]    // wrong scheme
    [InlineData("not-a-url")]                   // malformed
    [InlineData("")]
    [InlineData(null)]
    public async Task Prod_RejectsBadSchemeOrMalformed(string? url) =>
        Assert.False(await Prod().IsAllowedAsync(url));

    [Fact]
    public async Task Prod_AllowsPublicHttpsIpLiteral() =>
        Assert.True(await Prod().IsAllowedAsync("https://93.184.216.34/hook")); // a public address

    [Theory]
    [InlineData("http://localhost:5000/hook")]      // loopback + http OK locally
    [InlineData("https://10.0.0.5/hook")]           // private OK locally (dev/test)
    [InlineData("http://127.0.0.1/hook")]
    public async Task Dev_IsPermissive(string url) =>
        Assert.True(await Dev().IsAllowedAsync(url));
}
