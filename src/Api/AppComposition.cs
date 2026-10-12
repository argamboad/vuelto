using Vuelto.Api.Features.Budget;
using Vuelto.Api.Features.Cards;
using Vuelto.Api.Features.Catalog;
using Vuelto.Api.Features.Dashboard;
using Vuelto.Api.Features.DisplaySettings;
using Vuelto.Api.Features.Email;
using Vuelto.Api.Features.Envelopes;
using Vuelto.Api.Features.ExchangeRate;
using Vuelto.Api.Features.Expenses;
using Vuelto.Api.Features.Income;
using Vuelto.Api.Features.Ledger;
using Vuelto.Api.Features.Reports;
using Vuelto.Api.Features.Reports.Pdf;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Budget;
using Vuelto.Core.Mail;
using Vuelto.Infrastructure.ExchangeRate;
using Vuelto.Infrastructure.Mail;
using Vuelto.Infrastructure.Vouchers;

namespace Vuelto.Api;

/// <summary>
/// The app's half of composition (Arch A1, R159): the one file outside <c>src/Api/Features/</c> that may name a
/// slice (R8, as amended). <c>Program.cs</c> calls <see cref="AddAppServices"/> once after the platform's
/// registrations and <see cref="MapAppEndpoints"/> once after the platform's routes, and is otherwise identical in
/// the platform and every app. A slice registers its handler, its <see cref="ITenantDataContributor"/> and any
/// <see cref="IStartupTask"/> here and maps its group here; nothing else central is edited (the add-a-slice
/// checklist in <c>docs/WAYS_OF_WORKING.md</c>). This is Vuelto's budgeting domain, one block per slice (ADR-V001).
/// </summary>
public static class AppComposition
{
    /// <summary>Registers every slice's services. Behind each slice's own <c>Enabled</c> setting where it has one.</summary>
    public static IServiceCollection AddAppServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<BudgetSettingsHandler>();                                   // BUDGET-1
        services.AddScoped<ITenantDataContributor, BudgetSettingsDataContributor>();
        services.AddScoped<CategoryCatalogHandler>();                                  // CATALOG-1/2
        services.AddScoped<BankCatalogHandler>();
        services.AddScoped<ITenantDataContributor, CategoryDataContributor>();
        services.AddScoped<ITenantDataContributor, BankDataContributor>();
        services.AddScoped<IBankDefaults, BankDefaults>();                              // the Catalog seeds banks for the mail poll (Arch A8)
        services.AddExchangeRates(configuration);                                     // FX-1 (no entity)
        services.AddVoucherParsing();                                                 // EMAIL-1 (pure parser library; no entity)
        services.AddMailIngestion(configuration);                                     // EMAIL-2/3 (token protector, consent, Graph + Gmail readers)
        services.AddScoped<IExchangeRateResolver, ExchangeRateResolver>();
        services.AddScoped<IRecentRateSource, TransactionRecentRateSource>();          // LEDGER-2 fills the chain's last tier
        services.AddScoped<EnvelopeHandler>();                                         // ENV-1
        services.AddScoped<ITenantDataContributor, EnvelopeDataContributor>();
        services.AddSingleton<IWeekBoundaryService, WeekBoundaryService>();            // pure Core service (BUDGET-1)
        services.AddScoped<MonthHandler>();                                            // LEDGER-1/2
        services.AddScoped<TransactionHandler>();
        services.AddScoped<RefundHandler>();                                           // LEDGER-3
        services.AddScoped<ITenantDataContributor, LedgerDataContributor>();
        services.AddScoped<IMonthIncomeRows, MonthIncomeRows>();                        // Income writes month rows through the Ledger (Arch A8)
        services.AddScoped<ITransactionCards, TransactionCards>();                      // Cards rewrites transactions through the Ledger (Arch A8)
        services.AddScoped<FixedExpenseHandler>();                                     // EXPENSES-1
        services.AddScoped<VariableExpenseHandler>();
        services.AddScoped<ITenantDataContributor, FixedExpenseDataContributor>();
        services.AddScoped<ITenantDataContributor, VariableExpenseDataContributor>();
        services.AddScoped<IncomeHandler>();                                            // INCOME-1
        services.AddScoped<ITenantDataContributor, IncomeDataContributor>();
        services.AddScoped<IUserDataContributor, IncomeUserDataContributor>();
        services.AddSingleton<IDashboardSummaryService, DashboardSummaryService>();    // DASH-1 (pure Core calc)
        services.AddScoped<DashboardHandler>();
        services.AddScoped<ReportHandler>();                                            // REPORTS-1/2
        services.AddScoped<ReportPdfHandler>();                                         // REPORTS-7/8
        services.AddReportEmailRateLimit();                                              // REPORTS-8: 10 report emails a day per person
        services.AddScoped<EmailConnectionHandler>();                                   // EMAIL-2 (user-keyed, ADR-V002)
        services.AddScoped<IUserDataContributor, EmailConnectionUserDataContributor>();
        services.AddScoped<IVoucherStagingService, VoucherStagingService>();          // EMAIL-4 (staging with the tenant hop)
        services.AddScoped<IScheduledJob, EmailPollJob>();                              // EMAIL-4 (poller on the platform scheduler)
        services.AddScoped<ITenantDataContributor, VoucherStagingDataContributor>();
        services.AddScoped<CardHandler>();                                              // CARDS-1 (ADR-V021)
        services.AddScoped<ICardResolver>(sp => sp.GetRequiredService<CardHandler>());   // the review queue's face of it (R7)
        services.AddScoped<ITenantDataContributor, CardDataContributor>();
        services.AddScoped<DisplaySettingsHandler>();                                   // DISPLAY-1 (user-keyed, ADR-V020)
        services.AddScoped<IUserDataContributor, DisplaySettingsUserDataContributor>();
        services.AddScoped<ITransactionService>(sp => sp.GetRequiredService<TransactionHandler>()); // ADR-V010: the Ledger create behind the Core contract (R7 — slices never reference each other)
        services.AddScoped<MerchantMappingHandler>();                                   // EMAIL-5 (household suggestion rules)
        services.AddScoped<ITenantDataContributor, MerchantMappingDataContributor>();
        services.AddScoped<PendingVoucherHandler>();                                    // EMAIL-6 (review queue: the only draft → transaction path)
        return services;
    }

    /// <summary>Maps every slice's endpoint group (each through <c>MapTenantFeatureGroup</c>, R6).</summary>
    public static IEndpointRouteBuilder MapAppEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapBudgetSettings(); // BUDGET-1
        app.MapCatalog();        // CATALOG-1/2 (/api/categories, /api/banks)
        app.MapExchangeRate();   // FX-1
        app.MapEnvelopes();      // ENV-1
        app.MapLedger();         // LEDGER-1/2/3 (/api/months, /api/transactions, /api/refunds)
        app.MapExpenses();       // EXPENSES-1 (/api/expenses/fixed, /api/expenses/variable)
        app.MapIncomes();        // INCOME-1 (/api/incomes)
        app.MapDashboard();      // DASH-1 (/api/months/{id}/summary)
        app.MapReports();        // REPORTS-1/2/7/8 (/api/reports/category-analysis, /api/reports/transactions/export, /api/reports/pdf, /api/reports/pdf/email)
        app.MapEmail();          // EMAIL-2/3 (/api/email/connections — user-scoped; the consent callback is the one anonymous route)
        app.MapMerchantMappings(); // EMAIL-5 (/api/merchant-mappings)
        app.MapPendingVouchers();  // EMAIL-6 (/api/pending-vouchers — list, count, confirm, discard)
        app.MapCards();            // CARDS-1 (/api/cards)
        app.MapDisplaySettings();  // DISPLAY-1 (/api/display-settings — the caller's own ₡ · $ · both preference)
        return app;
    }
}
