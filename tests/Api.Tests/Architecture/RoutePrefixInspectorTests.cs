namespace Vuelto.Api.Tests.Architecture;

/// <summary>
/// Proves the route-uniqueness collector (<see cref="RoutePrefixInspector"/>) actually detects the collision
/// that v3 audit finding ADV-P4-1 showed slipped through: two feature slices claiming the same prefix via
/// <c>MapTenantFeatureGroup("…")</c>. The old gate matched only raw <c>MapGroup("…")</c>, so this exact case
/// passed green. If any of these ever regresses, the real <c>RouteGroupPrefixes_AreUnique</c> gate has gone
/// blind again.
/// </summary>
public class RoutePrefixInspectorTests
{
    [Fact]
    public void ReportsCollision_BetweenTwoFeatureSlices_UsingMapTenantFeatureGroup()
    {
        // The precise ADV-P4-1 scenario: two slices, each via the MANDATED helper, both claim /api/projects.
        var groupSources = new[]
        {
            """app.MapTenantFeatureGroup("/api/projects").RequirePermission(Permission.X);""",
            """app.MapTenantFeatureGroup("/api/projects").RequireEntitlement(Entitlements.Y);""",
        };

        var dupes = RoutePrefixInspector.FindDuplicatePrefixes(controllerSources: [], groupSources);

        Assert.Contains("/api/projects", dupes);
    }

    [Fact]
    public void ReportsCollision_BetweenAFeatureGroup_AndARawEndpointGroup()
    {
        // A feature slice (MapTenantFeatureGroup) colliding with a platform Endpoints/ surface (raw MapGroup)
        // — the cross-directory case the old gate missed by never scanning Endpoints/.
        var groupSources = new[]
        {
            """app.MapTenantFeatureGroup("/api/webhooks");""",   // hypothetical feature
            """app.MapGroup("/api/webhooks").RequireAuthorization();""", // real platform surface
        };

        var dupes = RoutePrefixInspector.FindDuplicatePrefixes(controllerSources: [], groupSources);

        Assert.Contains("/api/webhooks", dupes);
    }

    [Fact]
    public void ReportsCollision_BetweenAControllerRoute_AndAFeatureGroup()
    {
        var controllerSources = new[] { """[Route("api/billing")]""" };
        var groupSources = new[] { """app.MapTenantFeatureGroup("/api/billing");""" };

        var dupes = RoutePrefixInspector.FindDuplicatePrefixes(controllerSources, groupSources);

        Assert.Contains("/api/billing", dupes);
    }

    [Fact]
    public void SharedControllerPrefix_AcrossFocusedControllers_IsNotACollision()
    {
        // The /api/auth family is intentionally split across several controllers (SRP) — a shared [Route]
        // prefix must count once, not read as a duplicate.
        var controllerSources = new[]
        {
            """[Route("api/auth")] public class AuthLoginController""",
            """[Route("api/auth")] public class AuthRefreshController""",
        };

        var dupes = RoutePrefixInspector.FindDuplicatePrefixes(controllerSources, groupSources: []);

        Assert.Empty(dupes);
    }

    [Fact]
    public void DistinctPrefixes_AcrossAllForms_AreClean()
    {
        var controllerSources = new[] { """[Route("api/household")]""", """[Route("api/billing")]""" };
        var groupSources = new[]
        {
            """app.MapGroup("/api/webhooks");""",
            """app.MapTenantFeatureGroup("/api/notes");""",
        };

        var dupes = RoutePrefixInspector.FindDuplicatePrefixes(controllerSources, groupSources);

        Assert.Empty(dupes);
    }
}
