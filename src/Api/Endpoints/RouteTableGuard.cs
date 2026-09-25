using System.Text;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Vuelto.Api.Endpoints;

/// <summary>
/// Boot-time backstop for route uniqueness (v4 T13, ADV-P4-13, R155). Two endpoints with the same HTTP method,
/// pattern and order boot fine and then turn every request to that route into a 500 (AmbiguousMatchException),
/// before authorization runs, so anonymous callers see it too. The CI gate <c>RouteGroupPrefixes_AreUnique</c>
/// catches a shared group prefix from source; this checks the real route table, whatever mapped it, and refuses
/// to start naming both endpoints.
/// <para>
/// Two routes collide when their patterns match the same requests the same way: parameter names don't count
/// (<c>{id}</c> = <c>{noteId}</c>), constraints and optionality do (routing ranks <c>{id:int}</c> above
/// <c>{slug}</c>), an endpoint with no method metadata answers every method, and a different
/// <see cref="RouteEndpoint.Order"/> (a fallback) is ranked, not ambiguous.
/// </para>
/// </summary>
public static class RouteTableGuard
{
    private const string AnyMethod = "*";

    public static void EnsureUnique(IEnumerable<Endpoint> endpoints)
    {
        var claimed = new Dictionary<(string Pattern, int Order, string Method), RouteEndpoint>();
        var collisions = new List<string>();

        foreach (var endpoint in endpoints.OfType<RouteEndpoint>())
        {
            var pattern = Shape(endpoint.RoutePattern);
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is { Count: > 0 } m
                ? m.Select(x => x.ToUpperInvariant()).ToList()
                : [AnyMethod];

            foreach (var method in methods)
            {
                // A method-specific route collides with the any-method one on the same pattern, and vice versa.
                var rivals = method == AnyMethod
                    ? claimed.Where(c => c.Key.Pattern == pattern && c.Key.Order == endpoint.Order).Select(c => (c.Key.Method, c.Value))
                    : new[] { method, AnyMethod }
                        .Where(x => claimed.ContainsKey((pattern, endpoint.Order, x)))
                        .Select(x => (x, claimed[(pattern, endpoint.Order, x)]));

                foreach (var (rivalMethod, rival) in rivals.ToList())
                    collisions.Add($"{(method == AnyMethod ? rivalMethod : method)} {pattern}: " +
                                   $"'{rival.DisplayName}' and '{endpoint.DisplayName}'");

                claimed.TryAdd((pattern, endpoint.Order, method), endpoint);
            }
        }

        if (collisions.Count > 0)
            throw new InvalidOperationException(
                "Duplicate routes: every request to these would fail with AmbiguousMatchException. " +
                string.Join("; ", collisions.Distinct()));
    }

    // The pattern as routing sees it: literals case-insensitive, parameters by kind and constraints, not by name.
    private static string Shape(RoutePattern pattern)
    {
        var sb = new StringBuilder();
        foreach (var segment in pattern.PathSegments)
        {
            if (sb.Length > 0) sb.Append('/');
            foreach (var part in segment.Parts)
                switch (part)
                {
                    case RoutePatternLiteralPart literal:
                        sb.Append(literal.Content.ToLowerInvariant());
                        break;
                    case RoutePatternSeparatorPart separator:
                        sb.Append(separator.Content);
                        break;
                    case RoutePatternParameterPart parameter:
                        sb.Append('{').Append(parameter.IsCatchAll ? "*" : "");
                        foreach (var policy in parameter.ParameterPolicies)
                            sb.Append(':').Append(policy.Content);
                        sb.Append(parameter.IsOptional ? "?" : "").Append('}');
                        break;
                }
        }
        return sb.ToString();
    }
}
