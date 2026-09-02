using Perezosoft.Infrastructure.Persistence;

namespace Perezosoft.Api.Tests.Rls;

/// <summary>
/// Proves the cross-tenant tag detection (<see cref="RlsSessionInterceptor.HasLeadingCrossTenantTag"/>) is
/// anchored to EF's leading query-tag block, not a substring match anywhere in the SQL (v3 audit RLS-7).
/// The old <c>CommandText.Contains("-- rls:cross-tenant")</c> would honor the marker wherever it appeared —
/// a string literal, an echoed value, a non-sanctioned multi-tag — and disarm the RLS backstop for that
/// command. These pin that only an EXACT, LEADING tag line sanctions a bypass.
/// </summary>
public class RlsTagDetectionTests
{
    private const string Tag = "-- " + RlsTags.CrossTenant; // "-- rls:cross-tenant"

    [Fact]
    public void LeadingTagLine_IsHonored()
    {
        var sql = $"{Tag}\nSELECT * FROM \"Notes\";";
        Assert.True(RlsSessionInterceptor.HasLeadingCrossTenantTag(sql));
    }

    [Fact]
    public void LeadingTag_ThenBlankLine_BeforeSql_IsHonored()
    {
        var sql = $"{Tag}\n\nSELECT * FROM \"Notes\";";
        Assert.True(RlsSessionInterceptor.HasLeadingCrossTenantTag(sql));
    }

    [Fact]
    public void OneOfSeveralLeadingTags_IsHonored()
    {
        var sql = $"-- some-other-tag\n{Tag}\nSELECT 1;";
        Assert.True(RlsSessionInterceptor.HasLeadingCrossTenantTag(sql));
    }

    [Fact]
    public void MarkerAfterTheSqlBody_IsIgnored()
    {
        // The RLS-7 case: a marker that appears AFTER the SQL has started must not sanction a bypass.
        var sql = $"SELECT * FROM \"Notes\";\n{Tag}";
        Assert.False(RlsSessionInterceptor.HasLeadingCrossTenantTag(sql));
    }

    [Fact]
    public void MarkerInsideAStringLiteral_IsIgnored()
    {
        var sql = "SELECT * FROM \"Notes\" WHERE \"Title\" = '-- rls:cross-tenant';";
        Assert.False(RlsSessionInterceptor.HasLeadingCrossTenantTag(sql));
    }

    [Fact]
    public void LeadingLineThatMerelyContainsTheTag_IsIgnored()
    {
        // Exact-line strictness: only the sanctioned tag, not "-- rls:cross-tenant <anything>".
        var sql = $"{Tag} hack\nSELECT 1;";
        Assert.False(RlsSessionInterceptor.HasLeadingCrossTenantTag(sql));
    }

    [Fact]
    public void NoTag_IsIgnored()
    {
        Assert.False(RlsSessionInterceptor.HasLeadingCrossTenantTag("SELECT * FROM \"Notes\";"));
    }
}
