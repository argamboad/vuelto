namespace Perezosoft.Infrastructure.Persistence;

/// <summary>
/// EF query tags that the RLS session interceptor (<see cref="RlsSessionInterceptor"/>) recognizes.
/// Tags render as a leading SQL comment, so they survive deferred execution and reach the database
/// command — which lets the sanctioned cross-tenant escape hatches (ADR-003) declare themselves to
/// the row-level-security backstop (ADR-020) per query, instead of per connection or per request.
/// </summary>
public static class RlsTags
{
    /// <summary>
    /// Marks a query as a sanctioned cross-tenant read (<c>QueryAllTenants()</c> /
    /// the enumerated <c>IgnoreQueryFilters()</c> sites in Infrastructure). The RLS
    /// session interceptor sets the bypass GUC for commands carrying this tag; untagged
    /// queries stay subject to the tenant policy even when they escaped the EF filter.
    /// </summary>
    public const string CrossTenant = "rls:cross-tenant";
}
