using Microsoft.Extensions.Logging.Abstractions;
using Vuelto.Api.Configuration;
using Vuelto.Api.Services;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Infrastructure;

/// <summary>
/// Wires the real repositories + services around a single <see cref="AppDbContext"/>
/// (so they share one transaction/change-tracker, as in production) with lightweight
/// test doubles for settings, email, and the clock. Construct one per logical actor:
/// the context's current tenant drives the global query filter.
/// </summary>
public sealed class ServiceHarness(AppDbContext db, TimeProvider? clock = null, ICurrentTenant? currentTenant = null,
    SignupSettings? signup = null)
{
    public AppDbContext Db { get; } = db;
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    /// <summary>The ambient tenant QuotaService counts seats against. Defaults to "no tenant" (null),
    /// which makes the seat check trivially pass — pass an explicit one to exercise seat quotas.</summary>
    public ICurrentTenant CurrentTenant { get; } = currentTenant ?? new TestCurrentTenant();

    public IUserRepository Users { get; } = new UserRepository(db);
    public ILoginTokenRepository LoginTokens { get; } = new LoginTokenRepository(db, clock ?? TimeProvider.System);
    public IRefreshTokenRepository RefreshTokens { get; } = new RefreshTokenRepository(db, clock ?? TimeProvider.System);
    public ITenantRepository Tenants { get; } = new TenantRepository(db);
    public ITenantInvitationRepository Invitations { get; } = new TenantInvitationRepository(db);
    public IRepository<Subscription> Subscriptions { get; } = new EfRepository<Subscription>(db);
    public IRepository<UsageCounter> UsageCounters { get; } = new EfRepository<UsageCounter>(db);
    public IUnitOfWork UnitOfWork { get; } = new EfUnitOfWork(db);
    public ITokenGenerator TokenGen { get; } = new TokenGenerator();
    public ITokenHasher Hasher { get; } = new TokenHasher();
    public IAuditLog Audit { get; } = new Vuelto.Infrastructure.Audit.AuditLog(
        new EfRepository<AuditEvent>(db), clock ?? TimeProvider.System);

    /// <summary>The signup green list (GATES-2). Defaults to empty ⇒ open, so existing tests are unaffected.</summary>
    public SignupSettings Signup { get; } = signup ?? new SignupSettings();

    public ISignupGate SignupGate() =>
        new SignupGate(Signup, Invitations, Tenants, Clock, NullLogger<SignupGate>.Instance);

    public UserService UserService() =>
        new(Users, Tenants, UnitOfWork, SignupGate(), Clock, NullLogger<UserService>.Instance);

    public RefreshTokenService RefreshTokenService(int expiryDays = 30, int reuseGraceSeconds = 60, int? absoluteLifetimeDays = null) =>
        new(RefreshTokens, TokenGen, Hasher, new TestRefreshSettings(expiryDays, reuseGraceSeconds, absoluteLifetimeDays), Clock);

    public PasswordlessService PasswordlessService(IPasswordlessSettings? settings = null) =>
        new(LoginTokens, UserService(), TokenGen, Hasher, settings ?? new TestPasswordlessSettings(), Clock);

    public TenantService TenantService() =>
        new(Tenants, UnitOfWork,
            new TenantDissolutionService([], Tenants, CurrentTenant as ITenantContext ?? new TestCurrentTenant()),
            Clock, NullLogger<TenantService>.Instance, Audit);

    public JwtTokenService JwtTokenService() =>
        new(new TestJwtSettings(), Clock, NullLogger<JwtTokenService>.Instance);

    public SessionService SessionService() =>
        new(RefreshTokenService(), JwtTokenService(), Tenants, new TestJwtSettings());

    public QuotaService QuotaService() =>
        new(Subscriptions, Tenants, Invitations, UsageCounters, CurrentTenant, Clock);

    /// <param name="contributors">The tenant-data contributors the accept consults (would the old tenant be
    /// abandoned?) and dissolves through. Defaults to none; <see cref="PlatformContributors"/> is the shipped set.</param>
    public TenantInvitationService InvitationService(IInvitationSettings? invitation = null,
        IReadOnlyList<ITenantDataContributor>? contributors = null)
    {
        var tenantContext = CurrentTenant as ITenantContext ?? new TestCurrentTenant();
        contributors ??= [];
        return new(Invitations, Tenants, TokenGen, Hasher, UnitOfWork, new NoopEmailSender(),
            UserService(), new TestAppSettings(), invitation ?? new TestInvitationSettings(),
            contributors, new TenantDissolutionService(contributors, Tenants, tenantContext),
            QuotaService(), tenantContext, Clock, NullLogger<TenantInvitationService>.Instance);
    }

    /// <summary>
    /// Every <see cref="ITenantDataContributor"/> this app registers in DI, as production resolves them: the
    /// platform's six (API keys, webhooks, usage metering, billing, the audit log, the outbox) plus each feature
    /// slice's (budget settings, the category and bank catalogs, envelopes, the ledger, fixed and variable
    /// expenses, income, voucher staging, cards, merchant mappings). The whole set rather than just the platform's, so an
    /// accept-and-dissolve test also proves no slice counts an empty household as content and every slice's
    /// wipe runs inside the dissolve. Keep it in step with the <c>AddScoped&lt;ITenantDataContributor, …&gt;</c>
    /// lines in <c>Program.cs</c>, <c>ServiceRegistrationExtensions</c> and Infrastructure's
    /// <c>ServiceCollectionExtensions</c> when a slice adds one.
    /// </summary>
    public IReadOnlyList<ITenantDataContributor> PlatformContributors() =>
    [
        new ApiKeyDataContributor(new EfRepository<ApiKey>(Db)),
        new WebhookDataContributor(new EfRepository<WebhookSubscription>(Db), new EfRepository<WebhookDelivery>(Db)),
        new UsageCounterDataContributor(UsageCounters),
        new BillingDataContributor(Subscriptions, new Vuelto.Infrastructure.Outbox.EfOutbox(Db, Clock)),
        new Vuelto.Infrastructure.Audit.AuditDataContributor(new EfRepository<AuditEvent>(Db)),
        new Vuelto.Api.Features.Budget.BudgetSettingsDataContributor(new EfRepository<BudgetSettings>(Db)),
        new Vuelto.Api.Features.Catalog.CategoryDataContributor(new EfRepository<Category>(Db)),
        new Vuelto.Api.Features.Catalog.BankDataContributor(new EfRepository<Bank>(Db)),
        new Vuelto.Api.Features.Envelopes.EnvelopeDataContributor(new EfRepository<Envelope>(Db)),
        new Vuelto.Api.Features.Ledger.LedgerDataContributor(new EfRepository<Month>(Db), new EfRepository<Week>(Db),
            new EfRepository<Transaction>(Db), new EfRepository<Refund>(Db)),
        new Vuelto.Api.Features.Expenses.FixedExpenseDataContributor(new EfRepository<FixedExpense>(Db)),
        new Vuelto.Api.Features.Expenses.VariableExpenseDataContributor(new EfRepository<VariableExpense>(Db)),
        new Vuelto.Api.Features.Income.IncomeDataContributor(new EfRepository<IncomeLine>(Db), new EfRepository<MonthIncome>(Db)),
        new Vuelto.Api.Features.Email.VoucherStagingDataContributor(new EfRepository<PendingVoucher>(Db), new EfRepository<IngestedVoucher>(Db)),
        new Vuelto.Api.Features.Cards.CardDataContributor(new EfRepository<Card>(Db), new EfRepository<CardIdentity>(Db)),
        new Vuelto.Api.Features.Email.MerchantMappingDataContributor(new EfRepository<MerchantCategoryMapping>(Db)),
        // Only the email handler is needed to classify: a type no handler claims is kept, which is what the
        // billing.cancel this dissolve queues must be.
        new Vuelto.Infrastructure.Outbox.OutboxDataContributor(new EfRepository<OutboxMessage>(Db),
            [new Vuelto.Infrastructure.Email.EmailOutboxHandler(new NoopEmailSender())]),
    ];
}

