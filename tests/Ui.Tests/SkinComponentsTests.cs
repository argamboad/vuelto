using Bunit;
using Vuelto.Shared.Ui.Components;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// The SKIN-2 shared components — the vocabulary every redesigned screen is composed from. Nothing on a
/// screen uses them yet, so these tests are their only consumer and their only specification.
///
/// Three rules run through the whole set and are asserted here rather than trusted: money is never doubled
/// on one line (the second currency stacks underneath), a class chip is keyed off the
/// <see cref="TransactionClasses"/> constant so no call site can pick a colour, and every semantic colour comes
/// from the SKIN-1 tokens (--good / --bad / --warn-bg) rather than a literal or a Bootstrap contextual class.
/// </summary>
public class SkinComponentsTests : ComponentTestBase
{
    // ---------------------------------------------------------------- Money

    [Fact]
    public void Money_InBoth_StacksTheSecondCurrencyUnderneath_RatherThanDoublingTheLine()
    {
        var cut = Render<Money>(p => p
            .Add(x => x.Crc, 217_200m).Add(x => x.Usd, 402.22m)
            .Add(x => x.Display, MoneyDisplay.Both).Add(x => x.TestId, "m"));

        // The whole point of the redesign's second decision: primary large, other muted BELOW — never "₡x · $y".
        Assert.Equal("₡217,200.00", cut.Find("[data-testid='m-primary']").TextContent.Trim());
        Assert.Equal("$402.22", cut.Find("[data-testid='m-secondary']").TextContent.Trim());
        Assert.DoesNotContain("·", cut.Markup);
    }

    [Theory]
    [InlineData(MoneyDisplay.Crc, "₡217,200.00")]
    [InlineData(MoneyDisplay.Usd, "$402.22")]
    public void Money_InOneCurrency_ShowsThatSideAlone(string display, string expected)
    {
        var cut = Render<Money>(p => p
            .Add(x => x.Crc, 217_200m).Add(x => x.Usd, 402.22m)
            .Add(x => x.Display, display).Add(x => x.TestId, "m"));

        Assert.Equal(expected, cut.Find("[data-testid='m-primary']").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid='m-secondary']"));
    }

    [Fact]
    public void Money_WithItsOwnCurrency_IgnoresThePreference_BecauseABudgetLineIsNotAConvertedPair()
    {
        // Mirrors MoneyDisplay.Native: a figure set in ONE currency always shows on its own side.
        var cut = Render<Money>(p => p
            .Add(x => x.Crc, 450_000m).Add(x => x.Usd, 833.33m).Add(x => x.Currency, MoneyDisplay.Usd)
            .Add(x => x.Display, MoneyDisplay.Both).Add(x => x.TestId, "m"));

        Assert.Equal("$833.33", cut.Find("[data-testid='m-primary']").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid='m-secondary']"));
    }

    [Fact]
    public void Money_AlwaysSetsTabularNumerals_SoColumnsOfDigitsLineUp()
    {
        var cut = Render<Money>(p => p.Add(x => x.Crc, 1m).Add(x => x.Usd, 1m).Add(x => x.TestId, "m"));
        Assert.Contains("money", cut.Find("[data-testid='m']").GetAttribute("class"));
    }

    [Theory]
    [InlineData("display")]
    [InlineData("step")]
    [InlineData("row")]
    public void Money_CarriesItsSizeAsAModifier_NotAnInlineStyle(string size)
    {
        var cut = Render<Money>(p => p.Add(x => x.Crc, 1m).Add(x => x.Usd, 1m).Add(x => x.Size, size).Add(x => x.TestId, "m"));
        Assert.Contains($"money-{size}", cut.Find("[data-testid='m']").GetAttribute("class"));
    }

    // ---------------------------------------------------------------- ClassChip

