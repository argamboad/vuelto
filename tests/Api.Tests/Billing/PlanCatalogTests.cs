using Vuelto.Core.Billing;

namespace Vuelto.Api.Tests.Billing;

/// <summary>
/// The plan catalog is code/config (ADR-006), not tenant data, so its shape is pinned here rather than
/// discovered at runtime. Two properties matter beyond the example numbers: Free is the fail-closed
/// landing place for anything unrecognised (which is what makes GATES-1's "billing off ⇒ everyone Free"
/// need no economics code of its own), and the Free seat limit is the cap a whole private test lives
/// inside.
/// </summary>
public class PlanCatalogTests
{
    [Fact]
    public void Free_SeatLimit_LeavesHeadroomForAFamilyPlusOne()
    {
        // GATES-1/ADR-027 raised this 3 → 5. Three was exactly one household (the maintainer's is three
        // people) with zero room, and a merely PENDING invitation already consumes a seat — so the
        // fourth person could not be invited at all. Downstream apps re-tune it; the platform ships
        // room to actually run a friends test.
        Assert.Equal(5, PlanCatalog.Get(PlanKeys.Free).SeatLimit);
    }

    [Fact]
    public void UnknownPlanKey_FailsClosedToFree()
    {
        // Absent/garbage plan key ⇒ Free, never the paid tier. GATES-1 leans on this: with billing off
        // no tenant has a subscription projection at all, so every tenant resolves here.
        var resolved = PlanCatalog.Get("no-such-plan");

        Assert.Equal(PlanKeys.Free, resolved.Key);
        Assert.False(resolved.Includes(Entitlements.ProFeature));
    }

    [Fact]
    public void Free_GrantsNoPaidEntitlement()
    {
        // The accepted consequence of running with billing off: nobody holds the paid entitlement.
        Assert.False(PlanCatalog.Get(PlanKeys.Free).Includes(Entitlements.ProFeature));
        Assert.True(PlanCatalog.Get(PlanKeys.Pro).Includes(Entitlements.ProFeature));
    }
}
