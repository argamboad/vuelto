namespace Vuelto.Api.Tests.Architecture;

/// <summary>
/// Proves the slice-isolation scan (<see cref="SliceReferenceInspector"/>, used by
/// <c>FeatureFolders_DoNotReferenceEachOthersNamespaces</c>) matches a slice's namespace on a boundary. v4 audit
/// finding ADV-P4-12: a plain substring match read <c>Features.Reports2</c> as a reference to <c>Reports</c>, so
/// two slices whose names share a prefix failed the gate with no violation between them.
/// </summary>
public class SliceReferenceInspectorTests
{
    [Fact]
    public void ASliceWhoseNameExtendsAnother_IsNotAReferenceToIt()
    {
        const string reports2 = """
            namespace Vuelto.Api.Features.Reports2;
            using Vuelto.Api.Features.Reports2.Dto;
            """;

        Assert.Empty(SliceReferenceInspector.ReferencedSlices(reports2, ["Reports"]));
    }

    [Theory]
    [InlineData("using Vuelto.Api.Features.Reports;")]
    [InlineData("using Vuelto.Api.Features.Reports.Dto;")]
    [InlineData("var x = new Vuelto.Api.Features.Reports.ReportRow();")]
    [InlineData("using static Vuelto.Api.Features.Reports.ReportEndpoints;")]
    public void ARealReference_IsStillCaught(string line)
    {
        var source = "namespace Vuelto.Api.Features.Reports2;\n" + line;

        Assert.Equal(["Reports"], SliceReferenceInspector.ReferencedSlices(source, ["Reports"]));
    }

    [Fact]
    public void AShorterSlice_ReferencingTheLongerOne_IsCaught()
    {
        const string reports = """
            namespace Vuelto.Api.Features.Reports;
            using Vuelto.Api.Features.Reports2;
            """;

        Assert.Equal(["Reports2"], SliceReferenceInspector.ReferencedSlices(reports, ["Reports2"]));
    }
}