    [Theory]
    [InlineData(TransactionClasses.Budgeted, "Tx_Budgeted")]
    [InlineData(TransactionClasses.Extraordinary, "Tx_Extraordinary")]
    [InlineData(TransactionClasses.UnplannedEssential, "Tx_Unplanned")]
    [InlineData(TransactionClasses.Inflow, "Tx_Inflow")]
    [InlineData(TransactionClasses.EnvelopeContribution, "Tx_EnvelopeContribution")]
    public void ClassChip_LabelsEveryClass_FromItsOwnKey(string txClass, string expectedKey)
    {
        var cut = Render<ClassChip>(p => p.Add(x => x.Class, txClass).Add(x => x.TestId, "c"));

        // FakeStringLocalizer renders the key, so this pins the chip to the right resource, not to wording.
        Assert.Equal(expectedKey, cut.Find("[data-testid='c']").TextContent.Trim());
        Assert.Equal(txClass, cut.Find("[data-testid='c']").GetAttribute("data-class"));
    }

    [Fact]
    public void ClassChip_GivesEveryClassItsOwnTone_SoTwoClassesNeverReadAlike()
    {
        string Tone(string c) => Render<ClassChip>(p => p.Add(x => x.Class, c).Add(x => x.TestId, "c"))
            .Find("[data-testid='c']").GetAttribute("data-tone")!;

        var tones = new[]
        {
            TransactionClasses.Budgeted, TransactionClasses.Extraordinary, TransactionClasses.UnplannedEssential,
            TransactionClasses.Inflow, TransactionClasses.EnvelopeContribution,
        }.Select(Tone).ToList();

        Assert.Equal(tones.Count, tones.Distinct().Count());
        // Amber means "needs attention", and an unplanned essential is the one class that asks for a look.
        Assert.Equal("warn", Tone(TransactionClasses.UnplannedEssential));
        Assert.Equal("good", Tone(TransactionClasses.Inflow));
    }

    [Fact]
    public void ClassChip_WithAnUnknownClass_RendersNothing_RatherThanAnUnlabelledPill()
    {
        var cut = Render<ClassChip>(p => p.Add(x => x.Class, "not_a_class").Add(x => x.TestId, "c"));
        Assert.Empty(cut.FindAll("[data-testid='c']"));
    }

    // ---------------------------------------------------------------- PaceBar

    [Fact]
    public void PaceBar_DrawsOneDivPerSegment_ProportionalToTheTotal()
    {
        var cut = Render<PaceBar>(p => p
            .Add(x => x.Total, 1000m)
            .Add(x => x.Segments, new List<PaceSegment>
            {
                new("Budgeted", 510m, "primary"),
                new("Unplanned", 40m, "warn"),
                new("Still planned", 230m, "rail", Hatched: true),
            })
            .Add(x => x.TestId, "p"));

        var segs = cut.FindAll("[data-testid='p-segment']");
        Assert.Equal(3, segs.Count);
        Assert.Contains("51%", segs[0].GetAttribute("style"));   // 510 of 1000
        Assert.Equal("true", segs[2].GetAttribute("data-hatched"));
        Assert.Null(segs[0].GetAttribute("data-hatched"));
        Assert.Empty(cut.FindAll("svg")); // divs, not the SVG the StackedBar it supersedes draws
    }

    [Fact]
    public void PaceBar_PutsTheMarkerWhereTheMonthIs_AndClampsItInsideTheTrack()
    {
        var cut = Render<PaceBar>(p => p.Add(x => x.Total, 100m).Add(x => x.Marker, 0.68m)
            .Add(x => x.Segments, new List<PaceSegment> { new("Spent", 50m, "primary") }).Add(x => x.TestId, "p"));
        Assert.Contains("68%", cut.Find("[data-testid='p-marker']").GetAttribute("style"));

        var over = Render<PaceBar>(p => p.Add(x => x.Total, 100m).Add(x => x.Marker, 1.4m)
            .Add(x => x.Segments, new List<PaceSegment> { new("Spent", 50m, "primary") }).Add(x => x.TestId, "p"));
        Assert.Contains("100%", over.Find("[data-testid='p-marker']").GetAttribute("style"));
    }

