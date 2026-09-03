using System.Text.Json.Serialization;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Features.Dashboard;

// DASH-1 read shapes (snake_case, ADR-V012). One nested object per figure: { crc, usd }.

public record MoneyPairResponse([property: JsonPropertyName("crc")] decimal Crc, [property: JsonPropertyName("usd")] decimal Usd)
{
    public static MoneyPairResponse From(MoneyPair p) => new(p.Crc, p.Usd);
}

public record ExpenseLineResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("budget")] MoneyPairResponse Budget,
    [property: JsonPropertyName("actual")] MoneyPairResponse Actual);

public record WeeklyTotalResponse(
    [property: JsonPropertyName("week_number")] int WeekNumber,
    [property: JsonPropertyName("start_date")] DateOnly StartDate,
    [property: JsonPropertyName("end_date")] DateOnly EndDate,
    [property: JsonPropertyName("total")] MoneyPairResponse Total);

public record EnvelopeReminderResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("annual_target")] MoneyPairResponse AnnualTarget,
    [property: JsonPropertyName("contributed_this_month")] MoneyPairResponse ContributedThisMonth,
    [property: JsonPropertyName("remaining")] MoneyPairResponse Remaining,
    [property: JsonPropertyName("cadence")] string Cadence);

public record CategorySpendResponse(
    [property: JsonPropertyName("category_name")] string CategoryName,
    [property: JsonPropertyName("actual")] MoneyPairResponse Actual);

public record BankMethodBreakdownResponse(
    [property: JsonPropertyName("bank_id")] Guid? BankId,
    [property: JsonPropertyName("bank_name")] string BankName,
    [property: JsonPropertyName("payment_method")] string PaymentMethod,
    [property: JsonPropertyName("budget")] MoneyPairResponse Budget,
    [property: JsonPropertyName("actual")] MoneyPairResponse Actual);

