using System.Text.RegularExpressions;

namespace Perezosoft.Api.Tests.Architecture;

/// <summary>
/// Pure route-prefix collector shared by the uniqueness gate (<c>RouteGroupPrefixes_AreUnique</c>) and the
/// test that proves it bites (<see cref="RoutePrefixInspectorTests"/>). Given the raw source text of the
/// controller files and the endpoint/feature group files, it extracts every declared <c>/api/&lt;x&gt;</c>
/// prefix and returns the ones claimed more than once.
/// <para>
/// v3 audit finding ADV-P4-1: the old gate matched only raw <c>MapGroup("…")</c>, but every feature slice is
/// REQUIRED to register via <c>MapTenantFeatureGroup("…")</c> (enforced by
/// <c>FeatureFiles_RegisterRoutesViaMapTenantFeatureGroup_NotRawMapGroup</c>) — a form the old regex never
/// matched, so the Features prefix set was always empty and two slices claiming the same prefix passed green.
/// This collector matches BOTH forms, so a feature prefix is never invisible to the collision check.
/// </para>
/// </summary>
public static class RoutePrefixInspector
{
    // [Route("api/x")] on controllers.
    private static readonly Regex RouteAttribute = new(@"\[Route\(""([^""]+)""\)\]", RegexOptions.Compiled);

    // The raw MapGroup("…") used by the platform surfaces under Endpoints/ AND the mandated
    // MapTenantFeatureGroup("…") used by feature slices. Matching both is the whole point of ADV-P4-1.
    private static readonly Regex GroupCall = new(@"Map(?:TenantFeature)?Group\(""([^""]+)""\)", RegexOptions.Compiled);

    /// <param name="controllerSources">Source text of every file under <c>Controllers/</c>.</param>
    /// <param name="groupSources">Source text of every file under <c>Endpoints/</c> and <c>Features/</c>.</param>
    public static IReadOnlyList<string> FindDuplicatePrefixes(
        IEnumerable<string> controllerSources, IEnumerable<string> groupSources)
    {
        // A single logical controller surface may be split across several focused controllers that share one
        // [Route] prefix (the /api/auth family, B9-1/SOLID-2) — that's SRP, not a collision — so a shared
        // controller prefix counts once.
        var controllerPrefixes = controllerSources
            .SelectMany(t => RouteAttribute.Matches(t).Select(m => Normalize(m.Groups[1].Value)))
            .Distinct(StringComparer.Ordinal);

        // Every endpoint/feature group is its own surface, so a repeat here IS the collision we want to
        // catch — no dedup.
        var groupPrefixes = groupSources
            .SelectMany(t => GroupCall.Matches(t).Select(m => Normalize(m.Groups[1].Value)));

        return controllerPrefixes.Concat(groupPrefixes)
            .GroupBy(p => p, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
    }

    private static string Normalize(string route) => "/" + route.Trim('/').ToLowerInvariant();
}
