using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Perezosoft.Core.Abstractions;
using Perezosoft.Infrastructure.Repositories;
using Perezosoft.Testing;
using Vuelto.Host.Entities;
using Vuelto.Host.ExchangeRate;
using Vuelto.Host.Features.Budget;
using Vuelto.Host.Features.Cards;
using Vuelto.Host.Features.Catalog;
using Vuelto.Host.Features.Email;
using Vuelto.Host.Features.Envelopes;
using Vuelto.Host.Features.Expenses;
using Vuelto.Host.Features.Income;
using Vuelto.Host.Features.Ledger;
using Vuelto.Host.Mail;
using Vuelto.Host.Persistence;
namespace Vuelto.Api.Tests.App;

/// <summary>
/// What this app tells the packaged harness (Perezosoft.Platform.Testing): the contributors its slices register, so an
/// accept-and-dissolve test consults what production consults (an empty household must read as empty to every slice,
/// and every slice's wipe runs inside the dissolve); the pins its integration host needs regardless of the developer's
/// <c>.env</c> (a rate provider that must not be reached, a consent app that must read as unconfigured); its web origin.
/// </summary>
internal static class AppTestComposition
{
#pragma warning disable CA2255 // a module initializer in a test assembly is the point: it runs before any test builds a harness
    [System.Runtime.CompilerServices.ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register()
    {
        PlatformTestComposition.Contributors = (db, clock) => Contributors((AppDbContext)db, clock);
        PlatformTestComposition.PinEnvironment = PinEnvironment;
        PlatformTestComposition.ConfigureTestServices = ConfigureTestServices;
        PlatformTestComposition.ClientUrl = LocalPorts.WebHttpsUrl;
    }

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
        new CardDataContributor(new EfRepository<Card>(db), new EfRepository<CardIdentity>(db), new EfRepository<CardPattern>(db)),
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
