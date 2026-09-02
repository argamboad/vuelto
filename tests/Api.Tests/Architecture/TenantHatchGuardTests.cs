namespace Vuelto.Api.Tests.Architecture;

/// <summary>
/// Proves the tenant-hatch scanner (<see cref="TenantHatchGuard"/>) catches the cases the v3 audit flagged:
/// a hatch used in an <c>Endpoints/</c> file (S0-G2 — the old scan only saw <c>Features/</c>), and the RLS
/// bypass tag written as the raw <c>"rls:cross-tenant"</c> literal rather than the <c>RlsTags</c> identifier
/// (RLS-6 — the old scan matched only the identifier). If any of these regress, the real
/// <c>FeatureSlices_DoNotBypassTheTenantFilter</c> gate has gone blind again.
/// </summary>
public class TenantHatchGuardTests
{
    [Fact]
    public void Flags_IgnoreQueryFilters_InAnEndpointFile()
    {
        var sources = new[] { ("WebhookEndpoints.cs", "db.Set<X>().IgnoreQueryFilters().ToList();") };
        Assert.Contains(TenantHatchGuard.FindOffenders(sources), o => o.Contains("IgnoreQueryFilters"));
    }

    [Fact]
    public void Flags_QueryAllTenants_InARequestPathFile()
    {
        var sources = new[] { ("ApiKeyEndpoints.cs", "apiKeys.QueryAllTenants().Where(k => true);") };
        Assert.Contains(TenantHatchGuard.FindOffenders(sources), o => o.Contains("QueryAllTenants"));
    }

    [Fact]
    public void Allows_QueryAllTenants_InADataContributor()
    {
        var sources = new[] { ("ApiKeyDataContributor.cs", "apiKeys.QueryAllTenants().Where(k => true);") };
        Assert.Empty(TenantHatchGuard.FindOffenders(sources));
    }

    [Fact]
    public void Flags_RlsTags_Identifier()
    {
        var sources = new[] { ("FooEndpoints.cs", "q.TagWith(RlsTags.CrossTenant);") };
        Assert.Contains(TenantHatchGuard.FindOffenders(sources), o => o.Contains("RLS bypass tag"));
    }

    [Fact]
    public void Flags_TheRawRlsTagLiteral_NotJustTheIdentifier()
    {
        // The RLS-6 case: the old scan matched only "RlsTags", so this slipped through.
        var sources = new[] { ("FooEndpoints.cs", "q.TagWith(\"rls:cross-tenant\");") };
        Assert.Contains(TenantHatchGuard.FindOffenders(sources), o => o.Contains("RLS bypass tag"));
    }

    [Fact]
    public void CleanRequestPathCode_HasNoOffenders()
    {
        var sources = new[]
        {
            ("NotesEndpoints.cs", "notes.Query().Where(n => n.Title == title).ToListAsync();"),
            ("WebhookEndpoints.cs", "subscriptions.Query().OrderBy(s => s.CreatedAt);"),
        };
        Assert.Empty(TenantHatchGuard.FindOffenders(sources));
    }
}
