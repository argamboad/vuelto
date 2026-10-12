using System.Text.Json;
using System.Text.RegularExpressions;
using Vuelto.Api.Configuration;

namespace Vuelto.Api.Tests;

/// <summary>
/// v4 audit TR-21 (T63, R83 + R119): <c>PostmanParityTests</c> proves every endpoint HAS a request in the
/// collection; these prove the request SAYS what the endpoint answers. The signup refusal (GATES-2) and the
/// billing gate (GATES-1) shipped with three sign-in descriptions and a whole folder that never mentioned
/// them, because presence was all that was checked. Source scans of <c>src/Api/Controllers</c> against the
/// committed collection — no database, so they run in the fast lane.
/// </summary>
public class PostmanParityDescriptionTests
{
    [Fact]
    public void EveryErrorCodeAnActionReturns_IsNamedInItsRequestDescription() // R83
    {
        var requests = DocumentedRequests();
        var actions = ControllerActions();
        var withCodes = actions.Where(a => a.Codes.Count > 0).ToList();
        Assert.True(withCodes.Count >= 40, $"probe: only {withCodes.Count} actions with an error code were parsed — did the controllers' shape change?");

        var missing = new List<string>();
        foreach (var action in withCodes)
        {
            var described = RequestsFor(requests, action);
            if (described.Count == 0)
                continue; // absence is PostmanParityTests' finding, not this gate's
            foreach (var code in action.Codes.Where(c => !described.Any(d => d.Description.Contains(c, StringComparison.Ordinal))))
                missing.Add($"{action.Method} {action.Path}: {code}");
        }

        Assert.True(missing.Count == 0,
            "Error codes an action returns (an ErrorResponse(\"<code>\") or a ?error=<code> redirect) that its Postman "
            + "request never names — the collection is the canonical API documentation (CLAUDE.md), so add the code "
            + $"and its status to the request's description:\n - {string.Join("\n - ", missing)}");
    }

    [Fact]
    public void GatedAndRefusingRequests_NameTheGateKeyAndTheRefusal() // R119
    {
        var requests = DocumentedRequests();
        var actions = ControllerActions();
        var missing = new List<string>();

        var gatedControllers = BillingGateConvention.GatedControllers.Select(t => t.Name).ToHashSet();
        var gated = actions.Where(a => gatedControllers.Contains(a.Controller)).ToList();
        Assert.True(gated.Count >= 4, $"probe: {gated.Count} actions found on the billing-gated controllers");
        foreach (var action in gated)
            foreach (var request in RequestsFor(requests, action).Where(r => !r.Description.Contains("Billing:Enabled", StringComparison.Ordinal)))
                missing.Add($"{action.Method} {action.Path}: does not say it answers 404 when Billing:Enabled is off");

        var refusing = actions.Where(a => a.Body.Contains("catch (SignupNotAllowedException)", StringComparison.Ordinal)).ToList();
        Assert.True(refusing.Count >= 4, $"probe: {refusing.Count} actions catch SignupNotAllowedException — OTP verify, magic-link verify and both OAuth callbacks do");
        foreach (var action in refusing)
            foreach (var request in RequestsFor(requests, action).Where(r => !r.Description.Contains("signup_not_allowed", StringComparison.Ordinal)))
                missing.Add($"{action.Method} {action.Path}: does not name the signup_not_allowed refusal (GATES-2)");

        Assert.True(missing.Count == 0,
            $"Postman descriptions below the gate/refusal floor:\n - {string.Join("\n - ", missing)}");
    }

    private sealed record ControllerAction(string Controller, string Method, string Path, string Body, IReadOnlyList<string> Codes);

    private sealed record DocumentedRequest(string Method, string Path, string Description);

    /// <summary>Every controller action, as source: its verb, its route and the text of its body.</summary>
    private static List<ControllerAction> ControllerActions()
    {
        var actions = new List<ControllerAction>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Api", "Controllers"), "*.cs"))
        {
            var text = File.ReadAllText(file);
            var route = Regex.Match(text, @"\[Route\(""([^""]+)""\)\]");
            if (!route.Success)
                continue; // the abstract bases carry no route
            foreach (Match m in Regex.Matches(text,
                         @"\[Http(Get|Post|Put|Delete|Patch)(?:\(""([^""]*)""\))?\](.*?)(?=\[Http(?:Get|Post|Put|Delete|Patch)|\z)",
                         RegexOptions.Singleline))
            {
                var body = m.Groups[3].Value;
                var codes = Regex.Matches(body, @"ErrorResponse\(\s*""([a-z_]+)""").Select(c => c.Groups[1].Value)
                    .Concat(Regex.Matches(body, @"[?&](?:error|link_error)=([a-z_]+)").Select(c => c.Groups[1].Value))
                    .Concat(Regex.Matches(body, @"To\(""error"",\s*""([a-z_]+)""\)").Select(c => c.Groups[1].Value))
                    .Distinct().Order().ToList();
                actions.Add(new ControllerAction(Path.GetFileNameWithoutExtension(file), m.Groups[1].Value.ToUpperInvariant(),
                    "/" + $"{route.Groups[1].Value}/{m.Groups[2].Value}".Trim('/'), body, codes));
            }
        }
        return actions;
    }

    private static List<DocumentedRequest> RequestsFor(List<DocumentedRequest> requests, ControllerAction action)
    {
        // Route template → regex: {param} / {param:constraint} match one concrete segment.
        var pattern = "^" + Regex.Replace(Regex.Escape(action.Path), @"\\\{[^}/]+\}", "[^/]+") + "$";
        return [.. requests.Where(r => r.Method.Equals(action.Method, StringComparison.OrdinalIgnoreCase)
                                       && Regex.IsMatch(r.Path, pattern, RegexOptions.IgnoreCase))];
    }

    private static List<DocumentedRequest> DocumentedRequests()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "docs", "postman", "Vuelto.postman_collection.json")));

        var requests = new List<DocumentedRequest>();
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
                    var description = req.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString() ?? "" : "";
                    requests.Add(new DocumentedRequest(method, "/" + raw.Replace("{{baseUrl}}", "").Split('?')[0].Trim('/'), description));
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
