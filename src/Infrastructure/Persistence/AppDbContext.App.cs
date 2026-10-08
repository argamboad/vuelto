using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence;

/// <summary>
/// The app's half of the context (Arch A1, R159): its <c>DbSet</c>s, and any model rule of its own in
/// <c>OnAppModelCreating</c>, which the platform's <c>OnModelCreating</c> calls last. The other half
/// (<c>AppDbContext.cs</c>) is the platform's and stays identical across repos: the platform sets, the tenant filter,
/// the interceptors. Entity configurations need no registration — they are discovered from this assembly — so a
/// slice adds its entity's set here and nothing else central. Vuelto's budget domain, one set per port slice (ADR-V001).
/// </summary>
public partial class AppDbContext
{
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
    public DbSet<IncomeLine> IncomeLines => Set<IncomeLine>();       // INCOME-1 (the household's incomes, ADR-V023)
    public DbSet<MonthIncome> MonthIncomes => Set<MonthIncome>();    // INCOME-1: each month's income rows

    // Vuelto has no model rule outside its entity configurations, so OnAppModelCreating stays unimplemented and the
    // compiler drops the platform's call.
}
