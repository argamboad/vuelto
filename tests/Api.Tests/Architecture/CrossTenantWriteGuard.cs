using System.Text.RegularExpressions;

namespace Vuelto.Api.Tests.Architecture;

/// <summary>
/// Pure scanner shared by the ban gate (<c>QueryAllTenants_IsNotComposedWithSetBasedWrites</c>) and the test
/// that proves it bites (<see cref="CrossTenantWriteGuardTests"/>). It reports any statement that composes
/// <c>QueryAllTenants()</c> with a set-based write (<c>ExecuteUpdate</c>/<c>ExecuteDelete</c>).
/// <para>
/// v3 audit RLS-4/RLS-8: <c>QueryAllTenants()</c>'s cross-tenant sanction is a query <b>tag</b>, and EF does
/// not reliably render tags into the set-based-write pipeline — it renders for <c>ExecuteDelete</c> but NOT
/// <c>ExecuteUpdate</c> (pinned by <c>RlsBackstopTests</c>), and either could change. So the API reads as
/// sanctioned, compiles, and silently affects the wrong number of rows under the DB RLS policy. Set-based
/// writes must instead scope by <b>entering</b> the target tenant (<c>ITenantContext.EnterTenant</c>) and
/// using the normal <c>Query()</c> / <c>IgnoreQueryFilters()</c>. Cross-tenant <b>reads</b> via
/// <c>QueryAllTenants()</c> are fine and not flagged.
/// </para>
/// </summary>
public static class CrossTenantWriteGuard
{
    public static IReadOnlyList<string> FindOffenders(IEnumerable<(string File, string Text)> sources)
    {
        var offenders = new List<string>();
        foreach (var (file, text) in sources)
        {
            var code = StripComments(text);
            // A composed chain lives within one ;-terminated statement.
            foreach (var statement in code.Split(';'))
            {
                if (statement.Contains("QueryAllTenants", StringComparison.Ordinal)
                    && (statement.Contains("ExecuteUpdate", StringComparison.Ordinal)
                        || statement.Contains("ExecuteDelete", StringComparison.Ordinal)))
                {
                    offenders.Add($"{Path.GetFileName(file) ?? file}: QueryAllTenants() composed with a "
                        + "set-based write — enter the target tenant (EnterTenant) and use Query()/IgnoreQueryFilters (RLS-4)");
                    break; // one report per file is enough
                }
            }
        }
        return offenders;
    }

    // Strip /* */ block and // line comments so a comment that mentions QueryAllTenants next to a set-based
    // write isn't a false positive. Naive (does not parse string literals) — matches the repo's other
    // source-scan guards; it can only cause a rare false negative, never a false positive.
    private static string StripComments(string text)
    {
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline);
        text = Regex.Replace(text, @"//[^\n]*", "");
        return text;
    }
}
