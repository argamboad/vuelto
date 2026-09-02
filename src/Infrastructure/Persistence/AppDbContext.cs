using System.Reflection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Entities;

namespace Perezosoft.Infrastructure.Persistence;

public class AppDbContext : DbContext, IDataProtectionKeyContext
{
    private readonly ICurrentTenant _currentTenant;

    public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentTenant currentTenant)
        : this((DbContextOptions)options, currentTenant) { }

    /// <summary>
    /// Non-generic-options constructor so a test harness can subclass this context (e.g. to add a
    /// test-only <c>ITenantScoped</c> fixture entity) with its own <c>DbContextOptions&lt;TDerived&gt;</c>,
    /// without the platform tests depending on the DELETE-ME sample slice (v2 audit TR-1). Not used by
    /// the app itself.
    /// </summary>
    protected AppDbContext(DbContextOptions options, ICurrentTenant currentTenant)
        : base(options)
    {
        _currentTenant = currentTenant;
    }

    /// <summary>
    /// Tenant the global query filter scopes to. <see cref="Guid.Empty"/> when there is
    /// no current tenant — it matches no real (UUIDv7) row, so unauthenticated/tenant-less
    /// callers see no tenant-scoped data (fail closed).
    /// </summary>
    public Guid CurrentTenantId => _currentTenant.TenantId ?? Guid.Empty;

    /// <summary>
    /// Nullable tenant for the RLS backstop (ADR-020): unlike <see cref="CurrentTenantId"/>, the
    /// interceptor must distinguish "no tenant" (system context → explicit bypass GUC) from a real
    /// tenant (tenant GUC), so the null is preserved rather than collapsed to <see cref="Guid.Empty"/>.
    /// </summary>
    internal Guid? RlsTenantId => _currentTenant.TenantId;

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
    public DbSet<TenantInvitation> TenantInvitations => Set<TenantInvitation>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserLogin> UserLogins => Set<UserLogin>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<LoginToken> LoginTokens => Set<LoginToken>();
    public DbSet<UserMfa> UserMfa => Set<UserMfa>();
    public DbSet<MfaRecoveryCode> MfaRecoveryCodes => Set<MfaRecoveryCode>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();

    // Transactional outbox — reliable, atomic side effects (ADR-007). Platform infra, not
    // ITenantScoped, so it is outside the global tenant query filter.
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    // Inbox dedup ledger — idempotent inbound (webhook) deliveries (ADR-007). Platform infra,
    // not ITenantScoped.
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    // Billing subscription projection (ADR-006). ITenantScoped, so the global query filter scopes
    // it to the current tenant automatically.
    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    // Per-tenant metered-usage counters (BILLING-5). ITenantScoped (auto-filtered per tenant).
    public DbSet<UsageCounter> UsageCounters => Set<UsageCounter>();

    // Tenant API keys for programmatic access (PUBAPI, ADR-015). ITenantScoped; only the hash is stored.
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    // Outbound webhook subscriptions (HOOKS, ADR-016). ITenantScoped; signing secret encrypted at rest.
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();

    // Webhook delivery attempts (HOOKS-2). NOT ITenantScoped (written from the tenant-less dispatcher);
    // TenantId is a plain filter column for the read side.
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    // Append-only tenant audit trail (ADR-008). ITenantScoped (auto-filtered per tenant); writes are
    // append-only via AuditAppendOnlyInterceptor.
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    // 🗑️ DELETE-ME: sample feature set (remove with the Features/Notes slice).
    public DbSet<Note> Notes => Set<Note>();

    // Tenant isolation is structural in BOTH directions: the global query filter (below)
    // scopes reads, and this interceptor scopes writes — stamping the current tenant onto
    // new ITenantScoped rows and refusing foreign-tenant writes. Registered here (not only
    // in DI) so every context — including ones constructed directly in tests — enforces it.
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.AddInterceptors(
            TenantStampingInterceptor.Instance,
            AuditAppendOnlyInterceptor.Instance,
            // RLS backstop (ADR-020): carries the ambient tenant to Postgres per command, so the
            // DB-level policies scope even queries that escaped the EF filter.
            RlsSessionInterceptor.Instance);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Per-entity mapping lives in one IEntityTypeConfiguration<TEntity> class each, under
        // Persistence/Configurations (DEBT-4). Discovered + applied from this assembly.
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Tenant isolation as a structural guarantee: every ITenantScoped entity is
        // filtered to CurrentTenantId by default, so feature/domain queries can't forget
        // to scope. Genuinely cross-tenant or pre-auth lookups opt out with
        // IgnoreQueryFilters(). This is a query-time filter only — no schema change.
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            if (typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType))
                ApplyTenantFilterMethod
                    .MakeGenericMethod(entityType.ClrType)
                    .Invoke(this, [builder]);
        }
    }

    private static readonly MethodInfo ApplyTenantFilterMethod =
        typeof(AppDbContext).GetMethod(nameof(ApplyTenantFilter),
            BindingFlags.Instance | BindingFlags.NonPublic)!;

    private void ApplyTenantFilter<TEntity>(ModelBuilder builder) where TEntity : class, ITenantScoped
        => builder.Entity<TEntity>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
}
