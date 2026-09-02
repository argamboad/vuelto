using Vuelto.Core.Abstractions;

namespace Vuelto.Api.Tests.Infrastructure;

/// <summary>
/// Permissive <see cref="IOutboundUrlGuard"/> for tests that aren't exercising the SSRF guard itself —
/// allows any well-formed absolute URL so fixtures can use <c>https://example.test</c> / loopback stubs
/// without real DNS. The guard's own behavior is covered by <c>OutboundUrlGuardTests</c>.
/// </summary>
public sealed class AllowAllUrlGuard : IOutboundUrlGuard
{
    // Still enforces the http/https scheme (so "ftp://" / malformed are rejected) — only the SSRF/DNS
    // address checks are skipped, so fixtures can use example.test / loopback without real resolution.
    public ValueTask<bool> IsAllowedAsync(string? url, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(
            Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));
}
