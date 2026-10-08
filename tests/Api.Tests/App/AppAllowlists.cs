using Vuelto.Core.Entities;

namespace Vuelto.Api.Tests.App;

/// <summary>
/// The app's entries in the platform's architecture gates (Arch A1, R159). The gates in
/// <c>ArchitectureTests</c> and <c>DataProtectionIdentityTests</c> are the platform's and stay identical across
/// repos; what an app has to say to them — which contributor dissolves its entities, which of its files sends HTTP
/// to a fixed host, which purpose strings it protects with — is said here, typed (<c>nameof</c>: a renamed entity
/// fails to compile rather than silently dropping out of a canary). Vuelto's budgeting slices (ADR-V001).
/// A gate that grows a new allowlist adds a set here, and the add-a-slice checklist names it.
/// </summary>
internal static partial class AppAllowlists
{
    /// <summary>User-keyed entities an <c>IUserDataContributor</c> of the app erases (<c>EveryUserKeyedEntity_IsWiredIntoAccountErasure</c>).</summary>
    public static readonly IReadOnlySet<string> ErasureHandled = new HashSet<string>
    {
        nameof(EmailConnection),     // EmailConnectionUserDataContributor (EMAIL-2, ADR-V002)
        nameof(UserDisplaySettings), // DisplaySettingsUserDataContributor (DISPLAY-1, ADR-V020)
    };

    /// <summary>Tenant-owned entities an <c>ITenantDataContributor</c> of the app wipes and exports (<c>EveryTenantOwnedEntity_IsWiredIntoTenantDissolution</c>).</summary>
    public static readonly IReadOnlySet<string> DissolutionHandled = new HashSet<string>
    {
        nameof(BudgetSettings),                          // BudgetSettingsDataContributor (BUDGET-1)
        nameof(Category),                                // CategoryDataContributor (CATALOG-1)
        nameof(Bank),                                    // BankDataContributor (CATALOG-2)
        nameof(Card), nameof(CardIdentity),              // CardDataContributor (CARDS-1)
        nameof(Envelope),                                // EnvelopeDataContributor (ENV-1)
        nameof(Month), nameof(Week),                     // LedgerDataContributor (LEDGER-1/2)
        nameof(Transaction),                             // LedgerDataContributor
        nameof(Refund),                                  // LedgerDataContributor (LEDGER-3)
        nameof(FixedExpense),                            // FixedExpenseDataContributor (EXPENSES-1)
        nameof(VariableExpense),                         // VariableExpenseDataContributor (EXPENSES-1)
        nameof(IncomeLine), nameof(MonthIncome),         // IncomeDataContributor (INCOME-1)
        nameof(PendingVoucher), nameof(IngestedVoucher), // VoucherStagingDataContributor (EMAIL-4)
        nameof(MerchantCategoryMapping),                 // MerchantMappingDataContributor (EMAIL-5)
    };

    /// <summary>Nullable-TenantId entities whose lifecycle is pinned elsewhere than the four-facet spec (<c>EveryNullableTenantIdEntity_ShipsItsLifecycleSpec</c>), with the reason.</summary>
    public static readonly IReadOnlyDictionary<string, string> LifecycleSpecExceptions = new Dictionary<string, string>
    {
    };

    /// <summary>Entities carrying a <c>TenantId</c> that are scoped by convention rather than <c>ITenantScoped</c> (<c>EveryEntityWithATenantId_IsScopedOrAllowlisted</c>).</summary>
    public static readonly IReadOnlySet<string> TenantIdByConvention = new HashSet<string>
    {
    };