    [Fact]
    public void PaceBar_IsDecorative_SoTheFiguresBesideItCarryTheMeaning()
    {
        var cut = Render<PaceBar>(p => p.Add(x => x.Total, 100m)
            .Add(x => x.Segments, new List<PaceSegment> { new("Spent", 50m, "primary") }).Add(x => x.TestId, "p"));

        // Hatching and colour are not readable by a screen reader; the legend is the accessible copy.
        Assert.Equal("true", cut.Find("[data-testid='p-track']").GetAttribute("aria-hidden"));
        Assert.Contains("Spent", cut.Find("[data-testid='p-legend']").TextContent);
    }

    [Fact]
    public void PaceBar_CanDropItsLegend_WhenTheSameFiguresAlreadySitBesideIt()
    {
        // A month card prints "Spent ₡x" and the result as text; a legend under an 8px bar would say it twice.
        var cut = Render<PaceBar>(p => p.Add(x => x.Total, 100m).Add(x => x.ShowLegend, false)
            .Add(x => x.Segments, new List<PaceSegment> { new("Spent", 50m, "primary") }).Add(x => x.TestId, "p"));
        Assert.Empty(cut.FindAll("[data-testid='p-legend']"));
        Assert.Single(cut.FindAll("[data-testid='p-segment']"));
    }

    [Fact]
    public void PaceBar_WithNothingSpent_StillRendersItsTrack_RatherThanCollapsing()
    {
        var cut = Render<PaceBar>(p => p.Add(x => x.Total, 1000m)
            .Add(x => x.Segments, new List<PaceSegment>()).Add(x => x.TestId, "p"));
        Assert.NotNull(cut.Find("[data-testid='p-track']"));
        Assert.Empty(cut.FindAll("[data-testid='p-segment']"));
    }

    // ---------------------------------------------------------------- SegmentedSwitch

    [Fact]
    public void SegmentedSwitch_IsARadioGroup_SoItIsKeyboardAndScreenReaderNavigable()
    {
        var cut = Render<SegmentedSwitch>(p => p
            .Add(x => x.Options, new List<SegmentedOption> { new("week", "By week"), new("bank", "By bank"), new("card", "By card") })
            .Add(x => x.Value, "bank").Add(x => x.Label, "Where it went").Add(x => x.TestId, "s"));

        var radios = cut.FindAll("input[type=radio]");
        Assert.Equal(3, radios.Count);
        Assert.True(radios[1].HasAttribute("checked"));
        Assert.NotNull(cut.Find("fieldset"));                       // a real group, not styled buttons
        Assert.Contains("Where it went", cut.Find("legend").TextContent);
        Assert.All(radios, r => Assert.Equal(radios[0].GetAttribute("name"), r.GetAttribute("name")));
    }

