using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Api.Tests.Architecture;

/// <summary>
/// Pure tenant-hatch scanner shared by the ban gate (<c>FeatureSlices_DoNotBypassTheTenantFilter</c>) and
/// the test that proves it bites (<see cref="TenantHatchGuardTests"/>). Given the source text of request-path
/// tenant-scoped files (feature slices AND the config-gated platform surfaces under <c>Endpoints/</c>), it
/// reports every use of a construct that reaches past the tenant filter / RLS backstop:
/// <list type="bullet">
/// <item><c>IgnoreQueryFilters</c> — banned outright.</item>
/// <item><c>QueryAllTenants</c> — the sanctioned cross-tenant hatch, allowed ONLY in a <c>*DataContributor.cs</c>
/// (dissolve/export); in request-path code it bypasses tenancy identically.</item>
/// <item>The RLS bypass tag — both the <c>RlsTags</c> identifier AND the raw
/// <c>"rls:cross-tenant"</c> literal (v3 audit RLS-6: the old scan matched only the identifier, so
/// <c>.TagWith("rls:cross-tenant")</c> slipped through while the interceptor still honored it).</item>
/// </list>
/// </summary>
public static class TenantHatchGuard
{
    public static IReadOnlyList<string> FindOffenders(IEnumerable<(string File, string Text)> sources)
    {
        var offenders = new List<string>();
        foreach (var (file, text) in sources)
        {
            var name = Path.GetFileName(file) ?? file;

            if (text.Contains("IgnoreQueryFilters", StringComparison.Ordinal))
                offenders.Add($"{name}: IgnoreQueryFilters — use IRepository<T>.QueryAllTenants() in a *DataContributor");

            // QueryAllTenants is allowed only in the dissolve/export contributors.
            if (!name.EndsWith("DataContributor.cs", StringComparison.Ordinal)
                && text.Contains("QueryAllTenants", StringComparison.Ordinal))
                offenders.Add($"{name}: QueryAllTenants — allowed only in *DataContributor.cs");

            // The RLS bypass tag self-sanctions a query to the DB backstop: ban both the RlsTags identifier
            // and the raw literal (RlsTags.CrossTenant == "rls:cross-tenant").
            if (text.Contains("RlsTags", StringComparison.Ordinal)
                || text.Contains(RlsTags.CrossTenant, StringComparison.Ordinal))
                offenders.Add($"{name}: RLS bypass tag — go through IRepository<T>.QueryAllTenants()");
        }
        return offenders;
    }
}
