using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Perezosoft.Api.Services;
using Perezosoft.Core.Abstractions;

namespace Perezosoft.Api.Configuration;

/// <summary>
/// Per-epic service-registration extensions (DEBT-3). The flat DI block that used to live in
/// Program.cs is grouped here into cohesive, per-concern methods so Program stays a thin composition
/// root. Lifetimes and registrations are unchanged — this is pure structure.
/// </summary>
public static class ServiceRegistrationExtensions
{
    /// <summary>Auth services.</summary>
    public static IServiceCollection AddAuthServices(this IServiceCollection services)
    {
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        services.AddScoped<ISessionService, SessionService>();
        services.AddScoped<IClaimsExtractor, ClaimsExtractor>();
        // Per-provider email-trust policy (tenant-gated for Microsoft) layered on the fail-closed claim check.
        services.AddSingleton<IProviderEmailTrust, ProviderEmailTrust>();
        services.AddScoped<IPasswordlessService, PasswordlessService>();
        services.AddScoped<ICookieService, CookieService>();
        services.AddScoped<ITokenGenerator, TokenGenerator>();
        services.AddScoped<ITokenHasher, TokenHasher>();
        // Recovery codes are low-entropy, so they get a PEPPERED HMAC (ADM-4) instead of the plain hash the
        // high-entropy tokens use. Singleton: the key is derived from Jwt:Secret once.
        services.AddSingleton<IRecoveryCodeHasher, RecoveryCodeHasher>();
        services.AddSingleton<ILinkTokenService, LinkTokenService>();
        services.AddSingleton<INativeAuthCodeService, NativeAuthCodeService>();
        return services;
    }

    /// <summary>Tenant ("household") management services.</summary>
    public static IServiceCollection AddTenantServices(this IServiceCollection services)
    {
        // Current-tenant accessor — reads the JWT tenant_id claim; drives the global tenant
        // query filter in AppDbContext and is the slice-facing tenancy entry point.
        services.AddHttpContextAccessor();
        // One scoped HttpCurrentTenant backs both interfaces, so entering a tenant via ITenantContext is seen
        // by ICurrentTenant (and thus the AppDbContext filter/stamping) within the same scope.
        services.AddScoped<HttpCurrentTenant>();
        services.AddScoped<ICurrentTenant>(sp => sp.GetRequiredService<HttpCurrentTenant>());
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<HttpCurrentTenant>());
        // Ambient impersonation attribution (LB-ADM-1): the audit log stamps AuditEvent.ImpersonatedBy from
        // the impersonated_by JWT claim on every event — per-request, null for jobs/system contexts.
        services.AddScoped<ICurrentImpersonation, HttpCurrentImpersonation>();
        services.AddScoped<ITenantService, TenantService>();
        services.AddScoped<ITenantInvitationService, TenantInvitationService>();
        // Shared tenant-dissolve sequence (DEBT-7): contributors first, then core WipeDataAsync. Used by
        // both sole-owner leave and solo-owner erasure so the order is defined once (no own transaction).
        services.AddScoped<ITenantDissolutionService, TenantDissolutionService>();