public record DashboardSummaryResponse(
    [property: JsonPropertyName("income_primary")] MoneyPairResponse IncomePrimary,
    [property: JsonPropertyName("income_secondary")] MoneyPairResponse IncomeSecondary,
    [property: JsonPropertyName("income_total")] MoneyPairResponse IncomeTotal,
    [property: JsonPropertyName("expenses_card")] MoneyPairResponse ExpensesCard,
    [property: JsonPropertyName("expenses_account")] MoneyPairResponse ExpensesAccount,
    [property: JsonPropertyName("expenses_total")] MoneyPairResponse ExpensesTotal,
    [property: JsonPropertyName("expenses_remainder")] MoneyPairResponse ExpensesRemainder,
    [property: JsonPropertyName("fixed_expenses")] IReadOnlyList<ExpenseLineResponse> FixedExpenses,
    [property: JsonPropertyName("variable_expenses")] IReadOnlyList<ExpenseLineResponse> VariableExpenses,
    [property: JsonPropertyName("other_spending")] IReadOnlyList<CategorySpendResponse> OtherSpending,
    [property: JsonPropertyName("weekly_budgeted")] IReadOnlyList<WeeklyTotalResponse> WeeklyBudgeted,
    [property: JsonPropertyName("weekly_extraordinary")] IReadOnlyList<WeeklyTotalResponse> WeeklyExtraordinary,
    [property: JsonPropertyName("current_balance")] MoneyPairResponse CurrentBalance,
    [property: JsonPropertyName("remainder_for_debts")] MoneyPairResponse RemainderForDebts,
    [property: JsonPropertyName("pending_budgeted")] MoneyPairResponse PendingBudgeted,
    [property: JsonPropertyName("actual_remainder")] MoneyPairResponse ActualRemainder,
    [property: JsonPropertyName("unplanned_essential_total")] MoneyPairResponse UnplannedEssentialTotal,
    [property: JsonPropertyName("refunds_total")] MoneyPairResponse RefundsTotal,
    [property: JsonPropertyName("envelope_reminders")] IReadOnlyList<EnvelopeReminderResponse> EnvelopeReminders,
    [property: JsonPropertyName("bank_method_breakdown")] IReadOnlyList<BankMethodBreakdownResponse> BankMethodBreakdown)
{
    public static DashboardSummaryResponse From(DashboardSummary s) => new(
        MoneyPairResponse.From(s.Income.Primary), MoneyPairResponse.From(s.Income.Secondary), MoneyPairResponse.From(s.Income.Total),
        MoneyPairResponse.From(s.Expenses.Card), MoneyPairResponse.From(s.Expenses.Account), MoneyPairResponse.From(s.Expenses.GrandTotal), MoneyPairResponse.From(s.Expenses.Remainder),
        s.FixedExpenses.Select(l => new ExpenseLineResponse(l.Name, MoneyPairResponse.From(l.Budget), MoneyPairResponse.From(l.Actual))).ToList(),
        s.VariableExpenses.Select(l => new ExpenseLineResponse(l.Name, MoneyPairResponse.From(l.Budget), MoneyPairResponse.From(l.Actual))).ToList(),
        s.OtherSpending.Select(c => new CategorySpendResponse(c.CategoryName, MoneyPairResponse.From(c.Actual))).ToList(),
        s.WeeklyBudgeted.Select(w => new WeeklyTotalResponse(w.WeekNumber, w.StartDate, w.EndDate, MoneyPairResponse.From(w.Total))).ToList(),
        s.WeeklyExtraordinary.Select(w => new WeeklyTotalResponse(w.WeekNumber, w.StartDate, w.EndDate, MoneyPairResponse.From(w.Total))).ToList(),
        MoneyPairResponse.From(s.Balance.CurrentBalance), MoneyPairResponse.From(s.Balance.RemainderForDebts), MoneyPairResponse.From(s.Balance.PendingBudgeted), MoneyPairResponse.From(s.Balance.ActualRemainder),
        MoneyPairResponse.From(s.UnplannedEssentialTotal), MoneyPairResponse.From(s.RefundsTotal),
        s.EnvelopeReminders.Select(e => new EnvelopeReminderResponse(e.Name, MoneyPairResponse.From(e.AnnualTarget), MoneyPairResponse.From(e.ContributedThisMonth), MoneyPairResponse.From(e.Remaining), e.Cadence)).ToList(),
        s.BankMethodBreakdown.Select(b => new BankMethodBreakdownResponse(b.BankId, b.BankName, b.PaymentMethod, MoneyPairResponse.From(b.Budget), MoneyPairResponse.From(b.Actual))).ToList());
}

/// <summary>The month header the dashboard shows; the full month (with income) lives on <c>GET /api/months/{id}</c>.</summary>
public record DashboardMonthResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("month_number")] int MonthNumber,
    [property: JsonPropertyName("week_count")] int WeekCount,
    [property: JsonPropertyName("week1_start_date")] DateOnly Week1StartDate,
    [property: JsonPropertyName("last_day")] DateOnly LastDay)
{
    public static DashboardMonthResponse From(Month m, IReadOnlyList<Week> weeks) =>
        new(m.Id, m.Year, m.MonthNumber, m.WeekCount, m.Week1StartDate, weeks.Count == 0 ? m.Week1StartDate : weeks.Max(w => w.EndDate));
}

/// <summary>
/// <c>GET /api/months/{id}/summary</c>. When no rate can be resolved (ADR-V006 final tier) the summary is null
/// and <c>rate_unavailable</c> is true — projections are blocked with a clear message, never guessed.
/// </summary>
public record DashboardResponse(
    [property: JsonPropertyName("month")] DashboardMonthResponse Month,
    [property: JsonPropertyName("exchange_rate")] decimal? ExchangeRate,
    [property: JsonPropertyName("rate_source")] string? RateSource,
    [property: JsonPropertyName("rate_as_of")] DateTimeOffset? RateAsOf,
    [property: JsonPropertyName("rate_unavailable")] bool RateUnavailable,
    [property: JsonPropertyName("summary")] DashboardSummaryResponse? Summary);