    /// <summary>Server-side files that send HTTP without the outbound URL guard, each with why its destinations are not attacker-influenced (<c>OutboundHttpSenders_RouteThroughTheUrlGuard_OrAreAllowlisted</c>).</summary>
    public static readonly IReadOnlyDictionary<string, string> OutboundHttpSenders = new Dictionary<string, string>
    {
        // FX-1 (ADR-V006, ADR-V019): fixed vendor hosts, nothing tenant-supplied reaches a URL.
        ["ExchangeRateApiClient.cs"] = "destination = the configured vendor host (ExchangeRate:BaseUrl) + API key + two currency codes validated as ^[A-Z]{3}$ — nothing tenant-supplied reaches the URL",
        ["BccrExchangeRateClient.cs"] = "destination = the configured fixed BCCR mirror URL (ExchangeRate:BccrUrl), nothing appended — nothing tenant-supplied reaches the URL (ADR-V019)",
        // EMAIL-2/3 (ADR-V016): fixed provider hosts only — nothing user-supplied reaches a URL.
        ["MailConsentService.cs"] = "destination = the two fixed IdP token endpoints (login.microsoftonline.com/{configured tenant}, oauth2.googleapis.com); the code/refresh token travel in the form body",
        ["GraphEmailReader.cs"] = "destination = graph.microsoft.com only — folder ids are URL-escaped path segments and the @odata.nextLink is followed only when its host is graph.microsoft.com",
        ["GmailEmailReader.cs"] = "destination = gmail.googleapis.com only — message ids are URL-escaped path segments, the search string is a query value",
    };

    /// <summary>Controllers that derive from neither tenant nor admin base, being anonymous or system surfaces (<c>EveryController_DerivesFromATenantOrAdminBase_OrIsAllowlisted</c>).</summary>
    public static readonly IReadOnlySet<string> ControllersOutsideTheBases = new HashSet<string>
    {
    };

    /// <summary>DataProtection purpose strings the app protects with, frozen because renaming one orphans what it encrypted (<c>DataProtectionIdentityTests</c>).</summary>
    public static readonly IReadOnlySet<string> DataProtectionPurposes = new HashSet<string>
    {
        // EMAIL-2 (ADR-V016) — mail OAuth tokens at rest + the 15-minute consent state.
        "Vuelto.Mail.Tokens.v1",       // DataProtectionEmailTokenProtector — encrypted mail access/refresh tokens (AT REST)
        "Vuelto.Mail.ConsentState.v1", // MailConsentService — time-limited OAuth consent state
    };

    /// <summary>Which slice may WRITE each entity (Arch A8, R162): entity → the folder under <c>src/Api/Features/</c> that owns it.
    /// Other slices read it, or go through a Core contract the owner implements (<c>EveryEntity_HasOneWritingSlice</c>).</summary>
    public static readonly IReadOnlyDictionary<string, string> EntityWriters = new Dictionary<string, string>
    {
        [nameof(BudgetSettings)] = "Budget",
        // Category and Bank are written through the generic CatalogHandler<TEntry>, which SliceWriteInspector reads as
        // "TEntry" (it does not resolve a generic slice base); Bank is also written by the Catalog's BankDefaults.
        // Until the inspector resolves the type argument (perezosoft-platform), the parameter is what is declared.
        ["TEntry"] = "Catalog",
        [nameof(Bank)] = "Catalog",
        [nameof(Envelope)] = "Envelopes",
        [nameof(Month)] = "Ledger",
        [nameof(Week)] = "Ledger",
        [nameof(Transaction)] = "Ledger",          // ADR-V010: other slices create through ITransactionService
        [nameof(Refund)] = "Ledger",
        [nameof(VariableExpense)] = "Expenses",    // FixedExpense goes through the generic ExpenseLineHandler<TLine>, unseen like TEntry
        [nameof(IncomeLine)] = "Income",
        [nameof(MonthIncome)] = "Ledger",          // written with the month; Income goes through IMonthIncomeRows
        [nameof(EmailConnection)] = "Email",
        [nameof(PendingVoucher)] = "Email",
        [nameof(IngestedVoucher)] = "Email",
        [nameof(MerchantCategoryMapping)] = "Email",
        [nameof(Card)] = "Cards",                 // its transactions are rewritten through ITransactionCards
        [nameof(CardIdentity)] = "Cards",
        [nameof(UserDisplaySettings)] = "DisplaySettings",
    };

    /// <summary>Source files that aggregate several small types and so do not declare one named for the file (<c>SourceFile_DeclaresATypeMatchingItsName</c>).</summary>
    public static readonly IReadOnlySet<string> TypeNameExceptions = new HashSet<string>
    {
    };
}
