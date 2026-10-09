using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;

namespace Vuelto.E2E.Tests;

/// <summary>
/// #209: on a phone no main page scrolls sideways. A mobile browser that meets content wider than the screen widens
/// the whole layout, so one wide table made every page feel broken ("terrible table overflow"). With a household that
/// has real rows — two months of purchases, a refund pending and one received, budget lines and income, seeded through
/// the API at a fixed rate so the run never waits on the live rate — each page is opened at 375×740 and its document
/// must be no wider than the screen. A table may still scroll inside its own card; the page may not.
/// </summary>
[TestFixture]
public class PhoneLayoutTests : E2ETestBase
{
    private static readonly LocatorAssertionsToBeVisibleOptions Slow = new() { Timeout = 30_000 };

    [Test]
    public async Task OnAPhone_NoMainPageScrollsSideways()
    {
        var email = UniqueEmail("phone-layout");
        await Mailpit.ClearAsync();
        await SignInAsync(Page, email);
        var monthId = await SeedAsync(email);

        await Page.SetViewportSizeAsync(375, 740);
        string[] pages = ["/dashboard", $"/months/{monthId}", "/months", "/budget", "/refunds", "/reports", "/settings", "/household", "/review", "/transactions/new"];
        foreach (var path in pages)
        {
            await BlazorBoot.GotoAsync(Page, path);
            await Expect(Page.Locator("main h1, main h2").First).ToBeVisibleAsync(Slow);
            await Page.WaitForTimeoutAsync(500); // the cards fill after the shell renders
            var widths = await Page.EvaluateAsync<int[]>("() => [document.documentElement.scrollWidth, document.documentElement.clientWidth]");
            Assert.That(widths[0], Is.LessThanOrEqualTo(widths[1] + 1), $"{path} is {widths[0]}px wide on a {widths[1]}px phone — something pushes the page sideways");
        }

        // The dashboard's four cuts are one panel; each must fit too.
        await BlazorBoot.GotoAsync(Page, "/dashboard");
        foreach (var cut in new[] { "week", "bank", "card", "other" })
        {
            await Page.Locator($"[data-testid='dash-breakdown-switch-option'][data-value='{cut}']").CheckAsync(new() { Force = true });
            var widths = await Page.EvaluateAsync<int[]>("() => [document.documentElement.scrollWidth, document.documentElement.clientWidth]");
            Assert.That(widths[0], Is.LessThanOrEqualTo(widths[1] + 1), $"the dashboard's {cut} cut pushes the page sideways");
        }
    }

    /// <summary>A second session for the same person (an OTP over the API), and the household's rows through it.</summary>
    private static async Task<string> SeedAsync(string email)
    {
        using var api = NewApiClient();
        await Mailpit.ClearAsync(); // the browser's sign-in code is already spent; wait for this session's own
        (await api.PostAsJsonAsync("/api/auth/otp/send", new { email })).EnsureSuccessStatusCode();
        var code = await Mailpit.WaitForOtpAsync(email, TimeSpan.FromSeconds(60));
        var verify = await api.PostAsJsonAsync("/api/auth/otp/verify", new { email, code });
        verify.EnsureSuccessStatusCode();
        var token = (await verify.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var categories = await api.GetFromJsonAsync<JsonElement>("/api/categories");
        var banks = await api.GetFromJsonAsync<JsonElement>("/api/banks");
        string Cat(int i) => categories[i].GetProperty("id").GetString()!;
        var bank = banks[0].GetProperty("id").GetString()!;

        (await api.PostAsJsonAsync("/api/incomes", new { name = "Salario", currency = "USD", kind = "fixed", pay_period = "weekly", amount = 750 })).EnsureSuccessStatusCode();
        (await api.PostAsJsonAsync("/api/expenses/fixed", new { name = "Hipoteca de la casa principal", category_id = Cat(0), budget_crc = 450000, budget_usd = 0, payment_method = "bank_account" })).EnsureSuccessStatusCode();
        (await api.PostAsJsonAsync("/api/expenses/variable", new { name = "Supermercado del mes", category_id = Cat(1), budget_crc = 250000, budget_usd = 0, payment_method = "credit_card" })).EnsureSuccessStatusCode();

        string? monthId = null;
        async Task Tx(string payee, decimal amount, string currency, string date, string cls, string category, decimal? refund = null)
        {
            var res = await api.PostAsJsonAsync("/api/transactions", new
            {
                payee, bank_id = bank, payment_method = "credit_card", original_amount = amount, currency, transaction_date = date,
                category_id = category, transaction_type = cls, exchange_rate = 505.25m, refund_expected = refund is not null, refund_amount = refund,
            });
            res.EnsureSuccessStatusCode();
            monthId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("month_id").GetString();
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        await Tx("Hospital Clínica Bíblica — consulta especialista", 1_285_000m, "CRC", today.AddMonths(-1).ToString("yyyy-MM-dd"), "unplanned_essential", Cat(2), refund: 960_000m);
        await Tx("AutoMercado Plaza del Sol Escazú", 164_250.5m, "CRC", today.AddMonths(-1).AddDays(2).ToString("yyyy-MM-dd"), "budgeted", Cat(1));
        await Tx("Regalo cumpleaños (Amazon)", 1_120m, "USD", today.AddDays(-3).ToString("yyyy-MM-dd"), "extraordinary", Cat(2), refund: 400m);
        await Tx("Pago hipoteca", 450_000m, "CRC", today.AddDays(-2).ToString("yyyy-MM-dd"), "budgeted", Cat(0));
        await Tx("Farmacia Fischel", 23_000m, "CRC", today.AddDays(-1).ToString("yyyy-MM-dd"), "unplanned_essential", Cat(2), refund: 23_000m);

        var refunds = await api.GetFromJsonAsync<JsonElement>("/api/refunds");
        var first = refunds.GetProperty("refunds")[0].GetProperty("id").GetString();
        (await api.PutAsJsonAsync($"/api/refunds/{first}", new { status = "received", received_date = today.ToString("yyyy-MM-dd") })).EnsureSuccessStatusCode();
        return monthId!;
    }
}