internal sealed class TestRefreshSettings(int expiryDays = 30, int reuseGraceSeconds = 60, int? absoluteLifetimeDays = null) : IRefreshTokenSettings
{
    public int ExpiryDays => expiryDays;
    public int ReuseGraceSeconds => reuseGraceSeconds;
    public int? AbsoluteLifetimeDays => absoluteLifetimeDays;
}

internal sealed class TestPasswordlessSettings : IPasswordlessSettings
{
    public int MagicLinkLifespanMinutes { get; init; } = 15;
    public int OtpLifespanMinutes { get; init; } = 10;
    public int OtpLength { get; init; } = 6;
    public int OtpMaxAttempts { get; init; } = 5;
    public int OtpLockoutWindowMinutes { get; init; } = 15;
}

internal sealed class TestMfaSettings : IMfaSettings
{
    public int MaxAttempts { get; init; } = 5;
    public int LockoutWindowMinutes { get; init; } = 15;
}

internal sealed class TestAppSettings : IApplicationSettings
{
    public string ClientUrl => "https://localhost:7108";
    public string NativeCallbackScheme => string.Empty;
}

internal sealed class TestInvitationSettings(int lifespanDays = 7) : IInvitationSettings
{
    public int LifespanDays => lifespanDays;
}

internal sealed class TestJwtSettings : IJwtSettings
{
    public string SecretKey { get; init; } = "test-secret-key-at-least-32-chars-long-000";
    public string Issuer => "VueltoTests";
    public int ExpiryMinutes => 60;
}

internal sealed class NoopEmailSender : IEmailSender
{
    public Task SendAsync(string to, string subject, string htmlBody,
        IReadOnlyList<EmailInlineImage>? inlineImages = null, IReadOnlyList<EmailAttachment>? attachments = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Test double for the export service — used by controller tests that don't exercise export.</summary>
internal sealed class StubExportService : Vuelto.Api.Services.ITenantExportService
{
    public bool Called { get; private set; }

    public Task<Uri> ExportAsync(Guid tenantId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        Called = true;
        return Task.FromResult(new Uri("https://example.test/api/files/stub-token"));
    }
}
