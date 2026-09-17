using Bunit;
using Vuelto.Shared.Ui.Components;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>BUDGET-1 card: renders what the API returns, saves the wire-shaped body, and surfaces failures. Income left for its own page (INCOME-1).</summary>
public class BudgetSettingsCardTests : ComponentTestBase
{
    private const string Saved = """
        {"week_start_weekday":1,"month_anchor":"first_of_month","is_default":false,"updated_at":"2026-09-02T12:00:00Z"}
        """;

    [Fact]
    public async Task Renders_TheHouseholdsSavedSettings()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/budget-settings", Saved);

        var cut = Render<BudgetSettingsCard>();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("1", cut.Find("[data-testid='budget-weekday']").GetAttribute("value"));
            Assert.Equal("first_of_month", cut.Find("[data-testid='budget-anchor']").GetAttribute("value"));
        });
        // No income fields any more: a hint about the payday and a link to the income page instead.
        Assert.Empty(cut.FindAll("input[type='number']"));
        Assert.Contains("BudgetSettings_WeekStartHint", cut.Find("[data-testid='budget-weekday-hint']").TextContent);
        Assert.Equal("/incomes", cut.Find("[data-testid='budget-income-link'] a").GetAttribute("href"));
        // The anchor options are labelled with the selected weekday's name (Monday for 1).
        Assert.Contains("Monday", cut.Find("[data-testid='budget-anchor']").TextContent);
    }

    [Fact]
    public async Task Save_PutsTheSnakeCaseBody_AndConfirms()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/budget-settings", Saved);
        Http.On(HttpMethod.Put, "/api/budget-settings", Saved);

        var cut = Render<BudgetSettingsCard>();
        cut.WaitForElement("[data-testid='budget-save']");
        cut.Find("[data-testid='budget-weekday']").Change("5");
        cut.Find("[data-testid='budget-anchor']").Change("last_weekday_prev");
        cut.Find("[data-testid='budget-save']").Click();

        cut.WaitForElement("[data-testid='budget-saved']");
        var put = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put);
        var body = await put.Content!.ReadAsStringAsync();
        Assert.Contains("\"week_start_weekday\":5", body);
        Assert.Contains("\"month_anchor\":\"last_weekday_prev\"", body);
        Assert.DoesNotContain("income", body);
    }

    [Fact]
    public async Task Save_400_ShowsTheInvalidMessage_NotAGenericFailure()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/budget-settings", Saved);
        Http.On(HttpMethod.Put, "/api/budget-settings", """{"error":"invalid_request","message":"x"}""", System.Net.HttpStatusCode.BadRequest);

        var cut = Render<BudgetSettingsCard>();
        cut.WaitForElement("[data-testid='budget-save']").Click();

        var error = cut.WaitForElement("[data-testid='budget-error']");
        Assert.Contains("BudgetSettings_ErrInvalid", error.TextContent);
        Assert.Empty(cut.FindAll("[data-testid='budget-saved']"));
    }

    [Fact]
    public async Task LoadFailure_ShowsTheLoadError()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/budget-settings", "{}", System.Net.HttpStatusCode.InternalServerError);

        var cut = Render<BudgetSettingsCard>();

        Assert.Contains("BudgetSettings_ErrLoad", cut.WaitForElement("[data-testid='budget-error']").TextContent);
    }
}