        // Tenant data export (GDPR-1, ADR-011). Assembles core + each contributor's section into a JSON
        // bundle stored via IFileStorage, returned as a signed URL. Owner-gated at the endpoint.
        services.AddScoped<ITenantExportService, TenantExportService>();
        // Account erasure (GDPR-2, ADR-011). "Delete my account" — wipes identity/PII in one audited
        // transaction, honoring the single-owner invariant (transfer-or-dissolve first).
        services.AddScoped<IAccountErasureService, AccountErasureService>();
        return services;
    }

    /// <summary>MFA — authenticator-app TOTP (MFA, ADR-012).</summary>
    public static IServiceCollection AddMfaServices(this IServiceCollection services)
    {
        // MFA — authenticator-app TOTP (MFA-1, ADR-012). Secret encrypted at rest; hashed recovery codes.
        services.AddScoped<IMfaService, MfaService>();
        // Per-user data teardown for account erasure (GDPR-2): each concern that stores user-keyed PII
        // registers an IUserDataContributor so AccountErasureService wipes it without a hard-coded list.
        services.AddScoped<IUserDataContributor, MfaUserDataContributor>();
        services.AddScoped<IUserDataContributor, NotificationUserDataContributor>();
        // MFA login step-up (MFA-2). Signed short-lived challenge + verify → completes the session.
        services.AddSingleton<IMfaChallengeService, MfaChallengeService>();
        services.AddScoped<IMfaLoginService, MfaLoginService>();
        return services;
    }

    /// <summary>Per-user in-app notifications (NOTIFY, ADR-013).</summary>
    public static IServiceCollection AddNotificationServices(this IServiceCollection services)
    {
        // Per-user in-app notifications (NOTIFY-1, ADR-013). NotifyAsync stages an in-app row on the caller's
        // unit of work; the center API reads/marks the caller's own notifications.
        services.AddScoped<INotificationService, NotificationService>();
        // Staff broadcast fan-out (ADMIN-3): enqueued by /api/admin/announce-all, delivered out-of-band.
        services.AddScoped<IOutboxHandler, AdminBroadcastOutboxHandler>();
        return services;
    }

    /// <summary>Platform-staff admin surface (ADMIN, ADR-014).</summary>
    public static IServiceCollection AddPlatformAdminServices(this IServiceCollection services, IConfiguration config)
    {
        // Platform-staff admin surface (ADMIN, ADR-014). Staff is an out-of-band config allowlist; the admin
        // endpoints gate on it per-request. Cross-tenant reads use EnterTenant / non-scoped tables — the global
        // filter is never loosened.
        services.Configure<PlatformAdminSettings>(config.GetSection("Admin"));
        services.AddScoped<IPlatformStaffService, PlatformStaffService>();
        return services;
    }

    /// <summary>RBAC permission seam (ADR-009).</summary>
    public static IServiceCollection AddRbacServices(this IServiceCollection services)
    {
        // RBAC permission seam (ADR-009). Server-side role→permission check behind .RequirePermission(...);
        // resolves the caller's membership and consults the RolePermissions matrix; fails closed.
        services.AddScoped<IPermissionService, PermissionService>();
        return services;
    }

    /// <summary>Billing entitlements, quotas, dunning, checkout + webhook (BILLING, ADR-006).</summary>
    public static IServiceCollection AddBillingServices(this IServiceCollection services)
    {
        // Billing entitlements (ADR-006). Server-side plan gate behind .RequireEntitlement(...); reads the
        // tenant's Subscription projection and fails closed to Free.
        services.AddScoped<IEntitlementService, EntitlementService>();
        // Billing quotas (BILLING-5): seat + metered-usage limits from the plan; used by the invite flow.
        services.AddScoped<IQuotaService, QuotaService>();
        // Billing dunning (BILLING-6): notify the tenant owner on failed-payment/cancel transitions, and a
        // scheduled sweep that nudges once when a paid period lapses without a webhook.
        services.AddScoped<IBillingNotifier, BillingNotifier>();
        services.AddScoped<IScheduledJob, SubscriptionLapseSweepJob>();
        // Billing checkout orchestration (BILLING-2), behind the platform BillingController. The
        // IBillingProvider (Stripe or fake) is registered in AddInfrastructure.
        services.AddScoped<IBillingService, BillingService>();
        // Billing webhook handler (BILLING-3): verify → inbox-dedup → EnterTenant → upsert Subscription.
        services.AddScoped<BillingWebhookHandler>();
        // Billing participates in tenant dissolve (BILLING-7): wipe the Subscription projection + cancel the
        // provider subscription (via the outbox) so a dissolved tenant stops being billed.
        services.AddScoped<ITenantDataContributor, BillingDataContributor>();
        // Metered-usage counters (BILLING-5) participate in dissolve + export (LB-TEN-1) so a dissolved
        // tenant's usage rows don't orphan and the export doesn't silently omit them.
        services.AddScoped<ITenantDataContributor, UsageCounterDataContributor>();
        return services;
    }
}
