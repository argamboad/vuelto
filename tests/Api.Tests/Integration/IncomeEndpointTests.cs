using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Tests.Integration;

/// <summary>
/// INCOME-1 over HTTP through the real app (migrations, RLS enforced): anonymous is refused; a member keeps the
/// household's income lines (create, 409, reorder, update, 404 for another household, a member from another household
/// is refused); the first transaction of a month snapshots the lines into the month's income rows; the month income
/// endpoint takes the full row list; the dashboard reports the rows and their total.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class IncomeEndpointTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    [Fact]
    public async Task Anonymous_IsRefused()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/incomes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/incomes", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsJsonAsync("/api/incomes/order", new { ordered_ids = Array.Empty<Guid>() })).StatusCode);
    }

    [Fact]
    public async Task Member_KeepsTheLines_AndEveryNewMonthSnapshotsThem()
    {
        var member = await _factory.SeedUserAsync(TenantRoles.Member);
        var client = _factory.CreateClientFor(member);

        var salaryRes = await client.PostAsJsonAsync("/api/incomes", new { name = "Allan salary", member_user_id = member.UserId, currency = "USD", kind = "fixed", pay_period = "weekly", amount = 500m });
        Assert.Equal(HttpStatusCode.Created, salaryRes.StatusCode);
        var salary = (await salaryRes.Content.ReadFromJsonAsync<LineDto>())!;
        Assert.Equal($"/api/incomes/{salary.Id}", salaryRes.Headers.Location!.OriginalString);
        var son = (await (await client.PostAsJsonAsync("/api/incomes", new { name = "Son", currency = "CRC", kind = "fixed", pay_period = "monthly", amount = 600_000m })).Content.ReadFromJsonAsync<LineDto>())!;

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/incomes", new { name = "allan SALARY", currency = "USD", kind = "fixed", pay_period = "weekly", amount = 1m })).StatusCode);
        var outsider = await _factory.SeedUserAsync(TenantRoles.Owner);
        var foreignMember = await client.PostAsJsonAsync("/api/incomes", new { name = "Not ours", member_user_id = outsider.UserId, currency = "USD", kind = "fixed", pay_period = "weekly", amount = 1m });
        Assert.Equal(HttpStatusCode.BadRequest, foreignMember.StatusCode);
        Assert.Equal("invalid_request", (await foreignMember.Content.ReadFromJsonAsync<ErrorDto>())!.Error);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/incomes/order", new { ordered_ids = new[] { son.Id, salary.Id } })).StatusCode);
        Assert.Equal(["Son", "Allan salary"], (await client.GetFromJsonAsync<List<LineDto>>("/api/incomes"))!.Select(l => l.Name));

        // September 2026 under Tuesday weeks anchored on the last Tuesday: Aug 25 – Sep 28, five weeks.
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/budget-settings", new { week_start_weekday = 2, month_anchor = "last_weekday_prev" })).StatusCode);
        var category = (await client.GetFromJsonAsync<List<NamedDto>>("/api/categories"))![0];
        var bank = (await client.GetFromJsonAsync<List<NamedDto>>("/api/banks"))![0];
        var tx = (await (await client.PostAsJsonAsync("/api/transactions", new
        {
            payee = "Super", bank_id = bank.Id, payment_method = "credit_card", original_amount = 10_000m, currency = "CRC",
            transaction_date = "2026-09-10", category_id = category.Id, transaction_type = "budgeted", exchange_rate = 500m,
        })).Content.ReadFromJsonAsync<TxDto>())!;

        var month = (await client.GetFromJsonAsync<MonthDto>($"/api/months/{tx.MonthId}"))!;
        Assert.Equal(5, month.WeekCount);
        Assert.Equal([("Son", "CRC", 600_000m), ("Allan salary", "USD", 2_500m)], month.IncomeRows!.Select(r => (r.Label, r.Currency, r.Amount)));
        Assert.Equal(member.UserId, month.IncomeRows![1].MemberUserId);
        Assert.Equal(salary.Id, month.IncomeRows[1].IncomeLineId);

        // Correct the salary, add a one-off, drop the son's row.
        var put = await client.PutAsJsonAsync($"/api/months/{tx.MonthId}/income", new
        {
            rows = new object[]
            {
                new { id = month.IncomeRows[1].Id, label = "Allan salary", member_user_id = member.UserId, currency = "USD", amount = 2_300m },
                new { label = "Sold the bike", currency = "CRC", amount = 150_000m },
            },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var edited = (await put.Content.ReadFromJsonAsync<MonthDto>())!;
        Assert.Equal([("Allan salary", 2_300m, (decimal?)2_500m), ("Sold the bike", 150_000m, null)], edited.IncomeRows!.Select(r => (r.Label, r.Amount, r.PlannedAmount)));

        // No live provider in the test host: the rate chain falls back to the last transaction's 500 (ADR-V006).
        var summary = (await client.GetFromJsonAsync<DashboardDto>($"/api/months/{tx.MonthId}/summary"))!.Summary!;
        Assert.Equal(["Allan salary", "Sold the bike"], summary.IncomeLines.Select(l => l.Label));
        Assert.Equal((1_300_000m, 2_600m), (summary.IncomeTotal.Crc, summary.IncomeTotal.Usd)); // $2,300 → ₡1,150,000; ₡150,000 → $300

        // Renaming the line never rewrites the month.
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/incomes/{salary.Id}", new { name = "Allan (new job)", member_user_id = member.UserId, currency = "USD", kind = "fixed", pay_period = "weekly", amount = 600m, is_active = true })).StatusCode);
        Assert.Equal("Allan salary", (await client.GetFromJsonAsync<MonthDto>($"/api/months/{tx.MonthId}"))!.IncomeRows![0].Label);
    }

    [Fact]
    public async Task AnotherHouseholdsLine_Is404()
    {
        var owner = _factory.CreateClientFor(await _factory.SeedUserAsync(TenantRoles.Owner));
        var line = (await (await owner.PostAsJsonAsync("/api/incomes", new { name = "Theirs", currency = "USD", kind = "fixed", pay_period = "monthly", amount = 1m })).Content.ReadFromJsonAsync<LineDto>())!;
        var stranger = _factory.CreateClientFor(await _factory.SeedUserAsync(TenantRoles.Owner));

        var res = await stranger.PutAsJsonAsync($"/api/incomes/{line.Id}", new { name = "Mine", currency = "USD", kind = "fixed", pay_period = "monthly", amount = 1m, is_active = true });

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Empty((await stranger.GetFromJsonAsync<List<LineDto>>("/api/incomes?include_inactive=true"))!);
    }

    private sealed record NamedDto([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("name")] string Name);
    private sealed record TxDto([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("month_id")] Guid MonthId);
    private sealed record ErrorDto([property: JsonPropertyName("error")] string Error);
    private sealed record LineDto([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("name")] string Name);
    private sealed record RowDto(
        [property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("income_line_id")] Guid? IncomeLineId,
        [property: JsonPropertyName("label")] string Label, [property: JsonPropertyName("member_user_id")] Guid? MemberUserId,
        [property: JsonPropertyName("currency")] string Currency, [property: JsonPropertyName("amount")] decimal Amount,
        [property: JsonPropertyName("planned_amount")] decimal? PlannedAmount);
    private sealed record MonthDto([property: JsonPropertyName("week_count")] int WeekCount, [property: JsonPropertyName("income_rows")] List<RowDto>? IncomeRows);
    private sealed record MoneyDto([property: JsonPropertyName("crc")] decimal Crc, [property: JsonPropertyName("usd")] decimal Usd);
    private sealed record IncomeLineSummaryDto([property: JsonPropertyName("label")] string Label);
    private sealed record SummaryDto([property: JsonPropertyName("income_lines")] List<IncomeLineSummaryDto> IncomeLines, [property: JsonPropertyName("income_total")] MoneyDto IncomeTotal);
    private sealed record DashboardDto([property: JsonPropertyName("summary")] SummaryDto? Summary);
}
