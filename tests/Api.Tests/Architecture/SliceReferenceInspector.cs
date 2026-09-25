using System.Text.RegularExpressions;

namespace Vuelto.Api.Tests.Architecture;

/// <summary>
/// Pure slice-reference matcher behind <c>FeatureFolders_DoNotReferenceEachOthersNamespaces</c> and its
/// self-test (<see cref="SliceReferenceInspectorTests"/>). A slice's namespace counts as referenced only when it
/// is not followed by another identifier character, so <c>Features.Reports2</c> is never read as a reference to
/// <c>Reports</c> (v4 ADV-P4-12), while <c>Features.Reports</c> and <c>Features.Reports.Dto</c> still are.
/// </summary>
public static class SliceReferenceInspector
{
    public static IReadOnlyList<string> ReferencedSlices(string source, IEnumerable<string> otherSlices) =>
        otherSlices
            .Where(s => Regex.IsMatch(source, $@"Vuelto\.Api\.Features\.{Regex.Escape(s)}(?![\p{{L}}\p{{Nd}}_])"))
            .ToList();
}