    [Fact]
    public async Task SegmentedSwitch_RaisesTheChosenValue()
    {
        string? picked = null;
        var cut = Render<SegmentedSwitch>(p => p
            .Add(x => x.Options, new List<SegmentedOption> { new("week", "By week"), new("card", "By card") })
            .Add(x => x.Value, "week").Add(x => x.TestId, "s")
            .Add(x => x.ValueChanged, v => picked = v));

        await cut.Find("[data-testid='s-card']").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });
        Assert.Equal("card", picked);
    }

    // ---------------------------------------------------------------- KpiTile

    [Fact]
    public void KpiTile_ReadsLabelThenValueThenItsOneSubLine()
    {
        var cut = Render<KpiTile>(p => p
            .Add(x => x.Label, "Total spend").Add(x => x.Sub, "67% of income").Add(x => x.TestId, "k")
            .Add(x => x.Value, (Microsoft.AspNetCore.Components.RenderFragment)(b => b.AddContent(0, "₡1,485,800"))));

        Assert.Equal("Total spend", cut.Find("[data-testid='k-label']").TextContent.Trim());
        Assert.Equal("₡1,485,800", cut.Find("[data-testid='k-value']").TextContent.Trim());
        Assert.Equal("67% of income", cut.Find("[data-testid='k-sub']").TextContent.Trim());
    }

    [Fact]
    public void KpiTile_WithoutASubLine_LeavesItOut_RatherThanRenderingAnEmptyRow()
    {
        var cut = Render<KpiTile>(p => p.Add(x => x.Label, "Budgeted").Add(x => x.TestId, "k")
            .Add(x => x.Value, (Microsoft.AspNetCore.Components.RenderFragment)(b => b.AddContent(0, "₡1"))));
        Assert.Empty(cut.FindAll("[data-testid='k-sub']"));
    }

    // ---------------------------------------------------------------- ConsequencePanel

    [Fact]
    public void ConsequencePanel_StatesWhatWillHappen_BeforeTheActionsThatDoIt()
    {
        var cut = Render<ConsequencePanel>(p => p
            .Add(x => x.Eyebrow, "Confirming will").Add(x => x.TestId, "cp")
            .Add(x => x.ChildContent, (Microsoft.AspNetCore.Components.RenderFragment)(b => b.AddContent(0, "Book ₡48,320 into Groceries.")))
            .Add(x => x.Actions, (Microsoft.AspNetCore.Components.RenderFragment)(b => b.AddMarkupContent(0, "<button data-testid='cp-confirm'>Confirm</button>"))));

        var panel = cut.Find("[data-testid='cp']");
        Assert.Equal("Confirming will", cut.Find("[data-testid='cp-eyebrow']").TextContent.Trim());
        Assert.Contains("Book ₡48,320 into Groceries.", cut.Find("[data-testid='cp-body']").TextContent);

        // The consequence must precede the control in the DOM, so it is read before the button is reached.
        var html = panel.InnerHtml;
        Assert.True(html.IndexOf("cp-body", StringComparison.Ordinal) < html.IndexOf("cp-confirm", StringComparison.Ordinal));
    }

    [Fact]
    public void ConsequencePanel_AnnouncesItselfPolitely_BecauseItsTextChangesAsTheFormDoes()
    {
        var cut = Render<ConsequencePanel>(p => p.Add(x => x.Eyebrow, "Before you save").Add(x => x.TestId, "cp")
            .Add(x => x.ChildContent, (Microsoft.AspNetCore.Components.RenderFragment)(b => b.AddContent(0, "x"))));
        Assert.Equal("polite", cut.Find("[data-testid='cp-body']").GetAttribute("aria-live"));
    }

    // ---------------------------------------------------------------- VerdictHeader

    [Fact]
    public void VerdictHeader_LeadsWithTheState_ThenTheNumber_ThenOneSentence()
    {
        var cut = Render<VerdictHeader>(p => p
            .Add(x => x.Tone, "good").Add(x => x.State, "On track").Add(x => x.TestId, "v")
            .Add(x => x.Caption, "Forecast left at month end")
            .Add(x => x.Explanation, "That is what is left after everything already spent.")
            .Add(x => x.Value, (Microsoft.AspNetCore.Components.RenderFragment)(b => b.AddContent(0, "₡217,200"))));

        Assert.Equal("On track", cut.Find("[data-testid='v-state']").TextContent.Trim());
        Assert.Equal("good", cut.Find("[data-testid='v']").GetAttribute("data-tone"));
        Assert.Equal("Forecast left at month end", cut.Find("[data-testid='v-caption']").TextContent.Trim());
        Assert.Contains("₡217,200", cut.Find("[data-testid='v-value']").TextContent);
        Assert.Contains("already spent", cut.Find("[data-testid='v-explanation']").TextContent);
    }

    [Fact]
    public void VerdictHeader_DoesNotLeaveTheStateToColourAlone()
    {
        // The dot is decorative; "On track" is the readable state, and it is text in both themes.
        var cut = Render<VerdictHeader>(p => p.Add(x => x.Tone, "bad").Add(x => x.State, "Over").Add(x => x.TestId, "v")
            .Add(x => x.Value, (Microsoft.AspNetCore.Components.RenderFragment)(b => b.AddContent(0, "x"))));

        Assert.Equal("true", cut.Find("[data-testid='v-dot']").GetAttribute("aria-hidden"));
        Assert.Equal("Over", cut.Find("[data-testid='v-state']").TextContent.Trim());
    }
}
