using System.Text.RegularExpressions;

namespace Vuelto.Api.Tests.Architecture;

/// <summary>
/// Pure write-site matcher behind <c>EveryEntity_HasOneWritingSlice</c> (Arch A8, R162) and its self-test
/// (<see cref="SliceWriteInspectorTests"/>). Reads a slice's source and answers which entities it WRITES: an injected
/// <c>IRepository&lt;Entity&gt;</c> (or <c>DbSet&lt;Entity&gt;</c>) on which the code calls a writing member —
/// <c>AddAsync</c>, <c>Add</c>, <c>AddRange</c>, <c>Update</c>, <c>Remove</c>, <c>RemoveRange</c> — or a set-based
/// <c>ExecuteDeleteAsync</c> / <c>ExecuteUpdateAsync</c> chained from its <c>Query()</c> / <c>QueryAllTenants()</c>.
/// Reads (<c>Query()</c> and anything LINQ does with it) are free: a dashboard may read eleven slices' entities; it may
/// write none of them.
/// </summary>
public static class SliceWriteInspector
{
    private static readonly Regex Injected = new(@"(?:IRepository|DbSet)<(?<entity>[A-Za-z_][A-Za-z0-9_]*)>\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.Compiled);
    private const string WriteMembers = "AddAsync|AddRangeAsync|Add|AddRange|Update|UpdateRange|Remove|RemoveRange";

    /// <summary>The entities <paramref name="source"/> writes, with the member it wrote through, in first-seen order.</summary>
    public static IReadOnlyList<(string Entity, string Through)> Writes(string source)
    {
        var text = StripComments(source);
        // Each injected repository (or set) under every name the file gives it: `IRepository<Thing> things`, `_things = things`.
        // A name keeps every entity it is declared with: two subclasses in one file may each call theirs `lines`.
        var names = new List<(string Name, string Entity)>();
        foreach (Match m in Injected.Matches(text))
            if (!names.Contains((m.Groups["name"].Value, m.Groups["entity"].Value))) names.Add((m.Groups["name"].Value, m.Groups["entity"].Value));
        foreach (var (name, entity) in names.ToList())
            foreach (Match alias in Regex.Matches(text, @"\b(?<alias>_?[A-Za-z_][A-Za-z0-9_]*)\s*=\s*" + Regex.Escape(name) + @"\s*;"))
                if (!names.Contains((alias.Groups["alias"].Value, entity))) names.Add((alias.Groups["alias"].Value, entity));

        var writes = new List<(string, string)>();
        void Note(string entity, string through) { if (!writes.Contains((entity, through))) writes.Add((entity, through)); }
        foreach (var (name, entity) in names)
        {
            var escaped = Regex.Escape(name);
            foreach (Match m in Regex.Matches(text, $@"(?<![A-Za-z0-9_]){escaped}\.(?<member>{WriteMembers})\s*\("))
                Note(entity, m.Groups["member"].Value);
            // A set-based write chained from a query of the same repository, within one statement.
            foreach (Match m in Regex.Matches(text, $@"(?<![A-Za-z0-9_]){escaped}\.(?:Query|QueryAllTenants)\s*\([^;]*?\.(?<member>ExecuteDeleteAsync|ExecuteUpdateAsync)\s*\(", RegexOptions.Singleline))
                Note(entity, m.Groups["member"].Value);
        }
        return writes;
    }

    private static string StripComments(string source) =>
        Regex.Replace(Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");
}
