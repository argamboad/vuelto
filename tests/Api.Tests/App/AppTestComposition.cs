using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Vuelto.Api.Features.Budget;
using Vuelto.Api.Features.Cards;
using Vuelto.Api.Features.Catalog;
using Vuelto.Api.Features.Email;
using Vuelto.Api.Features.Envelopes;
using Vuelto.Api.Features.Expenses;
using Vuelto.Api.Features.Income;
using Vuelto.Api.Features.Ledger;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Mail;
using Vuelto.Infrastructure.ExchangeRate;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.App;

/// <summary>
/// The app's half of the test chassis (Arch A1, R159). <c>ServiceHarness</c> and <c>IntegrationTestFactory</c> are
/// the platform's and stay identical across repos; what an app adds to them is said here: the contributors its
/// slices register, so an accept-and-dissolve test consults what production consults (an empty household must read
/// as empty to every slice, and every slice's wipe runs inside the dissolve); and the pins its integration host
/// needs regardless of the developer's <c>.env</c> (a rate provider that must not be reached, a consent app that
/// must read as unconfigured).
/// </summary>
internal static class AppTestComposition
{
    /// <summary>
    /// Vuelto's <see cref="ITenantDataContributor"/>s as production resolves them (budget settings, the category and
    /// bank catalogs, envelopes, the ledger, fixed and variable expenses, income, voucher staging, cards, merchant
    /// mappings), built on the harness's context. Keep it in step with the <c>AddScoped&lt;ITenantDataContributor, …&gt;</c>
    /// lines in <c>AppComposition</c> when a slice adds one.
    /// </summary>
    public static IEnumerable<ITenantDataContributor> Contributors(AppDbContext db, TimeProvider clock) =>
    [
        new BudgetSettingsDataContributor(new EfRepository<BudgetSettings>(db)),
        new CategoryDataContributor(new EfRepository<Category>(db)),
        new BankDataContributor(new EfRepository<Bank>(db)),
        new EnvelopeDataContributor(new EfRepository<Envelope>(db)),
        new LedgerDataContributor(new EfRepository<Month>(db), new EfRepository<Week>(db),
            new EfRepository<Transaction>(db), new EfRepository<Refund>(db)),
        new FixedExpenseDataContributor(new EfRepository<FixedExpense>(db)),
        new VariableExpenseDataContributor(new EfRepository<VariableExpense>(db)),
        new IncomeDataContributor(new EfRepository<IncomeLine>(db), new EfRepository<MonthIncome>(db), new MonthIncomeRows(new EfRepository<MonthIncome>(db))),
        new VoucherStagingDataContributor(new EfRepository<PendingVoucher>(db), new EfRepository<IngestedVoucher>(db)),
        new CardDataContributor(new EfRepository<Card>(db), new EfRepository<CardIdentity>(db)),
        new MerchantMappingDataContributor(new EfRepository<MerchantCategoryMapping>(db)),
    ];

    /// <summary>Process-level pins the host reads at <c>CreateBuilder</c> time (environment variables); runs before the host is built.</summary>
    public static void PinEnvironment()
    {
        // ADR-V019: the provider is chosen at registration (Program reads ExchangeRate:Provider before any
        // service hook), so pin the keyless world feed here — with its key blanked below it reports
        // "unavailable" without a call, and the resolver falls to the last-transaction tier as the suite
        // asserts. The BCCR default would otherwise reach the real mirror from the test host.
        Environment.SetEnvironmentVariable("ExchangeRate__Provider", "exchangerate-api");
    }

    /// <summary>Service swaps for the integration host, after the platform's own (the throwaway database, the test auth handler).</summary>
    public static void ConfigureTestServices(IServiceCollection services)
    {
        // Fresh-checkout posture regardless of the developer's .env (Program loads it into the process
        // environment before any factory hook runs, so a config override is too late for settings
        // built at registration): no mail-consent apps, no live rate provider. The suite asserts the
        // "not configured" branches and must never reach a real IdP or spend the FX quota.
        services.RemoveAll<MailConsentSettings>();
        services.AddSingleton(new MailConsentSettings());
        services.RemoveAll<IConfigureOptions<ExchangeRateSettings>>();
    }
}
