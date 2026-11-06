using Microsoft.EntityFrameworkCore;
using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Entities;
using Vuelto.Host.Entities;
using Perezosoft.Infrastructure.Persistence;

namespace Vuelto.Host.Persistence;

/// <summary>
/// Vuelto's database context: the platform's half is <see cref="PlatformDbContext"/> (its sets, the tenant filter, the
/// interceptors), in the Infrastructure package; this is the app's half — its sets here, its model rules in
/// <see cref="ApplyAppModel"/>, its entity configurations discovered from this assembly. Its migrations live beside it.
/// </summary>
public class AppDbContext : PlatformDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentTenant currentTenant)
        : this((DbContextOptions)options, currentTenant) { }

    /// <summary>For a test harness that subclasses this context with its own options (a test-only fixture entity).</summary>
    protected AppDbContext(DbContextOptions options, ICurrentTenant currentTenant)
        : base(options, currentTenant) { }

    // Vuelto's budget domain, one set per slice (ADR-V001).
    public DbSet<BudgetSettings> BudgetSettings => Set<BudgetSettings>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Bank> Banks => Set<Bank>();
    public DbSet<Envelope> Envelopes => Set<Envelope>();
    public DbSet<Month> Months => Set<Month>();
    public DbSet<Week> Weeks => Set<Week>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<FixedExpense> FixedExpenses => Set<FixedExpense>();
    public DbSet<VariableExpense> VariableExpenses => Set<VariableExpense>();
    public DbSet<EmailConnection> EmailConnections => Set<EmailConnection>(); // EMAIL-2 (user-keyed, ADR-V002)
    public DbSet<PendingVoucher> PendingVouchers => Set<PendingVoucher>();       // EMAIL-4 (household-scoped drafts)
    public DbSet<IngestedVoucher> IngestedVouchers => Set<IngestedVoucher>();    // EMAIL-4 (dedup tombstones)
    public DbSet<MerchantCategoryMapping> MerchantCategoryMappings => Set<MerchantCategoryMapping>(); // EMAIL-5 (household suggestion rules)
    public DbSet<UserDisplaySettings> UserDisplaySettings => Set<UserDisplaySettings>(); // DISPLAY-1 (user-keyed, ADR-V020)
    public DbSet<Card> Cards => Set<Card>(); // CARDS-1 (household payment cards, ADR-V021)
    public DbSet<CardIdentity> CardIdentities => Set<CardIdentity>(); // CARDS-1: every (brand, last four) a card is known by
    public DbSet<CardPattern> CardPatterns => Set<CardPattern>(); // #210: a masked number whose digits are not the last four, mapped to its card
    public DbSet<IncomeLine> IncomeLines => Set<IncomeLine>();       // INCOME-1 (the household's incomes, ADR-V023)
    public DbSet<MonthIncome> MonthIncomes => Set<MonthIncome>();    // INCOME-1: each month's income rows

    // The app's entity configurations: this assembly's (a test subclass in another assembly still gets this one's).
    protected override IEnumerable<System.Reflection.Assembly> AppModelAssemblies => [typeof(AppDbContext).Assembly];

    /// <summary>The app's own model rules, applied after the platform's.</summary>
    protected override void ApplyAppModel(ModelBuilder builder) { }
}
