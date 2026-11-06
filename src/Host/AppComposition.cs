using System.Resources;
using Perezosoft.Core;
using Vuelto.Host.Features.Budget;
using Vuelto.Host.Features.Cards;
using Vuelto.Host.Features.Catalog;
using Vuelto.Host.Features.Dashboard;
using Vuelto.Host.Features.DisplaySettings;
using Vuelto.Host.Features.Email;
using Vuelto.Host.Features.Envelopes;
using Vuelto.Host.Features.ExchangeRate;
using Vuelto.Host.Features.Expenses;
using Vuelto.Host.Features.Income;
using Vuelto.Host.Features.Ledger;
using Vuelto.Host.Features.Reports;
using Vuelto.Host.Features.Reports.Pdf;
using Perezosoft.Core.Abstractions;
using Vuelto.Host.Budget;
using Vuelto.Host.Mail;
using Vuelto.Host.ExchangeRate;
using Vuelto.Host.Vouchers;

namespace Vuelto.Host;

/// <summary>
/// The app's half of composition: the one file outside <c>Features/</c> that may name a slice. <c>Program.cs</c> calls
/// <see cref="ConfigurePlatform"/> for the platform's options, <see cref="AddAppServices"/> after the platform's
/// registrations and <see cref="MapAppEndpoints"/> after its routes. A slice registers its handler, its
/// <see cref="ITenantDataContributor"/> and any <see cref="IStartupTask"/> here and maps its group here. This is
/// Vuelto's budgeting domain, one block per slice (ADR-V001).
/// </summary>
public static class AppComposition
{
    /// <summary>The app's options for the platform: its brand (the mails, the MFA issuer, the OpenAPI title).</summary>
    public static void ConfigurePlatform(PlatformOptions options)
    {
        options.Brand.Name = "¿Y el vuelto?";
        // Traces, metrics and logs stay filed under the name they always had (the default would be "¿Yelvuelto?.Api").
        options.Brand.ServiceName = "Vuelto.Api";
        options.Brand.Tagline = "Finanzas personales en colones y dólares.";
        // The mails' palette: app.css's indigo (BRAND-SPEC.md). Gold #F2CB6E lives only inside the logo, the colón ₡.
        options.Brand.Colors.Primary = "#5A67D8";
        options.Brand.Colors.PrimaryDark = "#3A4178";
        options.Brand.Colors.Accent = "#8B95F0";
        options.Brand.Colors.AccentLight = "#8B91C4";
        options.Brand.Colors.Surface = "#F7F7FC";
        options.Brand.Colors.Border = "#E1E3F2";
        options.Brand.Colors.Ink = "#2D3142";
        options.Brand.Colors.Muted = "#6E7290";
        var assembly = typeof(AppComposition).Assembly;
        using (var logo = assembly.GetManifestResourceStream("Vuelto.Host.Email.logo.png")!)
        using (var bytes = new MemoryStream())
        {
            logo.CopyTo(bytes);
            options.Brand.Logo = bytes.ToArray();
        }
        // Only the keys that name the app (Email/EmailStrings*.resx); the rest are the platform's.
        options.Brand.Strings.Add(new ResourceManager("Vuelto.Host.Email.EmailStrings", assembly));
    }
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
