using Microsoft.Extensions.Logging.Abstractions;
using Perezosoft.Api.Configuration;
using Perezosoft.Api.Services;
using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;
using Perezosoft.Infrastructure.Persistence;
using Perezosoft.Infrastructure.Repositories;

namespace Perezosoft.Api.Tests.Infrastructure;

/// <summary>
/// Wires the real repositories + services around a single <see cref="AppDbContext"/>
/// (so they share one transaction/change-tracker, as in production) with lightweight
/// test doubles for settings, email, and the clock. Construct one per logical actor:
/// the context's current tenant drives the global query filter.
/// </summary>
public sealed class ServiceHarness(AppDbContext db, TimeProvider? clock = null, ICurrentTenant? currentTenant = null)
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
    public IAuditLog Audit { get; } = new Perezosoft.Infrastructure.Audit.AuditLog(
        new EfRepository<AuditEvent>(db), clock ?? TimeProvider.System);

    public UserService UserService() => new(Users, Tenants, UnitOfWork, Clock, NullLogger<UserService>.Instance);

    public RefreshTokenService RefreshTokenService(int expiryDays = 30) =>
        new(RefreshTokens, TokenGen, Hasher, new TestRefreshSettings(expiryDays), Clock);

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

    public TenantInvitationService InvitationService(IInvitationSettings? invitation = null) =>
        new(Invitations, Tenants, TokenGen, Hasher, UnitOfWork, new NoopEmailSender(),
            UserService(), new TestAppSettings(), invitation ?? new TestInvitationSettings(),
            [], QuotaService(), CurrentTenant as ITenantContext ?? new TestCurrentTenant(),
            Clock, NullLogger<TenantInvitationService>.Instance);
}

internal sealed class TestRefreshSettings(int expiryDays = 30) : IRefreshTokenSettings
{
    public int ExpiryDays => expiryDays;
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
    public string ClientUrl => "https://localhost:7008";
    public string NativeCallbackScheme => string.Empty;
}

internal sealed class TestInvitationSettings(int lifespanDays = 7) : IInvitationSettings
{
    public int LifespanDays => lifespanDays;
}

internal sealed class TestJwtSettings : IJwtSettings
{
    public string SecretKey { get; init; } = "test-secret-key-at-least-32-chars-long-000";
    public string Issuer => "PerezosoftTests";
    public int ExpiryMinutes => 60;
}

internal sealed class NoopEmailSender : IEmailSender
{
    public Task SendAsync(string to, string subject, string htmlBody,
        IReadOnlyList<EmailInlineImage>? inlineImages = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Test double for the export service — used by controller tests that don't exercise export.</summary>
internal sealed class StubExportService : Perezosoft.Api.Services.ITenantExportService
{
    public bool Called { get; private set; }

    public Task<Uri> ExportAsync(Guid tenantId, Guid actorUserId, CancellationToken cancellationToken = default)
    {
        Called = true;
        return Task.FromResult(new Uri("https://example.test/api/files/stub-token"));
    }
}
