using Bunit;
using Microsoft.AspNetCore.Components;
using Vuelto.Shared.Ui.Components;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// #205: a card folds to its heading through a real disclosure button, says what it holds while folded, and stays the
/// way it was left on this device — and the card-heavy pages (Settings, Reports, Household, Dashboard) use it.
/// </summary>
public class CollapsibleCardTests : ComponentTestBase
{
    private IRenderedComponent<CollapsibleCard> RenderCard(string? summary = null) => Render<CollapsibleCard>(p => p
        .Add(x => x.Key, "test.card")
        .Add(x => x.Label, "Spend by bank")
        .Add(x => x.Summary, summary)
        .Add(x => x.Head, (RenderFragment)(b => b.AddMarkupContent(0, "<h2>Spend by bank</h2>")))
        .Add(x => x.ChildContent, (RenderFragment)(b => b.AddMarkupContent(0, "<p class=\"inside\">the chart</p>"))));

    [Fact]
    public void OpensExpanded_TheButtonNamesTheCard_AndControlsTheBody()
    {
        var cut = RenderCard();

        var toggle = cut.Find(".ccard-toggle");
        Assert.Equal("true", toggle.GetAttribute("aria-expanded"));
        Assert.Equal("Card_Collapse[Spend by bank]", toggle.GetAttribute("aria-label"));
        var body = cut.Find(".ccard-body");
        Assert.Equal(body.Id, toggle.GetAttribute("aria-controls"));
        Assert.False(body.HasAttribute("hidden"));
        Assert.Equal("button", toggle.TagName.ToLowerInvariant());
    }

    [Fact]
    public void Toggling_HidesTheBody_ShowsTheSummary_AndRemembersItOnTheDevice()
    {
        var cut = RenderCard(summary: "4 banks · ₡312,000");
        Assert.Empty(cut.FindAll(".ccard-summary")); // only while folded

        cut.Find(".ccard-toggle").Click();

        Assert.Equal("false", cut.Find(".ccard-toggle").GetAttribute("aria-expanded"));
        Assert.True(cut.Find(".ccard-body").HasAttribute("hidden"));
        Assert.Equal("4 banks · ₡312,000", cut.Find(".ccard-summary").TextContent);
        Assert.Equal("Card_Expand[Spend by bank]", cut.Find(".ccard-toggle").GetAttribute("aria-label"));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "appUi.setPref" && Equals(i.Arguments[0], "collapse.test.card") && Equals(i.Arguments[1], "1"));

        cut.Find(".ccard-toggle").Click();
        Assert.False(cut.Find(".ccard-body").HasAttribute("hidden"));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "appUi.setPref" && Equals(i.Arguments[1], "0"));
    }

    [Fact]
    public void AFoldedCard_OpensFolded_NextTime()
    {
        JSInterop.Setup<string?>("appUi.getPref", "collapse.test.card").SetResult("1");

        var cut = RenderCard();

        Assert.Equal("false", cut.Find(".ccard-toggle").GetAttribute("aria-expanded"));
        Assert.True(cut.Find(".ccard-body").HasAttribute("hidden"));
    }

    [Fact]
    public async Task Settings_FoldsItsCards()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/auth/logins", "[]");
        Http.On(HttpMethod.Get, "/api/auth/providers", "[]");
        var cut = Render<Settings>();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='preferences-card'] .ccard-toggle")));
        Assert.Contains(cut.FindAll(".ccard"), c => c.GetAttribute("data-key") == "settings.danger");
        Assert.Contains(cut.FindAll(".ccard"), c => c.GetAttribute("data-key") == "settings.budget");
        Assert.Contains(cut.FindAll(".ccard"), c => c.GetAttribute("data-key") == "settings.mfa");
    }
}
