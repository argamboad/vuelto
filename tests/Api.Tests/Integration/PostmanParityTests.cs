using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Perezosoft.Api.Tests.Infrastructure;

namespace Perezosoft.Api.Tests.Integration;

/// <summary>
/// v3 audit TR-6 (T55, R74): the Postman collection is the CANONICAL API documentation (CLAUDE.md
/// binding rule) — its README claims every HTTP surface, and six auth endpoints were silently absent.
/// This gate makes the claim machine-true: every route the REAL app maps under <c>/api</c> (read from
/// <see cref="EndpointDataSource"/> — the actual route table, controllers + minimal APIs alike) must
/// be documented by a matching request in the collection, or sit on the explicit exclusion list below
/// with a rationale. Config-gated surfaces (PUBAPI/HOOKS) are compared only when mapped in this
/// harness; the collection documenting MORE than the runtime table is fine (it also covers gated-on
/// setups), the reverse is the drift this gate exists to stop.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PostmanParityTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    [Fact]
    public void EveryMappedApiEndpoint_IsDocumentedInThePostmanCollection()
    {
        // Excluded by design — each with the reason it does not belong in the collection.
        var excluded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["POST api/billing/webhook"] = "provider-signed callback; documented via its dedicated folder request", // (it IS in the collection; kept here as the pattern for true exclusions)
        };
        excluded.Clear(); // nothing excluded today — the six redirect/native endpoints are documented as doc-only requests

        var mapped = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } raw && raw.TrimStart('/').StartsWith("api/", StringComparison.OrdinalIgnoreCase))
            .SelectMany(e =>
            {
                var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
                // Normalize: leading slash, no trailing slash (minimal-API groups map "/" as ".../").
                var path = "/" + e.RoutePattern.RawText!.Trim('/');
                return methods.Where(m => m != "HEAD").Select(m => (Method: m, Path: path));
            })
            .Distinct()
            .ToList();
        Assert.NotEmpty(mapped); // the probe must be alive

        var documented = DocumentedRequests();

        var missing = new List<string>();
        foreach (var (method, path) in mapped)
        {
            var key = $"{method} {path.TrimStart('/')}";
            if (excluded.ContainsKey(key))
                continue;
            // Route template → regex: {param} / {param:constraint} match one concrete segment.
            var pattern = "^" + Regex.Replace(Regex.Escape(path), @"\\\{[^}/]+\}", "[^/]+") + "$";
            if (!documented.Any(d => d.Method.Equals(method, StringComparison.OrdinalIgnoreCase)
                                     && Regex.IsMatch(d.Path, pattern, RegexOptions.IgnoreCase)))
                missing.Add($"{method} {path}");
        }

        Assert.True(missing.Count == 0,
            "API endpoints mapped by the app but absent from docs/postman/Perezosoft.postman_collection.json "
            + "— the collection is canonical (CLAUDE.md): add a request (doc-only for browser redirects) or an "
            + $"exclusion rationale above:\n - {string.Join("\n - ", missing)}");
    }

    /// <summary>Every (method, path) request in the committed collection, {{baseUrl}} + query stripped.</summary>
    private static List<(string Method, string Path)> DocumentedRequests()
    {
        var root = RepoRoot();
        using var doc = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "docs", "postman", "Perezosoft.postman_collection.json")));

        var requests = new List<(string, string)>();
        void Walk(JsonElement items)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("item", out var children))
                    Walk(children);
                else if (item.TryGetProperty("request", out var req))
                {
                    var method = req.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
                    var raw = req.TryGetProperty("url", out var url)
                        ? url.ValueKind == JsonValueKind.String ? url.GetString() ?? "" :
                          url.TryGetProperty("raw", out var r) ? r.GetString() ?? "" : ""
                        : "";
                    var path = raw.Replace("{{baseUrl}}", "").Split('?')[0].Trim('/');
                    if (path.Length > 0)
                        requests.Add((method, "/" + path)); // normalized like the route table (leading /, no trailing)
                }
            }
        }
        Walk(doc.RootElement.GetProperty("item"));
        return requests;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "postman")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root.");
    }
}
