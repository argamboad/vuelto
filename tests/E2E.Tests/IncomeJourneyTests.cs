using Microsoft.Playwright;

namespace Vuelto.E2E.Tests;

/// <summary>
/// INCOME-1 journey. A member reaches the income page from Settings, adds their weekly salary as their own and a
/// son's salary paid twice a month, sees both listed with who earns them and how often, moves the second to the top,
/// and deactivates the first; a second line under the first one's name then offers to reactivate it. Then the first
/// transaction of September 2026 creates the month, which starts with one income row per line, derived by its pay
/// period (default Thursday weeks: Aug 27 – Sep 23, four weeks, and the Aug 31 and Sep 15 pay days), and one row is
/// corrected for that month only. Maps to the QA plan's income cases.
/// </summary>
[TestFixture]
public class IncomeJourneyTests : E2ETestBase
{
    private static readonly LocatorAssertionsToBeVisibleOptions Slow = new() { Timeout = 30_000 };

    [Test]
    public async Task Member_Keeps_The_Households_Income_Lines()
    {
        await SignInAsync(Page, UniqueEmail("income"));
        await Page.GotoAsync("/settings");
        await Page.GetByTestId("catalog-link-incomes").ClickAsync();
        await Expect(Page.GetByTestId("inc-empty")).ToBeVisibleAsync(Slow);

        // A weekly salary, the signed-in member's own (the first member in the picker).
        await Page.GetByTestId("inc-new").ClickAsync();
        await Page.GetByTestId("inc-name").FillAsync("My salary");
        var member = await Page.GetByTestId("inc-member").Locator("option").Nth(1).GetAttributeAsync("value");
        await Page.GetByTestId("inc-member").SelectOptionAsync(member!);
        await Page.GetByTestId("inc-period").SelectOptionAsync("weekly");
        await Page.GetByTestId("inc-amount").FillAsync("500");
        await Page.GetByTestId("inc-currency").SelectOptionAsync("USD");
        await Page.GetByTestId("inc-save").ClickAsync();
        await Expect(Page.GetByTestId("inc-notice")).ToBeVisibleAsync(Slow);

        // A household salary paid twice a month, in colones, on the default days.
        await Page.GetByTestId("inc-new").ClickAsync();
        await Page.GetByTestId("inc-name").FillAsync("Son salary");
        await Page.GetByTestId("inc-period").SelectOptionAsync("biweekly");
        await Expect(Page.GetByTestId("inc-day1")).ToHaveValueAsync("15");
        await Page.GetByTestId("inc-amount").FillAsync("300000");
        await Page.GetByTestId("inc-currency").SelectOptionAsync("CRC");
        await Page.GetByTestId("inc-save").ClickAsync();
        await Expect(Page.GetByTestId("inc-row")).ToHaveCountAsync(2, new() { Timeout = 30_000 });

        var names = Page.GetByTestId("inc-row-name");
        await Expect(names).ToHaveTextAsync(["My salary", "Son salary"]);
        await Expect(Page.GetByTestId("inc-row-amount").Nth(1)).ToContainTextAsync("300,000.00");

        // Move the son's line up: the order is saved and the list reloads in it.
        await Page.GetByTestId("inc-up").Nth(1).ClickAsync();
        await Expect(names).ToHaveTextAsync(["Son salary", "My salary"], new() { Timeout = 30_000 });

        // Deactivate the salary, then try to add it again: the page offers to reactivate the stored line.
        await Page.GetByTestId("inc-edit").Nth(1).ClickAsync();
        await Page.GetByTestId("inc-active").UncheckAsync();
        await Page.GetByTestId("inc-save").ClickAsync();
        await Expect(Page.GetByTestId("inc-row").Nth(1).GetByTestId("inc-row-status")).ToBeVisibleAsync(Slow);
        await Expect(Page.GetByTestId("inc-up")).ToHaveCountAsync(1);

        await Page.GetByTestId("inc-new").ClickAsync();
        await Page.GetByTestId("inc-name").FillAsync("my SALARY");
        await Page.GetByTestId("inc-period").SelectOptionAsync("weekly"); // reactivating applies what was typed
        await Page.GetByTestId("inc-amount").FillAsync("550");
        await Page.GetByTestId("inc-save").ClickAsync();
        await Page.GetByTestId("inc-reactivate").ClickAsync(new() { Timeout = 30_000 });
        await Expect(Page.GetByTestId("inc-up")).ToHaveCountAsync(2, new() { Timeout = 30_000 });
        await Expect(names).ToContainTextAsync(["My salary"]);

        // The first transaction of the month creates it, and the month takes its income from the lines.
        await Page.GotoAsync("/transactions/new");
        await Expect(Page.GetByTestId("tx-form")).ToBeVisibleAsync(Slow);
        await Page.GetByTestId("tx-amount-field-input").FillAsync("10000");
        await Page.GetByTestId("tx-date").FillAsync("2026-09-10");
        await Page.GetByTestId("tx-date").DispatchEventAsync("change");
        await Page.GetByTestId("tx-payee").FillAsync("Super");
        await Page.GetByTestId("tx-category").SelectOptionAsync(new SelectOptionValue { Index = 1 });
        await Page.GetByTestId("tx-bank").SelectOptionAsync(new SelectOptionValue { Index = 1 });
        await Page.GetByTestId("tx-rate").FillAsync("500");
        await Page.GetByTestId("tx-save").ClickAsync();

        await Expect(Page.GetByTestId("month-income")).ToBeVisibleAsync(Slow);
        var labels = Page.GetByTestId("month-inc-label");
        await Expect(labels).ToHaveCountAsync(2, new() { Timeout = 30_000 });
        await Expect(labels.Nth(0)).ToHaveValueAsync("Son salary");
        await Expect(labels.Nth(1)).ToHaveValueAsync("My salary");
        var amounts = Page.GetByTestId("month-inc-amount");
        await Expect(amounts.Nth(0)).ToHaveValueAsync("600000.00"); // two pay days × ₡300,000
        await Expect(amounts.Nth(1)).ToHaveValueAsync("2200.00");   // four weeks × $550

        // This month the salary was lower: the correction keeps the plan beside it and survives a reload.
        await amounts.Nth(1).FillAsync("2000");
        await amounts.Nth(1).DispatchEventAsync("change");
        await Expect(Page.GetByTestId("month-inc-planned")).ToBeVisibleAsync();
        await Page.GetByTestId("month-inc-save").ClickAsync();
        await Expect(Page.GetByTestId("month-notice")).ToBeVisibleAsync(Slow);
        await Page.ReloadAsync();
        await Expect(Page.GetByTestId("month-inc-amount").Nth(1)).ToHaveValueAsync("2000.00", new() { Timeout = 30_000 });
        await Expect(Page.GetByTestId("month-inc-planned")).ToBeVisibleAsync();
    }
}
